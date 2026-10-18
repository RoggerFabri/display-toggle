using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace DisplayToggle;

/// <summary>
/// Symmetric TCP peer link, protocol v2 (see PROTOCOL.md). Listens on a port and dials
/// <c>peerHost:port</c> while it has no active link. Each connection runs a <see cref="PeerSession"/>
/// (handshake, optional shared-secret auth, per-message MACs); this class owns the sockets and the
/// connection management:
///  - a connection only becomes <b>active</b> once established (authenticated; open mode: hello),
///    so a stranger connecting can't tear down a working link, and "connected" means established;
///  - at most 4 pending connections, each closed if not established within 10s;
///  - dialer rule: at most one outbound dial in flight, none while a link is active;
///  - a deterministic tie-break when a second connection is established;
///  - ping/pong + TCP keepalive so a silently dropped link is detected in ~40s and re-dialled;
///  - an 8 KB line cap.
/// </summary>
public sealed class PeerLink : IDisposable
{
    /// <summary>Why the peer can't be linked, beyond plain "offline" (shown in the menu).</summary>
    public enum Problem { None, NeedsUpdate, NotAuthenticated, RequiresSecret }

    /// <summary>Raised when an established peer announces a state change (type="state").</summary>
    public event Action<bool>? PeerStateChanged;
    /// <summary>Raised when the active link comes up or goes down: (connected, endpoint IP, peer hostname).</summary>
    public event Action<bool, string?, string?>? ConnectionChanged;
    /// <summary>Raised when <see cref="CurrentProblem"/> changes.</summary>
    public event Action<Problem>? ProblemChanged;

    private const int MaxLineBytes = 8192;
    private const int PingIntervalMs = 15_000;
    private const int StaleTimeoutMs = 40_000;
    private const int EstablishTimeoutMs = 10_000;
    private const int MaxPending = 4;
    private const int DialRetryMs = 3_000;
    private const int ProblemRetryMs = 30_000;
    private const int ConnectTimeoutMs = 5_000;

    private sealed class Conn
    {
        public required TcpClient Client { get; init; }
        public required NetworkStream Stream { get; init; }
        public required PeerSession Session { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required long Serial { get; init; }
        public string? Endpoint { get; init; }
        public readonly object Gate = new();   // serialises the session and the order lines are queued in
        // Lines waiting to go out, drained by WriterLoop, so a caller (the UI thread, for a toggle)
        // never blocks on a stalled peer's full TCP window.
        public readonly Channel<byte[]> Outbox = Channel.CreateUnbounded<byte[]>(new() { SingleReader = true });
        public bool Established;               // under PeerLink._gate
        public DateTime LastRx = DateTime.UtcNow;
    }

    private readonly object _gate = new();     // never held while taking a Conn.Gate
    private readonly List<Conn> _conns = new();
    private Conn? _active;
    private long _serial;
    private bool _connecting;
    private CancellationTokenSource? _connectCts; // the outbound connect in progress, if any
    private Problem _problem;

    private CancellationTokenSource? _cts;
    private string _peerHost = "";
    private int _peerPort = 51763;
    private int _listenPort = 51763;
    private string _secret = "";
    private string _myId = "";
    private volatile bool _lastOnState = true;

    private readonly string _hostName = Environment.MachineName;

    public bool IsConnected { get { lock (_gate) return _active is not null; } }

    public Problem CurrentProblem { get { lock (_gate) return _problem; } }

    /// <summary>This machine's LAN IPv4 (route-preferred), for showing in the menu.</summary>
    public static string LocalAddress()
    {
        try
        {
            using var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            s.Connect("8.8.8.8", 65530); // no packets sent — just resolves the outbound interface
            if (s.LocalEndPoint is IPEndPoint ep) return ep.Address.ToString();
        }
        catch { }
        try
        {
            foreach (var ip in Dns.GetHostAddresses(Dns.GetHostName()))
                if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                    return ip.ToString();
        }
        catch { }
        return "unknown";
    }

    /// <summary>Start (or restart) with the given peer target, current state, shared secret and our discovery id.</summary>
    public void Start(string peerHost, int port, bool currentOn, string secret, string myId)
        => Start(peerHost, port, port, currentOn, secret, myId);

    /// <summary>As <see cref="Start(string,int,bool,string,string)"/>, listening on a different port than the peer's (tests).</summary>
    internal void Start(string peerHost, int peerPort, int listenPort, bool currentOn, string secret, string myId)
    {
        Stop();
        _peerHost = peerHost ?? "";
        _peerPort = peerPort;
        _listenPort = listenPort;
        _secret = secret ?? "";
        _myId = myId ?? "";
        _lastOnState = currentOn;
        SetProblem(Problem.None);
        var cts = new CancellationTokenSource();
        lock (_gate) _cts = cts;

        _ = Task.Run(() => ListenLoop(cts.Token));
        if (!string.IsNullOrWhiteSpace(_peerHost))
            _ = Task.Run(() => DialLoop(cts.Token));
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        List<Conn> conns;
        lock (_gate)
        {
            cts = _cts;
            _cts = null;
            conns = _conns.ToList();
            _conns.Clear();
            _active = null;
        }
        cts?.Cancel();
        foreach (var c in conns) Close(c);
        cts?.Dispose();
    }

    /// <summary>Broadcast a local state change on the active link (called only for LOCAL toggles).</summary>
    public void SendState(bool on)
    {
        _lastOnState = on;
        Conn? active;
        lock (_gate) active = _active;
        if (active is not null) Send(active, s => s.State(on));
    }

    // ---- listener / dialer --------------------------------------------------

    private async Task ListenLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Any, _listenPort);
                listener.Start();
                while (!token.IsCancellationRequested)
                {
                    var client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                    bool full;
                    lock (_gate) full = _conns.Count(c => !c.Established) >= MaxPending;
                    if (full) { client.Dispose(); continue; }
                    _ = Task.Run(() => RunConnection(client, PeerRole.Listener, token));
                }
            }
            catch (OperationCanceledException) { }
            catch { await DelayQuiet(2000, token).ConfigureAwait(false); } // port busy → retry
            finally { try { listener?.Stop(); } catch { } }
        }
    }

    private async Task DialLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            bool dial;
            lock (_gate)
            {
                // Dialer rule: one outbound attempt (connecting or pending) at a time, none while linked.
                dial = _active is null && !_connecting
                       && !_conns.Any(c => c.Session.Role == PeerRole.Dialer && !c.Established);
                if (dial) _connecting = true;
            }
            if (dial)
            {
                var client = new TcpClient();
                using var connect = CancellationTokenSource.CreateLinkedTokenSource(token);
                lock (_gate) _connectCts = connect;
                try
                {
                    connect.CancelAfter(ConnectTimeoutMs);
                    await client.ConnectAsync(_peerHost, _peerPort, connect.Token).ConfigureAwait(false);
                    _ = Task.Run(() => RunConnection(client, PeerRole.Dialer, token));
                }
                catch { client.Dispose(); /* refused / unreachable / superseded: retried below */ }
                finally { lock (_gate) { _connecting = false; _connectCts = null; } }
            }
            int wait = CurrentProblem == Problem.None ? DialRetryMs : ProblemRetryMs;
            await DelayQuiet(wait, token).ConfigureAwait(false);
        }
    }

    // ---- one connection -----------------------------------------------------

    private async Task RunConnection(TcpClient client, PeerRole role, CancellationToken token)
    {
        Conn conn;
        try
        {
            client.NoDelay = true;
            EnableKeepAlive(client);
            var session = new PeerSession(role, _myId, _hostName, _secret, IsOwnNonce);
            conn = new Conn
            {
                Client = client,
                Stream = client.GetStream(),
                Session = session,
                Cts = CancellationTokenSource.CreateLinkedTokenSource(token),
                Serial = Interlocked.Increment(ref _serial),
                Endpoint = EndpointOf(client),
            };
        }
        catch { client.Dispose(); return; }

        lock (_gate)
        {
            if (token.IsCancellationRequested) { client.Dispose(); return; }
            _conns.Add(conn); // registered before our hello goes out, so IsOwnNonce sees its nonce
        }

        PeerCloseReason? reason = null;
        try
        {
            Send(conn, s => s.Hello(_lastOnState));
            _ = WriterLoop(conn);
            _ = Heartbeat(conn);
            await ReadLoop(conn, r => reason = r).ConfigureAwait(false);
        }
        catch { /* socket closed / cap exceeded → fall through to cleanup */ }
        finally { Closed(conn, reason); }
    }

    private async Task ReadLoop(Conn conn, Action<PeerCloseReason> onClose)
    {
        var buf = new byte[4096];
        using var line = new MemoryStream();
        var token = conn.Cts.Token;
        int n;
        while (!token.IsCancellationRequested && (n = await conn.Stream.ReadAsync(buf, token).ConfigureAwait(false)) > 0)
        {
            conn.LastRx = DateTime.UtcNow;
            for (int i = 0; i < n; i++)
            {
                byte b = buf[i];
                if (b != (byte)'\n')
                {
                    if (line.Length >= MaxLineBytes) throw new InvalidDataException("peer line exceeds cap");
                    line.WriteByte(b);
                    continue;
                }
                PeerSession.Result r;
                lock (conn.Gate)
                {
                    r = conn.Session.Receive(line.GetBuffer().AsSpan(0, (int)line.Length));
                    foreach (var reply in r.Send) Write(conn, reply);
                }
                line.SetLength(0);
                if (r.Close is PeerCloseReason why) { onClose(why); return; }
                if (r.Established) OnEstablished(conn);
                if (r.PeerState is bool on) PeerStateChanged?.Invoke(on);
            }
        }
    }

    private async Task Heartbeat(Conn conn)
    {
        var token = conn.Cts.Token;
        try
        {
            await Task.Delay(EstablishTimeoutMs, token).ConfigureAwait(false);
            bool established;
            lock (_gate) established = conn.Established;
            if (!established) { Close(conn); return; } // not established within 10s

            while (!token.IsCancellationRequested)
            {
                await Task.Delay(PingIntervalMs, token).ConfigureAwait(false);
                if ((DateTime.UtcNow - conn.LastRx).TotalMilliseconds > StaleTimeoutMs) { Close(conn); return; }
                Send(conn, s => s.Ping());
            }
        }
        catch (OperationCanceledException) { }
    }

    private void OnEstablished(Conn conn)
    {
        Conn? loser = null;
        bool raise;
        lock (_gate)
        {
            if (!_conns.Contains(conn)) return;
            conn.Established = true;
            if (_active is null || ShouldReplace(_active, conn))
            {
                loser = _active;
                _active = conn;
                raise = true;
            }
            else
            {
                loser = conn;
                raise = false;
            }
            // An outbound connect still in flight is now pointless; if it completed later it would
            // only start a tie-break and replace this link. The peer never saw it, so cancel it.
            try { _connectCts?.Cancel(); } catch (ObjectDisposedException) { }
        }
        SetProblem(Problem.None);
        if (loser is not null) Close(loser);
        if (raise) ConnectionChanged?.Invoke(true, conn.Endpoint, conn.Session.PeerHost);
    }

    /// <summary>Tie-break (PROTOCOL.md "Connection management"): does <paramref name="candidate"/> beat the active link?</summary>
    private bool ShouldReplace(Conn active, Conn candidate)
        => PickWinner(active.Session.Role, active.Serial, candidate.Session.Role, candidate.Serial,
                      _myId, candidate.Session.PeerId) == 1;

    /// <summary>0 = keep the first connection, 1 = the second. Pure, for tests.</summary>
    internal static int PickWinner(PeerRole roleA, long serialA, PeerRole roleB, long serialB, string myId, string? peerId)
    {
        // Both opened by the same side: by the dialer rule the older one is stale → newer wins.
        if (roleA == roleB || string.IsNullOrEmpty(myId) || string.IsNullOrEmpty(peerId))
            return serialB > serialA ? 1 : 0;
        // One dialled each way: keep the one dialled by the side whose id sorts lower.
        bool iAmLower = string.Compare(myId, peerId, StringComparison.OrdinalIgnoreCase) < 0;
        PeerRole keep = iAmLower ? PeerRole.Dialer : PeerRole.Listener;
        return roleB == keep ? 1 : 0;
    }

    private void Closed(Conn conn, PeerCloseReason? reason)
    {
        bool wasActive;
        Conn? promoted = null;
        lock (_gate)
        {
            if (!_conns.Remove(conn)) { Close(conn); return; } // already torn down by Stop
            wasActive = ReferenceEquals(_active, conn);
            if (wasActive)
            {
                promoted = _conns.Where(c => c.Established).MaxBy(c => c.Serial);
                _active = promoted;
            }
        }
        Close(conn);

        var problem = reason switch
        {
            PeerCloseReason.NeedsUpdate => Problem.NeedsUpdate,
            PeerCloseReason.AuthFailed => Problem.NotAuthenticated,
            PeerCloseReason.PeerRequiresSecret => Problem.RequiresSecret,
            // Secret mode, the peer said hello but the link closed (timed out, or the open side hung up
            // on our auth) before it proved the secret: mixed mode or a wrong secret.
            null when !conn.Established && conn.Session.SecretMode && conn.Session.PeerId is not null
                => Problem.NotAuthenticated,
            _ => (Problem?)null,
        };
        if (problem is Problem p) SetProblem(p);

        if (wasActive)
        {
            if (promoted is not null) ConnectionChanged?.Invoke(true, promoted.Endpoint, promoted.Session.PeerHost);
            else ConnectionChanged?.Invoke(false, null, null);
        }
    }

    private bool IsOwnNonce(string nonce)
    {
        lock (_gate) return _conns.Any(c => c.Session.MyNonce == nonce);
    }

    private void SetProblem(Problem p)
    {
        bool changed;
        lock (_gate) { changed = _problem != p; _problem = p; }
        if (changed) ProblemChanged?.Invoke(p);
    }

    // ---- I/O helpers ----------------------------------------------------------

    private static void Send(Conn conn, Func<PeerSession, string> make)
    {
        try { lock (conn.Gate) Write(conn, make(conn.Session)); }
        catch { /* not established yet, or the socket is gone: the read loop tears it down */ }
    }

    // Caller holds conn.Gate, so a sequenced message is built and queued in one step (seq order = wire order).
    private static void Write(Conn conn, string line)
        => conn.Outbox.Writer.TryWrite(Encoding.UTF8.GetBytes(line + "\n"));

    private static async Task WriterLoop(Conn conn)
    {
        try
        {
            await foreach (var bytes in conn.Outbox.Reader.ReadAllAsync(conn.Cts.Token).ConfigureAwait(false))
                await conn.Stream.WriteAsync(bytes, conn.Cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { Close(conn); } // write failed: the socket is gone, let the read loop tear it down
    }

    private static void Close(Conn conn)
    {
        conn.Outbox.Writer.TryComplete();
        try { conn.Cts.Cancel(); } catch { }
        try { conn.Client.Close(); } catch { }
    }

    private static void EnableKeepAlive(TcpClient c)
    {
        try
        {
            c.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            c.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 15);
            c.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            c.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch { /* older stacks: app-level ping still covers it */ }
    }

    private static string? EndpointOf(TcpClient c)
    {
        try
        {
            if (c.Client.RemoteEndPoint is IPEndPoint ep)
            {
                var addr = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
                return addr.ToString(); // just the peer IP — the source port is ephemeral noise
            }
        }
        catch { }
        return null;
    }

    private static async Task DelayQuiet(int ms, CancellationToken token)
    {
        try { await Task.Delay(ms, token).ConfigureAwait(false); } catch { }
    }

    public void Dispose() => Stop();
}
