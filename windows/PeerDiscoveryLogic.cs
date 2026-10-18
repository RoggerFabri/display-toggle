using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace DisplayToggle;

/// <summary>A peer instance found via DNS-SD. <see cref="Id"/> is its TXT <c>id</c> (may be empty).</summary>
public sealed record DiscoveredPeer(string Id, string Name, string Ip, int Port, string Os);

/// <summary>
/// Pure decision logic for peer discovery — no P/Invoke, no sockets, no UI — so it can be unit
/// tested. The Mac app implements the same rules (docs/specs/peer-discovery.md).
/// </summary>
internal static class PeerDiscoveryLogic
{
    /// <summary>
    /// Is a discovered instance this app? By TXT id when it has one, else by instance name. Both
    /// compare case-insensitively: ids are UUIDs (Mac uppercase, .NET lowercase) and instance names
    /// are DNS labels, which are case-insensitive.
    /// </summary>
    public static bool IsSelf(string? id, string name, string ourId, string ourName)
        => !string.IsNullOrEmpty(id)
            ? string.Equals(id, ourId, StringComparison.OrdinalIgnoreCase)
            : string.Equals(name, ourName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Pick the address to dial from a peer's candidates: the first candidate on the same subnet as
    /// a local interface (locals checked in order, so pass the default-route interface first), else
    /// the first candidate that isn't link-local. 169.254.x.x is never picked; empty → null.
    /// </summary>
    public static string? PickAddress(IEnumerable<string> candidates, IEnumerable<(IPAddress Address, int Prefix)> locals)
    {
        var usable = candidates
            .Select(c => IPAddress.TryParse(c, out var a) ? a : null)
            .Where(a => a is { AddressFamily: AddressFamily.InterNetwork } && !IsLinkLocal(a) && !IPAddress.IsLoopback(a))
            .Select(a => a!)
            .ToList();
        foreach (var (local, prefix) in locals)
        {
            if (local.AddressFamily != AddressFamily.InterNetwork) continue;
            var hit = usable.FirstOrDefault(a => SameSubnet(a, local, prefix));
            if (hit is not null) return hit.ToString();
        }
        return usable.FirstOrDefault()?.ToString();
    }

    /// <summary>
    /// Which discovered peer (if any) to adopt as the link target right now:
    /// never while connected; follow the chosen PeerId to a new ip/port; auto-pick the only peer
    /// when nothing is configured. A manually entered peer (PeerId empty) is never replaced, and
    /// nothing is adopted when the user chose None (<paramref name="standalone"/>).
    /// </summary>
    public static DiscoveredPeer? DecideAdopt(bool connected, string peerHost, int peerPort, string peerId,
                                              IReadOnlyList<DiscoveredPeer> peers, bool standalone = false)
    {
        if (connected || standalone) return null;
        if (!string.IsNullOrEmpty(peerId))
        {
            var p = peers.FirstOrDefault(x => string.Equals(x.Id, peerId, StringComparison.OrdinalIgnoreCase));
            if (p is null || (p.Ip == peerHost && p.Port == peerPort)) return null;
            return p;
        }
        if (string.IsNullOrWhiteSpace(peerHost) && peers.Count == 1) return peers[0];
        return null;
    }

    /// <summary>Checkmark in "Choose peer": by PeerId when set, otherwise by host + port.</summary>
    public static bool IsCurrent(DiscoveredPeer p, string peerHost, int peerPort, string peerId)
        => !string.IsNullOrEmpty(peerId)
            ? string.Equals(p.Id, peerId, StringComparison.OrdinalIgnoreCase)
            : p.Ip == peerHost && p.Port == peerPort;

    public static bool IsLinkLocal(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b.Length == 4 && b[0] == 169 && b[1] == 254;
    }

    public static bool SameSubnet(IPAddress a, IPAddress b, int prefix)
    {
        if (prefix is <= 0 or > 32) return false;
        uint ua = BinaryPrimitives.ReadUInt32BigEndian(a.GetAddressBytes());
        uint ub = BinaryPrimitives.ReadUInt32BigEndian(b.GetAddressBytes());
        uint mask = prefix == 32 ? uint.MaxValue : ~(uint.MaxValue >> prefix);
        return (ua & mask) == (ub & mask);
    }

    public const string ServiceSuffix = "._display-toggle._tcp.local";

    /// <summary>
    /// Canonical instance name: the table key and the menu label. The two sources format an
    /// instance differently — our reply parser escapes dots inside the label
    /// (<c>J\. Example Mac._display-toggle._tcp.local</c>), while dnsapi may hand back the raw label
    /// (<c>J. Example Mac._…</c>) or <c>\032</c>-style escapes — so strip the service suffix, then
    /// undo <c>\.</c>, <c>\\</c> and <c>\DDD</c>. All three forms map to <c>J. Example Mac</c>.
    /// </summary>
    public static string InstanceKey(string fqdn)
    {
        string head = fqdn.TrimEnd('.');
        head = head.EndsWith(ServiceSuffix, StringComparison.OrdinalIgnoreCase)
            ? head[..^ServiceSuffix.Length]
            : head[..FirstUnescapedDot(head)]; // some other service: keep the first label
        return Unescape(head);
    }

    /// <summary>Display name of an instance — the same as its canonical key.</summary>
    public static string InstanceLabel(string fqdn) => InstanceKey(fqdn);

    private static int FirstUnescapedDot(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\') { i++; continue; }
            if (s[i] == '.') return i;
        }
        return s.Length;
    }

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '\\' && i + 1 < s.Length)
            {
                if (i + 3 < s.Length && char.IsAsciiDigit(s[i + 1]) && char.IsAsciiDigit(s[i + 2]) && char.IsAsciiDigit(s[i + 3]))
                {
                    sb.Append((char)int.Parse(s.AsSpan(i + 1, 3)));
                    i += 3;
                }
                else sb.Append(s[++i]);
                continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }
}

/// <summary>
/// The discovered-instance table, keyed by canonical instance name (<see cref="PeerDiscoveryLogic.InstanceKey"/>). Not thread-safe (the caller locks).
/// Repeated sightings of one instance collapse into one entry; a TTL-0 PTR removes it; an
/// entry not seen for more than <see cref="MissedCyclesToExpire"/> query cycles expires.
/// Mutators return true when the visible (non-self, resolved) peer list changed.
/// </summary>
internal sealed class PeerTable
{
    private sealed class Entry
    {
        public int LastCycle;
        public DiscoveredPeer? Peer;   // null until resolved, and for our own instance
    }

    public int MissedCyclesToExpire { get; }
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private int _cycle;

    public PeerTable(int missedCyclesToExpire = 2) => MissedCyclesToExpire = missedCyclesToExpire;

    public int Count => _entries.Count;

    public bool Contains(string instance) => _entries.ContainsKey(instance);

    public IReadOnlyList<DiscoveredPeer> Peers
        => _entries.Values.Where(e => e.Peer is not null).Select(e => e.Peer!)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    /// <summary>A PTR sighting. TTL 0 = goodbye (removes the entry).</summary>
    public bool Observe(string instance, uint ttl)
    {
        if (ttl == 0) return _entries.Remove(instance, out var gone) && gone.Peer is not null;
        if (!_entries.TryGetValue(instance, out var e)) _entries[instance] = e = new Entry();
        e.LastCycle = _cycle;
        return false;
    }

    /// <summary>Full details for an instance (also counts as a sighting). <paramref name="peer"/> null = self.</summary>
    public bool Update(string instance, DiscoveredPeer? peer)
    {
        if (!_entries.TryGetValue(instance, out var e)) _entries[instance] = e = new Entry();
        e.LastCycle = _cycle;
        bool changed = e.Peer != peer;
        e.Peer = peer;
        return changed;
    }

    /// <summary>Start a new query cycle; drop entries missed for more than N cycles.</summary>
    public bool NextCycle()
    {
        _cycle++;
        bool changed = false;
        foreach (var (key, e) in _entries.ToList())
        {
            if (_cycle - e.LastCycle <= MissedCyclesToExpire) continue;
            _entries.Remove(key);
            changed |= e.Peer is not null;
        }
        return changed;
    }

    public bool Clear()
    {
        bool changed = _entries.Values.Any(e => e.Peer is not null);
        _entries.Clear();
        return changed;
    }
}

/// <summary>One service instance assembled from a DNS-SD reply.</summary>
internal sealed record ServiceAnswer(string Instance, int Port, IReadOnlyDictionary<string, string> Txt, IReadOnlyList<string> Addresses);

/// <summary>Minimal mDNS packet codec: a PTR query, and PTR/SRV/TXT/A extraction from replies.</summary>
internal static class MdnsPacket
{
    private const ushort TypeA = 1, TypePtr = 12, TypeTxt = 16, TypeSrv = 33;

    public static byte[] BuildPtrQuery(string service, ushort id = 0)
    {
        var ms = new MemoryStream();
        Span<byte> hdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(hdr, id);
        BinaryPrimitives.WriteUInt16BigEndian(hdr[4..], 1); // QDCOUNT
        ms.Write(hdr);
        WriteName(ms, service);
        Span<byte> q = stackalloc byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(q, TypePtr);
        BinaryPrimitives.WriteUInt16BigEndian(q[2..], 1);    // IN, QM
        ms.Write(q);
        return ms.ToArray();
    }

    /// <summary>Instances of <paramref name="service"/> in a reply, plus every PTR (instance, ttl) seen.</summary>
    public static (List<ServiceAnswer> Answers, List<(string Instance, uint Ttl)> Ptrs) Parse(ReadOnlySpan<byte> buf, string service)
    {
        var ptrs = new List<(string, uint)>();
        var srv = new Dictionary<string, (int Port, string Target)>(StringComparer.OrdinalIgnoreCase);
        var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var a = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (buf.Length < 12) return (new(), ptrs);
            int qd = BinaryPrimitives.ReadUInt16BigEndian(buf[4..]);
            int rr = BinaryPrimitives.ReadUInt16BigEndian(buf[6..]) + BinaryPrimitives.ReadUInt16BigEndian(buf[8..])
                   + BinaryPrimitives.ReadUInt16BigEndian(buf[10..]);
            int off = 12;
            for (int i = 0; i < qd; i++) { ReadName(buf, ref off); off += 4; }
            for (int i = 0; i < rr; i++)
            {
                string name = ReadName(buf, ref off);
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(buf[off..]);
                uint ttl = BinaryPrimitives.ReadUInt32BigEndian(buf[(off + 4)..]);
                int len = BinaryPrimitives.ReadUInt16BigEndian(buf[(off + 8)..]);
                off += 10;
                int rdata = off;
                if (rdata + len > buf.Length) break;
                switch (type)
                {
                    case TypePtr when string.Equals(name, service, StringComparison.OrdinalIgnoreCase):
                        int p = rdata; ptrs.Add((ReadName(buf, ref p), ttl));
                        break;
                    case TypeSrv when len >= 7:
                        int t = rdata + 6;
                        srv[name] = (BinaryPrimitives.ReadUInt16BigEndian(buf[(rdata + 4)..]), ReadName(buf, ref t));
                        break;
                    case TypeTxt:
                        var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        for (int j = rdata; j < rdata + len;)
                        {
                            int l = buf[j];
                            if (j + 1 + l > rdata + len) break;
                            string s = Encoding.UTF8.GetString(buf.Slice(j + 1, l));
                            int eq = s.IndexOf('=');
                            if (s.Length > 0) kv.TryAdd(eq < 0 ? s : s[..eq], eq < 0 ? "" : s[(eq + 1)..]); // first wins (RFC 6763 §6.4)
                            j += 1 + l;
                        }
                        txt[name] = kv;
                        break;
                    case TypeA when len == 4:
                        if (!a.TryGetValue(name, out var list)) a[name] = list = new();
                        list.Add(new IPAddress(buf.Slice(rdata, 4)).ToString());
                        break;
                }
                off = rdata + len;
            }
        }
        catch (Exception e) when (e is ArgumentOutOfRangeException or IndexOutOfRangeException) { /* truncated */ }

        var answers = new List<ServiceAnswer>();
        foreach (var (instance, ttl) in ptrs)
        {
            if (ttl == 0 || !srv.TryGetValue(instance, out var s)) continue;
            answers.Add(new ServiceAnswer(instance, s.Port,
                txt.TryGetValue(instance, out var kv) ? kv : new Dictionary<string, string>(),
                a.TryGetValue(s.Target, out var addrs) ? addrs : new List<string>()));
        }
        return (answers, ptrs);
    }

    private static void WriteName(Stream ms, string name)
    {
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var b = Encoding.UTF8.GetBytes(label);
            ms.WriteByte((byte)b.Length);
            ms.Write(b);
        }
        ms.WriteByte(0);
    }

    private static string ReadName(ReadOnlySpan<byte> buf, ref int off)
    {
        var labels = new List<string>();
        int pos = off, jumps = 0;
        bool jumped = false;
        while (true)
        {
            int n = buf[pos];
            if (n == 0) { pos++; break; }
            if ((n & 0xC0) == 0xC0)
            {
                if (++jumps > 16) throw new IndexOutOfRangeException("compression loop");
                int target = BinaryPrimitives.ReadUInt16BigEndian(buf[pos..]) & 0x3FFF;
                if (!jumped) off = pos + 2;
                jumped = true;
                pos = target;
                continue;
            }
            labels.Add(EscapeLabel(Encoding.UTF8.GetString(buf.Slice(pos + 1, n))));
            pos += 1 + n;
        }
        if (!jumped) off = pos;
        return string.Join('.', labels);
    }

    // Keep dots inside a label distinguishable from separators ("J. Example Mac" → "J\. Example Mac").
    private static string EscapeLabel(string s) => s.Replace("\\", "\\\\").Replace(".", "\\.");
}
