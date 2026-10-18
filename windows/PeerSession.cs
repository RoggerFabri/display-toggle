using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DisplayToggle;

/// <summary>Which end of a connection we are: the side that opened it, or the side that accepted it.</summary>
public enum PeerRole { Dialer, Listener }

/// <summary>Why a session asked for its connection to be closed (PROTOCOL.md v2).</summary>
public enum PeerCloseReason
{
    /// <summary>The first line wasn't a v2 <c>hello</c>, or a field was malformed.</summary>
    Protocol,
    /// <summary>The peer speaks another protocol version (<i>needs update</i>).</summary>
    NeedsUpdate,
    /// <summary>The peer's <c>hello</c> carried our own id or one of our nonces (a reflection).</summary>
    SelfReflection,
    /// <summary>Wrong proof, or a sequenced message before authentication (<i>not authenticated</i>).</summary>
    AuthFailed,
    /// <summary>A bad <c>seq</c> or <c>mac</c> on an established session.</summary>
    Integrity,
    /// <summary>We have no secret but the peer sent <c>auth</c>: it requires one.</summary>
    PeerRequiresSecret,
}

/// <summary>
/// The protocol state of one peer-link connection (PROTOCOL.md v2), with no I/O: feed it received
/// lines, send the lines it returns. Not thread-safe — the owner serialises calls per connection.
/// </summary>
public sealed class PeerSession
{
    public sealed class Result
    {
        public List<string> Send { get; } = new();
        /// <summary>The peer became authenticated on this call (open mode: its hello arrived).</summary>
        public bool Established { get; set; }
        /// <summary>A verified <c>state</c> from an established peer.</summary>
        public bool? PeerState { get; set; }
        public PeerCloseReason? Close { get; set; }
    }

    private const string KeyLabel = "display-toggle/2 session|";

    private readonly PeerRole _role;
    private readonly string _myId;
    private readonly string _myHost;
    private readonly string _secret;
    private readonly Func<string, bool> _isOwnNonce;

    private bool _gotHello;
    private string? _key;            // K, secret mode, once both hellos are known
    private long _outSeq;
    private long _inSeq;

    public PeerSession(PeerRole role, string myId, string myHost, string secret,
                       Func<string, bool> isOwnNonce, string? nonce = null)
    {
        _role = role;
        _myId = myId;
        _myHost = myHost;
        _secret = secret ?? "";
        _isOwnNonce = isOwnNonce;
        MyNonce = nonce ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    }

    public string MyNonce { get; }
    public PeerRole Role => _role;
    public bool SecretMode => _secret.Length > 0;
    public bool IsEstablished { get; private set; }
    public string? PeerId { get; private set; }
    public string? PeerHost { get; private set; }

    private string MyRoleCode => _role == PeerRole.Dialer ? "d" : "l";
    private string PeerRoleCode => _role == PeerRole.Dialer ? "l" : "d";

    // ---- outgoing -----------------------------------------------------------

    public string Hello(bool on) => Line(w =>
    {
        w.WriteString("type", "hello");
        w.WriteString("host", _myHost);
        w.WriteString("id", _myId);
        w.WriteBoolean("on", on);
        w.WriteString("nonce", MyNonce);
    });

    /// <summary>A <c>state</c> line; only valid once established.</summary>
    public string State(bool on) => Sequenced("state", on);
    public string Ping() => Sequenced("ping", null);
    private string Pong() => Sequenced("pong", null);

    private string Sequenced(string type, bool? on)
    {
        if (!IsEstablished) throw new InvalidOperationException("session not established");
        return Line(w =>
        {
            w.WriteString("type", type);
            if (on is bool b) w.WriteBoolean("on", b);
            if (_key is not null)
            {
                long seq = ++_outSeq;
                w.WriteNumber("seq", seq);
                w.WriteString("mac", Mac(_key, MyRoleCode, seq, type, on));
            }
        });
    }

    // ---- incoming -----------------------------------------------------------

    public Result Receive(ReadOnlySpan<byte> utf8Line)
    {
        var r = new Result();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(utf8Line.ToArray()); }
        catch (JsonException)
        {
            if (!_gotHello) r.Close = PeerCloseReason.Protocol;
            return r;
        }
        using (doc)
        {
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object)
            {
                if (!_gotHello) r.Close = PeerCloseReason.Protocol;
                return r;
            }
            string? type = Str(o, "type");
            if (!_gotHello) { OnHello(o, type, r); return r; }

            switch (type)
            {
                case "auth": OnAuth(o, r); break;
                case "state" or "ping" or "pong": OnSequenced(o, type, r); break;
                // a repeated hello and unknown types are ignored
            }
        }
        return r;
    }

    private void OnHello(JsonElement o, string? type, Result r)
    {
        if (!o.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number
            || !v.TryGetInt32(out int ver) || ver != 2)
        {
            // An old build's hello (or anything without v:2) → the peer needs updating.
            r.Close = type == "hello" ? PeerCloseReason.NeedsUpdate : PeerCloseReason.Protocol;
            return;
        }
        string? nonce = Str(o, "nonce"), id = Str(o, "id");
        if (type != "hello" || !IsNonce(nonce) || string.IsNullOrEmpty(id))
        {
            r.Close = PeerCloseReason.Protocol;
            return;
        }
        if (string.Equals(id, _myId, StringComparison.OrdinalIgnoreCase) || _isOwnNonce(nonce!))
        {
            r.Close = PeerCloseReason.SelfReflection;
            return;
        }

        _gotHello = true;
        PeerId = id;
        PeerHost = Str(o, "host");
        if (!SecretMode)
        {
            IsEstablished = true;
            r.Established = true;
            return;
        }
        _key = SessionKey(_secret,
            _role == PeerRole.Dialer ? MyNonce : nonce!, _role == PeerRole.Dialer ? nonce! : MyNonce,
            _role == PeerRole.Dialer ? _myId : id!, _role == PeerRole.Dialer ? id! : _myId);
        r.Send.Add(Line(w =>
        {
            w.WriteString("type", "auth");
            w.WriteString("proof", Proof(_key, MyRoleCode));
        }));
    }

    private void OnAuth(JsonElement o, Result r)
    {
        if (!SecretMode) { r.Close = PeerCloseReason.PeerRequiresSecret; return; }
        if (IsEstablished) return; // a repeat is harmless
        string? proof = Str(o, "proof");
        if (proof is null || !FixedEquals(proof, Proof(_key!, PeerRoleCode)))
        {
            r.Close = PeerCloseReason.AuthFailed;
            return;
        }
        IsEstablished = true;
        r.Established = true;
    }

    private void OnSequenced(JsonElement o, string type, Result r)
    {
        bool? on = o.TryGetProperty("on", out var onEl) && onEl.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? onEl.GetBoolean() : null;

        if (SecretMode)
        {
            if (!IsEstablished) { r.Close = PeerCloseReason.AuthFailed; return; }
            if (!o.TryGetProperty("seq", out var seqEl) || seqEl.ValueKind != JsonValueKind.Number
                || !seqEl.TryGetInt64(out long seq) || seq < 0 || seq != _inSeq + 1
                || Str(o, "mac") is not string mac || !FixedEquals(mac, Mac(_key!, PeerRoleCode, seq, type, on)))
            {
                r.Close = PeerCloseReason.Integrity;
                return;
            }
            _inSeq = seq; // consumed even when a state without `on` is then ignored
        }
        else if (!IsEstablished) return; // can't happen in open mode (hello establishes)

        switch (type)
        {
            case "state" when on is bool b: r.PeerState = b; break;
            case "ping": r.Send.Add(Pong()); break;
        }
    }

    // ---- crypto (PROTOCOL.md "Authentication") ------------------------------

    internal static string H(string key, string message)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
    }

    internal static string SessionKey(string secret, string nD, string nL, string idD, string idL)
        => H(secret, KeyLabel + nD + "|" + nL + "|" + idD + "|" + idL);

    internal static string Proof(string key, string roleCode) => H(key, "auth|" + roleCode);

    internal static string Mac(string key, string roleCode, long seq, string type, bool? on)
        => H(key, $"{roleCode}|{seq}|{type}|{(on is bool b ? (b ? "1" : "0") : "")}");

    private static bool FixedEquals(string a, string b)
    {
        var ba = Encoding.ASCII.GetBytes(a);
        var bb = Encoding.ASCII.GetBytes(b);
        return ba.Length == bb.Length && CryptographicOperations.FixedTimeEquals(ba, bb);
    }

    private static bool IsNonce(string? s)
        => s is { Length: 32 } && s.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string? Str(JsonElement o, string name)
        => o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    private static string Line(Action<Utf8JsonWriter> body)
    {
        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buf))
        {
            w.WriteStartObject();
            w.WriteNumber("v", 2);
            body(w);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }
}
