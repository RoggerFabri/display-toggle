using System.Text;
using Xunit;

namespace DisplayToggle.Tests;

// Protocol v2 session logic (PROTOCOL.md "Authentication" + "Test vectors"). The Mac's suite
// checks the same vectors and cases.
public class PeerSessionTests
{
    private const string Secret = "correct horse";
    private const string ND = "00112233445566778899aabbccddeeff";
    private const string NL = "ffeeddccbbaa99887766554433221100";
    private const string IdD = "4f1c2b3a-0000-4000-8000-000000000001";
    private const string IdL = "9e8d7c6b-0000-4000-8000-000000000002";
    private const string K = "96c594917831a601c95535419dcf0039da5be5dff680fed3faac55ed40b6a9c2";

    // ---- shared test vectors ------------------------------------------------

    [Fact]
    public void Vectors_SessionKey() => Assert.Equal(K, PeerSession.SessionKey(Secret, ND, NL, IdD, IdL));

    [Fact]
    public void Vectors_Proofs()
    {
        Assert.Equal("9ebc15d5188dd9d5e7257d4bd5e00e65d9818d7cc960f757547422689696aa65", PeerSession.Proof(K, "d"));
        Assert.Equal("7d41a2f69679cbeffaafaf5d91414556f231e86787d85d65f9ec3c972b0fc7cb", PeerSession.Proof(K, "l"));
    }

    [Fact]
    public void Vectors_Macs()
    {
        Assert.Equal("0f7ad2edb53a51e9597bb584625d624dafb11913f4e47738acb900e6a251818f", PeerSession.Mac(K, "d", 1, "state", false));
        Assert.Equal("38b16fc774a7c35072c7cc9d800ea51bbd27aa818fab1cdf2fa53ec078fd3099", PeerSession.Mac(K, "l", 1, "state", true));
        Assert.Equal("297973222f8eab884e579e82f3caa77fe91ab95dee6d45ec5c3da5b9b52b46a3", PeerSession.Mac(K, "d", 2, "ping", null));
    }

    // ---- a pair of sessions wired together ----------------------------------

    private static PeerSession Dialer(string secret = Secret, string nonce = ND, string id = IdD)
        => new(PeerRole.Dialer, id, "dialer-host", secret, _ => false, nonce);

    private static PeerSession Listener(string secret = Secret, string nonce = NL, string id = IdL)
        => new(PeerRole.Listener, id, "listener-host", secret, _ => false, nonce);

    private static PeerSession.Result Feed(PeerSession to, string line) => to.Receive(Encoding.UTF8.GetBytes(line));

    /// <summary>Run the handshake both ways; returns each side's results (hello reply, then auth).</summary>
    private static void Handshake(PeerSession d, PeerSession l)
    {
        var atL = Feed(l, d.Hello(true));
        var atD = Feed(d, l.Hello(false));
        foreach (var line in atL.Send) Assert.Null(Feed(d, line).Close);
        foreach (var line in atD.Send) Assert.Null(Feed(l, line).Close);
    }

    [Fact]
    public void SecretMode_HandshakeEstablishes_AndStateFlows()
    {
        var (d, l) = (Dialer(), Listener());
        var atL = Feed(l, d.Hello(true));
        Assert.False(atL.Established); // secret mode: hello alone isn't enough
        var atD = Feed(d, l.Hello(false));
        Assert.Contains($"\"proof\":\"{PeerSession.Proof(K, "l")}\"", atL.Send.Single());
        Assert.True(Feed(d, atL.Send.Single()).Established);
        Assert.True(Feed(l, atD.Send.Single()).Established);

        Assert.Equal(false, Feed(l, d.State(false)).PeerState);
        Assert.Equal(true, Feed(d, l.State(true)).PeerState);
        Assert.Equal("dialer-host", l.PeerHost);
        Assert.Equal(IdD, l.PeerId);
    }

    [Fact]
    public void SecretMode_FirstStateCarriesTheVectorMac()
    {
        var (d, l) = (Dialer(), Listener());
        Handshake(d, l);
        Assert.Contains($"\"mac\":\"{PeerSession.Mac(K, "d", 1, "state", false)}\"", d.State(false));
    }

    [Fact]
    public void Ping_IsAnsweredWithPong()
    {
        var (d, l) = (Dialer(), Listener());
        Handshake(d, l);
        var r = Feed(l, d.Ping());
        Assert.Null(r.Close);
        Assert.Contains("\"type\":\"pong\"", r.Send.Single());
        Assert.Null(Feed(d, r.Send.Single()).Close);
    }

    [Fact]
    public void OpenMode_HelloEstablishes_NoMacs()
    {
        var (d, l) = (Dialer(secret: ""), Listener(secret: ""));
        Assert.True(Feed(l, d.Hello(true)).Established);
        Assert.True(Feed(d, l.Hello(true)).Established);
        string state = d.State(true);
        Assert.DoesNotContain("mac", state);
        Assert.Equal(true, Feed(l, state).PeerState);
    }

    [Fact]
    public void WrongSecret_IsNotAuthenticated()
    {
        var (d, l) = (Dialer(), Listener(secret: "other"));
        var atL = Feed(l, d.Hello(true));
        Feed(d, l.Hello(true));
        Assert.Equal(PeerCloseReason.AuthFailed, Feed(d, atL.Send.Single()).Close);
    }

    [Fact]
    public void MixedMode_OpenSideClosesOnAuth()
    {
        var (d, l) = (Dialer(), Listener(secret: ""));
        var atL = Feed(l, d.Hello(true));
        Assert.True(atL.Established);                 // open side: hello is enough
        var atD = Feed(d, l.Hello(true));              // secret side answers with auth
        Assert.Equal(PeerCloseReason.PeerRequiresSecret, Feed(l, atD.Send.Single()).Close);
    }

    // ---- behavioural cases (PROTOCOL.md "Test vectors") ----------------------

    [Fact]
    public void PeerHello_WithOneOfOurNonces_Closes()
    {
        var l = new PeerSession(PeerRole.Listener, IdL, "h", Secret, n => n == ND, NL);
        Assert.Equal(PeerCloseReason.SelfReflection, Feed(l, Dialer().Hello(true)).Close);
    }

    [Fact]
    public void PeerHello_WithOurId_Closes()
    {
        var l = Listener(id: IdD.ToUpperInvariant()); // ids compare case-insensitively
        Assert.Equal(PeerCloseReason.SelfReflection, Feed(l, Dialer().Hello(true)).Close);
    }

    [Theory]
    [InlineData("""{"v":2,"type":"state","on":true}""")]
    [InlineData("not json")]
    [InlineData("[1,2]")]
    [InlineData("""{"v":2,"type":"hello","id":"x","nonce":"ABCDEF00112233445566778899AABBCC"}""")] // uppercase nonce
    [InlineData("""{"v":2,"type":"hello","id":"","nonce":"00112233445566778899aabbccddeeff"}""")]  // empty id
    public void FirstLine_NotAValidV2Hello_Closes(string line)
        => Assert.Equal(PeerCloseReason.Protocol, Feed(Listener(), line).Close);

    [Theory]
    [InlineData("""{"v":1,"type":"hello","host":"old","on":true}""")]
    [InlineData("""{"type":"hello","host":"old","on":true}""")]
    public void V1Hello_NeedsUpdate(string line)
        => Assert.Equal(PeerCloseReason.NeedsUpdate, Feed(Listener(), line).Close);

    [Fact]
    public void StateBeforeAuth_IsNotAuthenticated()
    {
        var (d, l) = (Dialer(), Listener());
        Feed(l, d.Hello(true));
        string forged = $$"""{"v":2,"type":"state","on":true,"seq":1,"mac":"{{new string('0', 64)}}"}""";
        Assert.Equal(PeerCloseReason.AuthFailed, Feed(l, forged).Close);
    }

    [Theory]
    [InlineData(1)]   // repeated
    [InlineData(3)]   // skipped
    [InlineData(-1)]  // negative
    public void Seq_NotLastPlusOne_Closes(long seq)
    {
        var (d, l) = (Dialer(), Listener());
        Handshake(d, l);
        Assert.Null(Feed(l, d.State(true)).Close); // seq 1
        string mac = PeerSession.Mac(K, "d", seq, "state", true);
        Assert.Equal(PeerCloseReason.Integrity,
            Feed(l, $$"""{"v":2,"type":"state","on":true,"seq":{{seq}},"mac":"{{mac}}"}""").Close);
    }

    [Fact]
    public void Seq_NonInteger_Closes()
    {
        var (d, l) = (Dialer(), Listener());
        Handshake(d, l);
        Assert.Equal(PeerCloseReason.Integrity,
            Feed(l, $$"""{"v":2,"type":"state","on":true,"seq":1.5,"mac":"{{PeerSession.Mac(K, "d", 1, "state", true)}}"}""").Close);
    }

    [Fact]
    public void Seq_Boolean_Closes()
    {
        var (d, l) = (Dialer(), Listener());
        Handshake(d, l);
        Assert.Equal(PeerCloseReason.Integrity,
            Feed(l, $$"""{"v":2,"type":"state","on":true,"seq":true,"mac":"{{PeerSession.Mac(K, "d", 1, "state", true)}}"}""").Close);
    }

    [Fact]
    public void On_AsNumber_ReadsAsAbsent()
    {
        // `on:1` isn't a boolean: treated as absent (mac over on=""), so the state is ignored.
        var (d, l) = (Dialer(), Listener());
        Handshake(d, l);
        var r = Feed(l, $$"""{"v":2,"type":"state","on":1,"seq":1,"mac":"{{PeerSession.Mac(K, "d", 1, "state", null)}}"}""");
        Assert.Null(r.Close);
        Assert.Null(r.PeerState);
    }

    [Fact]
    public void BadMac_Closes()
    {
        var (d, l) = (Dialer(), Listener());
        Handshake(d, l);
        string tampered = d.State(true).Replace("\"on\":true", "\"on\":false"); // flip the state, keep the mac
        Assert.Equal(PeerCloseReason.Integrity, Feed(l, tampered).Close);
    }

    [Fact]
    public void ReflectedMessage_FailsTheMac()
    {
        // Our own state sent back to us: same K, but our role in the mac → rejected.
        var (d, l) = (Dialer(), Listener());
        Handshake(d, l);
        Assert.Equal(PeerCloseReason.Integrity, Feed(d, d.State(true)).Close);
    }

    [Fact]
    public void StateWithoutOn_IsIgnored_ButConsumesItsSeq()
    {
        var (d, l) = (Dialer(), Listener());
        Handshake(d, l);
        string noOn = $$"""{"v":2,"type":"state","seq":1,"mac":"{{PeerSession.Mac(K, "d", 1, "state", null)}}"}""";
        var r = Feed(l, noOn);
        Assert.Null(r.Close);
        Assert.Null(r.PeerState);
        d.Ping();                                   // the dialer's seq 1 (we hand-built seq 1 above)
        Assert.Equal(true, Feed(l, d.State(true)).PeerState); // seq 2 → accepted
    }

    [Fact]
    public void UnknownTypes_AreIgnored()
    {
        var (d, l) = (Dialer(), Listener());
        Handshake(d, l);
        var r = Feed(l, """{"v":2,"type":"future-thing"}""");
        Assert.Null(r.Close);
        Assert.Empty(r.Send);
    }

    // ---- tie-break (PROTOCOL.md "Connection management") ---------------------

    [Fact]
    public void TieBreak_SameOpener_NewerWins()
    {
        Assert.Equal(1, PeerLink.PickWinner(PeerRole.Dialer, 1, PeerRole.Dialer, 2, IdD, IdL));
        Assert.Equal(1, PeerLink.PickWinner(PeerRole.Listener, 1, PeerRole.Listener, 2, IdD, IdL));
    }

    [Fact]
    public void TieBreak_CrossedDials_LowerIdsDialWins_SameAnswerOnBothEnds()
    {
        // IdD < IdL. On IdD's side its dial is Dialer; on IdL's side that same connection is Listener.
        Assert.Equal(0, PeerLink.PickWinner(PeerRole.Dialer, 1, PeerRole.Listener, 2, IdD, IdL));
        Assert.Equal(1, PeerLink.PickWinner(PeerRole.Dialer, 1, PeerRole.Listener, 2, IdL, IdD));
        // Case-insensitive.
        Assert.Equal(0, PeerLink.PickWinner(PeerRole.Dialer, 1, PeerRole.Listener, 2, IdD.ToUpperInvariant(), IdL));
    }
}
