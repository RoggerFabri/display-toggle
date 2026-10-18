using System.Net;
using System.Net.Sockets;
using System.Text;
using Xunit;

namespace DisplayToggle.Tests;

// PeerLink over real loopback sockets: two instances on 127.0.0.1, each listening on its own port.
public class PeerLinkTests : IDisposable
{
    private const string IdA = "4f1c2b3a-0000-4000-8000-000000000001";
    private const string IdB = "9e8d7c6b-0000-4000-8000-000000000002";

    private readonly PeerLink _a = new(), _b = new();
    private readonly int _portA = FreePort(), _portB = FreePort();

    public void Dispose() { _a.Dispose(); _b.Dispose(); }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static async Task<bool> WaitFor(Func<bool> cond, int ms = 8000)
    {
        var until = DateTime.UtcNow.AddMilliseconds(ms);
        while (DateTime.UtcNow < until) { if (cond()) return true; await Task.Delay(50); }
        return cond();
    }

    private void StartPair(string secretA, string secretB)
    {
        _a.Start("127.0.0.1", _portB, _portA, true, secretA, IdA);
        _b.Start("127.0.0.1", _portA, _portB, false, secretB, IdB);
    }

    [Theory]
    [InlineData("s3cret")]
    [InlineData("")]
    public async Task Pair_Links_AndStateFlows(string secret)
    {
        var got = new List<bool>();
        _b.PeerStateChanged += on => { lock (got) got.Add(on); };
        StartPair(secret, secret);

        Assert.True(await WaitFor(() => _a.IsConnected && _b.IsConnected));
        _a.SendState(false);
        Assert.True(await WaitFor(() => { lock (got) return got.Contains(false); }));
        Assert.Equal(PeerLink.Problem.None, _a.CurrentProblem);
    }

    [Fact]
    public async Task Stranger_DoesNotDropTheLink()
    {
        int drops = 0;
        _b.ConnectionChanged += (up, _, _) => { if (!up) Interlocked.Increment(ref drops); };
        StartPair("s3cret", "s3cret");
        Assert.True(await WaitFor(() => _a.IsConnected && _b.IsConnected));

        // A stranger connects and sends a well-formed hello, but can't prove the secret.
        using var stranger = new TcpClient();
        await stranger.ConnectAsync(IPAddress.Loopback, _portB);
        var hello = """{"v":2,"type":"hello","host":"x","id":"stranger","on":true,"nonce":"0123456789abcdef0123456789abcdef"}""" + "\n";
        await stranger.GetStream().WriteAsync(Encoding.UTF8.GetBytes(hello));
        await Task.Delay(500);

        Assert.True(_b.IsConnected);
        Assert.Equal(0, drops);
        var got = false;
        _a.PeerStateChanged += _ => got = true;
        _b.SendState(true);
        Assert.True(await WaitFor(() => got));
    }

    [Fact]
    public async Task MixedMode_BothSidesReportTheProblem_AndNeitherConnects()
    {
        StartPair("s3cret", "");
        Assert.True(await WaitFor(() => _a.CurrentProblem == PeerLink.Problem.NotAuthenticated
                                     && _b.CurrentProblem == PeerLink.Problem.RequiresSecret));
        Assert.False(_a.IsConnected);
        Assert.False(_b.IsConnected);
    }

    [Fact]
    public async Task V1Peer_NeedsUpdate()
    {
        _a.Start("", _portB, _portA, true, "", IdA); // listen only
        using var old = new TcpClient();
        await old.ConnectAsync(IPAddress.Loopback, _portA);
        await old.GetStream().WriteAsync(Encoding.UTF8.GetBytes("""{"v":1,"type":"hello","host":"old","on":true}""" + "\n"));
        Assert.True(await WaitFor(() => _a.CurrentProblem == PeerLink.Problem.NeedsUpdate));
        Assert.False(_a.IsConnected);
    }

    [Fact]
    public async Task DialingOurself_IsRefused()
    {
        // Our dial lands on our own listener: same id and our own nonce → closed, never "connected".
        _a.Start("127.0.0.1", _portA, _portA, true, "s3cret", IdA);
        await Task.Delay(1500);
        Assert.False(_a.IsConnected);
    }
}
