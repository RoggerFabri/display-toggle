using Xunit;

namespace DisplayToggle.Tests;

// Windows-only: the discovered-instance table (dedupe, TTL-0 goodbye, expiry by missed cycles).
public class PeerTableTests
{
    private const string Mac = @"J\. Example Mac._display-toggle._tcp.local";
    private static readonly DiscoveredPeer MacPeer = new("0F1E2D3C", "J. Example Mac", "192.0.2.35", 51763, "mac");

    [Fact]
    public void RepeatedAnswers_CollapseIntoOneEntry()
    {
        var t = new PeerTable();
        Assert.True(t.Update(Mac, MacPeer));
        for (int i = 0; i < 20; i++)
        {
            Assert.False(t.Observe(Mac, 10));
            Assert.False(t.Update(Mac, MacPeer)); // identical → no change event
        }
        Assert.Single(t.Peers);
        Assert.Equal(1, t.Count);
        Assert.True(t.Contains(Mac.ToUpperInvariant())); // instance names are case-insensitive
    }

    [Fact]
    public void NewAddress_ForSameInstance_IsAChange()
    {
        var t = new PeerTable();
        t.Update(Mac, MacPeer);
        Assert.True(t.Update(Mac, MacPeer with { Ip = "192.0.2.36" }));
        Assert.Equal("192.0.2.36", Assert.Single(t.Peers).Ip);
    }

    [Fact]
    public void Goodbye_InRawDnsapiForm_RemovesEntryAddedFromEscapedReply()
    {
        var t = new PeerTable();
        t.Update(PeerDiscoveryLogic.InstanceKey(Mac), MacPeer);                       // from our reply parser
        Assert.True(t.Observe(PeerDiscoveryLogic.InstanceKey("J. Example Mac._display-toggle._tcp.local"), 0)); // dnsapi
        Assert.Empty(t.Peers);
    }

    [Fact]
    public void TtlZeroPtr_RemovesEntry()
    {
        var t = new PeerTable();
        t.Update(Mac, MacPeer);
        Assert.True(t.Observe(Mac, 0));
        Assert.Empty(t.Peers);
        Assert.Equal(0, t.Count);
        Assert.False(t.Observe(Mac, 0)); // already gone
    }

    [Fact]
    public void SelfEntry_IsTrackedButNeverListed()
    {
        var t = new PeerTable();
        Assert.False(t.Update("WIN-LAPTOP._display-toggle._tcp.local", null));
        Assert.Empty(t.Peers);
        Assert.Equal(1, t.Count);
    }

    [Fact]
    public void Entry_ExpiresAfterMissingMoreThanNCycles()
    {
        var t = new PeerTable(missedCyclesToExpire: 2);
        t.Update(Mac, MacPeer);
        Assert.False(t.NextCycle()); // missed 1
        Assert.False(t.NextCycle()); // missed 2
        Assert.Single(t.Peers);
        Assert.True(t.NextCycle());  // missed 3 → expired
        Assert.Empty(t.Peers);
    }

    [Fact]
    public void Sighting_ResetsExpiry()
    {
        var t = new PeerTable(missedCyclesToExpire: 2);
        t.Update(Mac, MacPeer);
        for (int i = 0; i < 10; i++)
        {
            t.NextCycle();
            t.Observe(Mac, 10);
        }
        Assert.Single(t.Peers);
    }

    [Fact]
    public void Peers_SortedByName()
    {
        var t = new PeerTable();
        t.Update("b._display-toggle._tcp.local", MacPeer with { Name = "Zed", Id = "z" });
        t.Update("a._display-toggle._tcp.local", MacPeer with { Name = "alpha", Id = "a" });
        Assert.Equal(new[] { "alpha", "Zed" }, t.Peers.Select(p => p.Name));
    }
}
