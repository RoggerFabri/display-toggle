using System.Net;
using Xunit;

namespace DisplayToggle.Tests;

// Shared semantics — mirrored 1:1 by the Mac's peer-logic tests (docs/specs/peer-discovery.md).
public class PeerDiscoveryLogicTests
{
    private const string OurId = "11111111-2222-3333-4444-555555555555";
    private const string OurName = "WIN-LAPTOP";

    private static DiscoveredPeer Peer(string id, string ip, int port = 51763, string name = "Mac")
        => new(id, name, ip, port, "mac");

    private static readonly (IPAddress, int)[] Lan24 = { (IPAddress.Parse("192.0.2.112"), 24) };

    // ---- self-filter ----------------------------------------------------------

    [Fact] public void SelfFilter_OwnId_IsSelf() => Assert.True(PeerDiscoveryLogic.IsSelf(OurId, "Whatever", OurId, OurName));
    [Fact] public void SelfFilter_OwnIdDifferentCase_IsSelf() => Assert.True(PeerDiscoveryLogic.IsSelf(OurId.ToUpperInvariant(), "x", OurId, OurName));
    [Fact] public void SelfFilter_OtherId_Kept() => Assert.False(PeerDiscoveryLogic.IsSelf("0F1E2D3C-4B5A-4697-8877-665544332211", OurName, OurId, OurName));
    [Fact] public void SelfFilter_NoId_SameName_IsSelf() => Assert.True(PeerDiscoveryLogic.IsSelf("", OurName, OurId, OurName));
    [Fact] public void SelfFilter_NullId_SameName_IsSelf() => Assert.True(PeerDiscoveryLogic.IsSelf(null, OurName, OurId, OurName));
    [Fact] public void SelfFilter_NoId_OtherName_Kept() => Assert.False(PeerDiscoveryLogic.IsSelf("", "J. Example Mac", OurId, OurName));

    // ---- IP pick ----------------------------------------------------------------

    [Fact]
    public void PickAddress_PrefersSameSubnet()
        => Assert.Equal("192.0.2.35",
            PeerDiscoveryLogic.PickAddress(new[] { "100.64.0.117", "172.16.0.1", "192.0.2.35" }, Lan24));

    [Fact]
    public void PickAddress_NeverLinkLocal()
        => Assert.Equal("10.0.0.5",
            PeerDiscoveryLogic.PickAddress(new[] { "169.254.10.20", "10.0.0.5" }, new[] { (IPAddress.Parse("169.254.1.1"), 16) }));

    [Fact]
    public void PickAddress_OnlyLinkLocal_None()
        => Assert.Null(PeerDiscoveryLogic.PickAddress(new[] { "169.254.10.20" }, Lan24));

    [Fact]
    public void PickAddress_NoSubnetMatch_FirstNonLinkLocal()
        => Assert.Equal("100.64.0.117",
            PeerDiscoveryLogic.PickAddress(new[] { "169.254.3.3", "100.64.0.117", "172.16.0.1" }, Lan24));

    [Fact]
    public void PickAddress_Empty_None()
        => Assert.Null(PeerDiscoveryLogic.PickAddress(Array.Empty<string>(), Lan24));

    [Fact]
    public void PickAddress_IgnoresGarbageAndIPv6()
        => Assert.Equal("192.0.2.35", PeerDiscoveryLogic.PickAddress(new[] { "not-an-ip", "fe80::1", "192.0.2.35" }, Lan24));

    // Windows-only: locals are checked in order, so the default-route interface (passed first) wins.
    [Fact]
    public void PickAddress_TwoLocals_DefaultRouteFirstWins()
    {
        var locals = new[] { (IPAddress.Parse("192.0.2.112"), 24), (IPAddress.Parse("172.16.0.1"), 20) };
        Assert.Equal("192.0.2.35", PeerDiscoveryLogic.PickAddress(new[] { "172.16.6.9", "192.0.2.35" }, locals));
        Array.Reverse(locals);
        Assert.Equal("172.16.6.9", PeerDiscoveryLogic.PickAddress(new[] { "172.16.6.9", "192.0.2.35" }, locals));
    }

    // ---- follow / auto-pick -------------------------------------------------------

    private const string MacId = "0F1E2D3C-4B5A-4697-8877-665544332211";

    [Fact]
    public void Adopt_Connected_AlwaysNone()
    {
        Assert.Null(PeerDiscoveryLogic.DecideAdopt(true, "", 51763, "", new[] { Peer(MacId, "192.0.2.35") }));
        Assert.Null(PeerDiscoveryLogic.DecideAdopt(true, "192.0.2.20", 51763, MacId, new[] { Peer(MacId, "192.0.2.35") }));
    }

    [Fact]
    public void Adopt_PeerIdFoundAtNewIp_Adopts()
        => Assert.Equal("192.0.2.35",
            PeerDiscoveryLogic.DecideAdopt(false, "192.0.2.20", 51763, MacId, new[] { Peer(MacId, "192.0.2.35") })?.Ip);

    [Fact]
    public void Adopt_PeerIdFoundAtNewPort_Adopts()
        => Assert.Equal(51800,
            PeerDiscoveryLogic.DecideAdopt(false, "192.0.2.35", 51763, MacId, new[] { Peer(MacId, "192.0.2.35", 51800) })?.Port);

    [Fact]
    public void Adopt_PeerIdDifferentCase_StillMatches()
    {
        Assert.Null(PeerDiscoveryLogic.DecideAdopt(false, "192.0.2.35", 51763, MacId.ToLowerInvariant(), new[] { Peer(MacId, "192.0.2.35") }));
        Assert.Equal("192.0.2.36",
            PeerDiscoveryLogic.DecideAdopt(false, "192.0.2.35", 51763, MacId.ToLowerInvariant(), new[] { Peer(MacId, "192.0.2.36") })?.Ip);
        Assert.True(PeerDiscoveryLogic.IsCurrent(Peer(MacId, "192.0.2.35"), "", 0, MacId.ToLowerInvariant()));
    }

    [Fact]
    public void PickAddress_LinkLocalOnLinkLocalSubnet_StillDropped()
        => Assert.Null(PeerDiscoveryLogic.PickAddress(new[] { "169.254.10.20" }, new[] { (IPAddress.Parse("169.254.1.1"), 16) }));

    [Fact]
    public void Adopt_PeerIdSameIpAndPort_None()
        => Assert.Null(PeerDiscoveryLogic.DecideAdopt(false, "192.0.2.35", 51763, MacId, new[] { Peer(MacId, "192.0.2.35") }));

    [Fact]
    public void Adopt_PeerIdNotFound_None()
        => Assert.Null(PeerDiscoveryLogic.DecideAdopt(false, "192.0.2.35", 51763, MacId, new[] { Peer("other", "192.0.2.50") }));

    [Fact]
    public void Adopt_NoPeerHost_OneDiscovered_Adopts()
        => Assert.Equal(MacId, PeerDiscoveryLogic.DecideAdopt(false, "", 51763, "", new[] { Peer(MacId, "192.0.2.35") })?.Id);

    [Fact]
    public void Adopt_NoPeerHost_TwoDiscovered_None()
        => Assert.Null(PeerDiscoveryLogic.DecideAdopt(false, "", 51763, "",
            new[] { Peer(MacId, "192.0.2.35"), Peer("other", "192.0.2.50") }));

    [Fact]
    public void Adopt_ManualPeerHost_OneDiscovered_None()
        => Assert.Null(PeerDiscoveryLogic.DecideAdopt(false, "192.0.2.20", 51763, "", new[] { Peer(MacId, "192.0.2.35") }));

    [Fact]
    public void Adopt_Standalone_NeverAutoPicksOrFollows()
    {
        Assert.Null(PeerDiscoveryLogic.DecideAdopt(false, "", 51763, "", new[] { Peer(MacId, "192.0.2.35") }, standalone: true));
        Assert.Null(PeerDiscoveryLogic.DecideAdopt(false, "192.0.2.20", 51763, MacId, new[] { Peer(MacId, "192.0.2.35") }, standalone: true));
    }

    // ---- checkmark ------------------------------------------------------------------

    [Fact]
    public void Current_WithPeerId_MatchesByIdOnly()
    {
        Assert.True(PeerDiscoveryLogic.IsCurrent(Peer(MacId, "192.0.2.99", 1), "192.0.2.35", 51763, MacId));
        Assert.False(PeerDiscoveryLogic.IsCurrent(Peer("other", "192.0.2.35"), "192.0.2.35", 51763, MacId));
    }

    [Fact]
    public void Current_WithoutPeerId_MatchesByHostAndPort()
    {
        Assert.True(PeerDiscoveryLogic.IsCurrent(Peer(MacId, "192.0.2.35"), "192.0.2.35", 51763, ""));
        Assert.False(PeerDiscoveryLogic.IsCurrent(Peer(MacId, "192.0.2.35", 51800), "192.0.2.35", 51763, ""));
        Assert.False(PeerDiscoveryLogic.IsCurrent(Peer(MacId, "192.0.2.36"), "192.0.2.35", 51763, ""));
    }

    // ---- instance label --------------------------------------------------------------

    [Theory]
    [InlineData(@"J\. Example Mac._display-toggle._tcp.local", "J. Example Mac")]
    [InlineData(@"Jane\032Mac._display-toggle._tcp.local", "Jane Mac")]
    [InlineData("WIN-LAPTOP._display-toggle._tcp.local", "WIN-LAPTOP")]
    public void InstanceLabel_Unescapes(string fqdn, string expected) => Assert.Equal(expected, PeerDiscoveryLogic.InstanceLabel(fqdn));

    // The reply parser (escaped), dnsapi (raw label) and \DDD forms must all land on one table key,
    // or a dnsapi goodbye wouldn't remove the entry and the early-query guard wouldn't hold.
    [Theory]
    [InlineData(@"J\. Example Mac._display-toggle._tcp.local")]
    [InlineData("J. Example Mac._display-toggle._tcp.local")]
    [InlineData(@"J\.\032Example\032Mac._display-toggle._tcp.local")]
    [InlineData("J. Example Mac._DISPLAY-TOGGLE._tcp.local.")]
    public void InstanceKey_AllFormsCanonicalise(string fqdn) => Assert.Equal("J. Example Mac", PeerDiscoveryLogic.InstanceKey(fqdn));

    [Fact]
    public void InstanceKey_KeepsLiteralBackslash() => Assert.Equal(@"a\b", PeerDiscoveryLogic.InstanceKey(@"a\\b._display-toggle._tcp.local"));

}
