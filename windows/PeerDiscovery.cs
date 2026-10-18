using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace DisplayToggle;

/// <summary>
/// mDNS / DNS-SD discovery (see docs/specs/peer-discovery.md).
///
/// Advertise: dnsapi.dll <c>DnsServiceRegister</c> (Windows 10 1809+) of
/// <c>&lt;MachineName&gt;._display-toggle._tcp.local</c> on the peer port, TXT <c>v=1, id, os=win</c>.
///
/// Discover: every <see cref="QueryIntervalMs"/> a legacy-unicast PTR query (RFC 6762 §6.7 — sent
/// from an ephemeral port, so responders answer straight back to it) on each up IPv4 interface;
/// the reply carries PTR + SRV + TXT + A. dnsapi's own browse/resolve can't be relied on: its
/// queries ask for unicast replies to port 5353, and when Bonjour (or a browser) also binds 5353
/// those replies never reach the DNS Client. A dnsapi browse is still kept running because the
/// multicast TTL-0 goodbyes *do* arrive through it, so a peer that quits vanishes at once rather
/// than after <see cref="PeerTable.MissedCyclesToExpire"/> missed cycles.
///
/// Peers are never probed over TCP: PeerLink adopts every inbound connection, so a probe would
/// break a live link. <see cref="PeersChanged"/> is raised on background threads.
/// </summary>
public sealed class PeerDiscovery : IDisposable
{
    public const string ServiceType = "_display-toggle._tcp.local";

    /// <summary>Raised (on a background thread) whenever the peer list changes.</summary>
    public event Action? PeersChanged;

    /// <summary>Optional diagnostics sink (the <c>--discover</c> CLI sets it).</summary>
    public static Action<string>? Trace;

    private const int QueryIntervalMs = 15_000;
    private const int ReplyWindowMs = 2_000;
    private const uint DNS_REQUEST_PENDING = 9506;
    private const uint DNS_QUERY_REQUEST_VERSION1 = 1;
    private const ushort DNS_TYPE_PTR = 12;

    // Lock order: _dnsLock, then _gate — and dnsapi is never called while holding _gate. dnsapi's
    // cancel/deregister wait for an in-flight callback to return, and the callbacks take _gate, so
    // calling dnsapi under _gate deadlocks (it froze the tray menu, whose Peers read waited on _gate).
    private readonly object _gate = new();     // table + run state; taken by dnsapi callbacks
    private readonly object _dnsLock = new();  // serialises dnsapi register/browse calls; never taken by callbacks
    private readonly PeerTable _table = new();
    private IReadOnlyList<DiscoveredPeer> _peers = Array.Empty<DiscoveredPeer>(); // published under _gate

    // Delegates are held in fields so the GC can't collect them while dnsapi holds the pointers.
    private readonly RegisterCallback _onRegister;
    private readonly BrowseCallback _onBrowse;

    private readonly IntPtr _browseName = Marshal.StringToHGlobalUni(ServiceType);
    // dnsapi handles, guarded by _dnsLock.
    private IntPtr _browseCancel;
    private IntPtr _instance;
    private IntPtr _registration;   // DNS_SERVICE_REGISTER_REQUEST, unmanaged (see Register_DnsLocked)
    private bool _registered;

    private CancellationTokenSource? _cts;
    private System.Threading.Timer? _netDebounce;
    private readonly SemaphoreSlim _queryNow = new(0);
    private string _instanceId = "";
    private int _port;
    private bool _advertise;
    private bool _running;
    private bool _disposed;

    public PeerDiscovery()
    {
        _onRegister = OnRegister;
        _onBrowse = OnBrowse;
    }

    /// <summary>Start advertising (optional) + discovering. No-op if already running with the same settings.</summary>
    public void Start(string instanceId, int port, bool advertise = true)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_running && _instanceId == instanceId && _port == port && _advertise == advertise) return;
        }
        Stop();
        lock (_dnsLock)
        {
            lock (_gate)
            {
                _instanceId = instanceId;
                _port = port;
                _advertise = advertise;
                _running = true;
                _cts = new CancellationTokenSource();
                var token = _cts.Token;
                _ = Task.Run(() => QueryLoop(token), token);
            }
            if (advertise) Register_DnsLocked();
            Browse_DnsLocked();
        }
        RefreshNetworkCategory();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
    }

    public void Stop()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        bool changed;
        lock (_gate)
        {
            _running = false;
            _cts?.Cancel(); _cts?.Dispose(); _cts = null;
            _netDebounce?.Dispose(); _netDebounce = null;
            changed = _table.Clear();
            if (changed) Publish_NoLock();
        }
        lock (_dnsLock)
        {
            CancelBrowse_DnsLocked();
            Deregister_DnsLocked();
        }
        if (changed) PeersChanged?.Invoke();
    }

    /// <summary>Snapshot of the resolved, non-self peers, sorted by name. Lock-free, so the UI thread never waits on discovery.</summary>
    public IReadOnlyList<DiscoveredPeer> Peers => Volatile.Read(ref _peers);

    private void Publish_NoLock() => Volatile.Write(ref _peers, _table.Peers);

    /// <summary>
    /// Cached <see cref="OnPublicNetwork"/>, refreshed off the UI thread at start and on network
    /// changes, so opening the menu never waits on the COM call.
    /// </summary>
    public bool IsPublicNetwork => Volatile.Read(ref _isPublic);
    private bool _isPublic;

    private void RefreshNetworkCategory() => _ = Task.Run(() => Volatile.Write(ref _isPublic, OnPublicNetwork()));

    /// <summary>True if any connected network is categorised Public (Windows then blocks the peer port).</summary>
    public static bool OnPublicNetwork()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B"));
            if (type is null) return false;
            dynamic nlm = Activator.CreateInstance(type)!;
            foreach (dynamic net in nlm.GetNetworks(1 /* NLM_ENUM_NETWORK_CONNECTED */))
                if ((int)net.GetCategory() == 0 /* NLM_NETWORK_CATEGORY_PUBLIC */) return true;
        }
        catch { }
        return false;
    }

    // ---- discover: legacy-unicast queries ----------------------------------

    private async Task QueryLoop(CancellationToken token)
    {
        var query = MdnsPacket.BuildPtrQuery(ServiceType);
        while (!token.IsCancellationRequested)
        {
            bool changed;
            lock (_gate) { changed = _table.NextCycle(); if (changed) Publish_NoLock(); }
            if (changed) PeersChanged?.Invoke();

            var locals = LocalSubnets();
            await Task.WhenAll(locals.Select(l => QueryOn(l.Address, query, locals, token))).ConfigureAwait(false);

            try { await _queryNow.WaitAsync(QueryIntervalMs - ReplyWindowMs, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Cut the current wait short (at most one early query pending).</summary>
    private void QueryNow()
    {
        lock (_queryNow) { if (_queryNow.CurrentCount == 0) _queryNow.Release(); }
    }

    private async Task QueryOn(IPAddress local, byte[] query, IReadOnlyList<(IPAddress Address, int Prefix)> locals, CancellationToken token)
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Bind(new IPEndPoint(local, 0));
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
            await s.SendToAsync(query, SocketFlags.None, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353), token).ConfigureAwait(false);

            using var window = CancellationTokenSource.CreateLinkedTokenSource(token);
            window.CancelAfter(ReplyWindowMs);
            var buf = new byte[9000];
            while (true)
            {
                SocketReceiveFromResult r;
                try { r = await s.ReceiveFromAsync(buf, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), window.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                string src = ((IPEndPoint)r.RemoteEndPoint).Address.ToString();
                HandleReply(buf.AsSpan(0, r.ReceivedBytes), src, locals);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            Trace?.Invoke($"query on {local} failed: {e.Message}");
        }
    }

    private void HandleReply(ReadOnlySpan<byte> reply, string source, IReadOnlyList<(IPAddress Address, int Prefix)> locals)
    {
        if (Trace is not null) Trace($"reply from {source}: {Convert.ToHexString(reply)}");
        var (answers, ptrs) = MdnsPacket.Parse(reply, ServiceType);
        bool changed = false;
        lock (_gate)
        {
            if (!_running) return;
            foreach (var (instance, ttl) in ptrs)
                if (ttl == 0) changed |= _table.Observe(PeerDiscoveryLogic.InstanceKey(instance), 0);
            foreach (var a in answers)
            {
                string name = PeerDiscoveryLogic.InstanceKey(a.Instance);
                a.Txt.TryGetValue("id", out var id);
                a.Txt.TryGetValue("os", out var os);
                bool self = PeerDiscoveryLogic.IsSelf(id, name, _instanceId, Environment.MachineName);
                string? ip = self ? null : PeerDiscoveryLogic.PickAddress(a.Addresses.Append(source), locals);
                Trace?.Invoke($"answer {a.Instance}: port={a.Port} A=[{string.Join(",", a.Addresses)}] id={id} os={os} → {(self ? "self" : ip ?? "no usable address")}");
                if (!self && ip is null) continue;
                changed |= _table.Update(name, self ? null : new DiscoveredPeer(id ?? "", name, ip!, a.Port, os ?? ""));
            }
            if (changed) Publish_NoLock();
        }
        if (changed) PeersChanged?.Invoke();
    }

    /// <summary>Up, multicast-capable IPv4 interface addresses (default-route interface first), no link-local.</summary>
    private static IReadOnlyList<(IPAddress Address, int Prefix)> LocalSubnets()
    {
        var list = new List<(IPAddress Address, int Prefix)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || !nic.SupportsMulticast
                    || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                    if (u.Address.AddressFamily == AddressFamily.InterNetwork && !PeerDiscoveryLogic.IsLinkLocal(u.Address))
                        list.Add((u.Address, u.PrefixLength));
            }
        }
        catch { }
        string primary = PeerLink.LocalAddress();
        return list.OrderBy(l => l.Address.ToString() == primary ? 0 : 1).ToList();
    }

    private void OnNetworkChanged(object? sender, EventArgs e)
    {
        // Addresses settle over a few seconds (Wi-Fi join, VPN up); re-advertise and re-query then.
        lock (_gate)
        {
            if (!_running) return;
            _netDebounce?.Dispose();
            _netDebounce = new System.Threading.Timer(_ =>
            {
                lock (_dnsLock)
                {
                    bool advertise;
                    lock (_gate)
                    {
                        if (!_running) return;
                        advertise = _advertise;
                    }
                    if (advertise) { Deregister_DnsLocked(); Register_DnsLocked(); }
                    CancelBrowse_DnsLocked();
                    Browse_DnsLocked();
                }
                RefreshNetworkCategory();
                QueryNow();
            }, null, 3000, Timeout.Infinite);
        }
    }

    // ---- advertise ----------------------------------------------------------

    private void Register_DnsLocked()
    {
        string host = Environment.MachineName;
        IntPtr ip4p = IntPtr.Zero;
        try
        {
            if (IPAddress.TryParse(PeerLink.LocalAddress(), out var local) && local.AddressFamily == AddressFamily.InterNetwork)
            {
                ip4p = Marshal.AllocHGlobal(4);
                Marshal.Copy(local.GetAddressBytes(), 0, ip4p, 4);
            }
            _instance = DnsServiceConstructInstance($"{host}.{ServiceType}", $"{host}.local",
                ip4p, IntPtr.Zero, (ushort)_port, 0, 0, 3,
                new[] { "v", "id", "os" }, new[] { "1", _instanceId, "win" });
        }
        finally { if (ip4p != IntPtr.Zero) Marshal.FreeHGlobal(ip4p); }
        if (_instance == IntPtr.Zero) { Trace?.Invoke("DnsServiceConstructInstance failed"); return; }

        // dnsapi completes (de)registration asynchronously and doesn't document whether it copies the
        // request, so the request lives in unmanaged memory and is never moved or freed while in use.
        _registration = Alloc(new DNS_SERVICE_REGISTER_REQUEST
        {
            Version = DNS_QUERY_REQUEST_VERSION1,
            pServiceInstance = _instance,
            pRegisterCompletionCallback = Marshal.GetFunctionPointerForDelegate(_onRegister),
        });
        uint rc = DnsServiceRegister(_registration, IntPtr.Zero);
        _registered = rc == DNS_REQUEST_PENDING;
        if (!_registered) Trace?.Invoke($"DnsServiceRegister rc={rc}");
    }

    private void Deregister_DnsLocked()
    {
        if (_registered) { try { DnsServiceDeRegister(_registration, IntPtr.Zero); } catch { } }
        _registered = false;
        // Deregistration completes asynchronously and may still read the request and instance, so
        // neither is freed — a few hundred bytes per network change / port change.
        _registration = IntPtr.Zero;
        _instance = IntPtr.Zero;
    }

    private void OnRegister(uint status, IntPtr context, IntPtr instance)
    {
        Trace?.Invoke($"register callback status={status}");
        if (instance != IntPtr.Zero) DnsServiceFreeInstance(instance);
    }

    // ---- goodbyes: dnsapi browse --------------------------------------------

    private void Browse_DnsLocked()
    {
        // Like the registration, the request and the DNS_SERVICE_CANCEL stay in unmanaged memory and
        // are never freed once dnsapi has them: the cancel completes asynchronously (the 1223
        // callback comes later). A browse is only restarted on a network change, so this is tiny.
        var req = Alloc(new DNS_SERVICE_BROWSE_REQUEST
        {
            Version = DNS_QUERY_REQUEST_VERSION1,
            QueryName = _browseName,
            pBrowseCallback = Marshal.GetFunctionPointerForDelegate(_onBrowse),
        });
        _browseCancel = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(_browseCancel, IntPtr.Zero);
        if (DnsServiceBrowse(req, _browseCancel) != DNS_REQUEST_PENDING)
        {
            Marshal.FreeHGlobal(_browseCancel); // never accepted → nothing references them
            Marshal.FreeHGlobal(req);
            _browseCancel = IntPtr.Zero;
        }
    }

    private void CancelBrowse_DnsLocked()
    {
        if (_browseCancel == IntPtr.Zero) return;
        try { DnsServiceBrowseCancel(_browseCancel); } catch { }
        _browseCancel = IntPtr.Zero; // not freed: see Browse_DnsLocked
    }

    private void OnBrowse(uint status, IntPtr context, IntPtr records)
    {
        bool changed = false, announced = false;
        try
        {
            if (status != 0) return; // 1223 = ERROR_CANCELLED on a restart
            lock (_gate)
            {
                if (!_running) return;
                for (IntPtr p = records; p != IntPtr.Zero; p = Marshal.ReadIntPtr(p, 0))
                {
                    // DNS_RECORD: pNext, pName, wType, wDataLength, Flags, dwTtl, dwReserved, Data
                    if ((ushort)Marshal.ReadInt16(p, 2 * IntPtr.Size) != DNS_TYPE_PTR) continue;
                    uint ttl = (uint)Marshal.ReadInt32(p, 2 * IntPtr.Size + 8);
                    string? instance = Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, 2 * IntPtr.Size + 16));
                    if (string.IsNullOrEmpty(instance)) continue;
                    string key = PeerDiscoveryLogic.InstanceKey(instance);
                    Trace?.Invoke($"browse PTR {instance} (key \"{key}\") ttl={ttl}");
                    if (ttl == 0) changed |= _table.Observe(key, 0);
                    // dnsapi repeats every answer (ours included) many times a second — only an
                    // instance we've never had an answer for is worth an early query.
                    else if (!_table.Contains(key)
                             && !string.Equals(key, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                        announced = true;
                }
                if (changed) Publish_NoLock();
            }
        }
        finally
        {
            if (records != IntPtr.Zero) DnsRecordListFree(records, 1 /* DnsFreeRecordList */);
        }
        if (changed) PeersChanged?.Invoke();
        if (announced) QueryNow(); // something new announced itself — fetch its details now
    }

    public void Dispose()
    {
        Stop();
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // _browseName is not freed: the browse cancel completes asynchronously and dnsapi may still
        // read the request (which points at it). It's a few bytes, and Dispose runs at exit.
    }

    private static IntPtr Alloc<T>(T value) where T : struct
    {
        IntPtr p = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
        Marshal.StructureToPtr(value, p, false);
        return p;
    }

    // ---- dnsapi.dll ---------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void RegisterCallback(uint status, IntPtr context, IntPtr instance);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate void BrowseCallback(uint status, IntPtr context, IntPtr records);

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_SERVICE_REGISTER_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr pServiceInstance;
        public IntPtr pRegisterCompletionCallback;
        public IntPtr pQueryContext;
        public IntPtr hCredentials;
        public int unicastEnabled;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DNS_SERVICE_BROWSE_REQUEST
    {
        public uint Version;
        public uint InterfaceIndex;
        public IntPtr QueryName;
        public IntPtr pBrowseCallback;
        public IntPtr pQueryContext;
    }

    [DllImport("dnsapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DnsServiceConstructInstance(string serviceName, string hostName, IntPtr ip4, IntPtr ip6,
        ushort port, ushort priority, ushort weight, uint propertyCount, string[] keys, string[] values);
    [DllImport("dnsapi.dll")] private static extern void DnsServiceFreeInstance(IntPtr instance);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceRegister(IntPtr request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceDeRegister(IntPtr request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceBrowse(IntPtr request, IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern uint DnsServiceBrowseCancel(IntPtr cancel);
    [DllImport("dnsapi.dll")] private static extern void DnsRecordListFree(IntPtr records, int freeType);
}
