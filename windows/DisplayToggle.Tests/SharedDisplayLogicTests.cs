using Xunit;
using T = DisplayToggle.DisplayTargets.Target;

namespace DisplayToggle.Tests;

public class SharedDisplayLogicTests
{
    private static readonly T Panel = new("panel", "Built-in display", IsInternal: true, IsActive: true);
    private static readonly T Dell = new("dell", "DELL U2720Q", IsInternal: false, IsActive: true);
    private static readonly T Virtual = new("vda", "SudoVDA", IsInternal: false, IsActive: false);

    [Fact]
    public void ConfiguredKey_Wins_EvenWhenDisconnected()
    {
        Assert.Equal("gone", SharedDisplayLogic.ResolveSharedKey("gone", new[] { Panel, Dell }));
        Assert.Equal("vda", SharedDisplayLogic.ResolveSharedKey("vda", new[] { Panel, Dell, Virtual }));
    }

    [Fact]
    public void SingleExternal_IsAutoPicked() =>
        Assert.Equal("dell", SharedDisplayLogic.ResolveSharedKey("", new[] { Panel, Dell }));

    [Fact]
    public void TwoExternals_NoKey_IsAllMode() =>
        Assert.Null(SharedDisplayLogic.ResolveSharedKey("", new[] { Panel, Dell, Virtual }));

    [Fact]
    public void InternalTargets_AreNeverPicked()
    {
        Assert.Null(SharedDisplayLogic.ResolveSharedKey("", new[] { Panel }));
        Assert.Null(SharedDisplayLogic.ResolveSharedKey("", Array.Empty<T>()));
    }

    [Fact]
    public void AggregateOn_SharedMode_FollowsSharedTarget()
    {
        Assert.True(SharedDisplayLogic.AggregateOn("dell", new[] { Panel, Dell, Virtual }, storedOn: false));
        Assert.False(SharedDisplayLogic.AggregateOn("vda", new[] { Panel, Dell, Virtual }, storedOn: true));
    }

    [Fact]
    public void AggregateOn_SharedNotConnected_UsesStoredIntent()
    {
        Assert.True(SharedDisplayLogic.AggregateOn("gone", new[] { Panel, Dell }, storedOn: true));
        Assert.False(SharedDisplayLogic.AggregateOn("gone", new[] { Panel, Dell }, storedOn: false));
    }

    [Fact]
    public void AggregateOn_AllMode_AnyActiveExternal()
    {
        Assert.True(SharedDisplayLogic.AggregateOn(null, new[] { Panel, Dell, Virtual }, storedOn: false));
        Assert.False(SharedDisplayLogic.AggregateOn(null, new[] { Panel, Dell with { IsActive = false }, Virtual }, storedOn: true));
        Assert.False(SharedDisplayLogic.AggregateOn(null, new[] { Panel }, storedOn: true));
    }

    [Fact]
    public void AggregateOn_AllMode_NoTargets_UsesStoredIntent()
    {
        Assert.True(SharedDisplayLogic.AggregateOn(null, Array.Empty<T>(), storedOn: true));
        Assert.False(SharedDisplayLogic.AggregateOn(null, Array.Empty<T>(), storedOn: false));
    }

    private static HashSet<string> Keys(params string[] keys) => new(keys, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Hotplug_FirstObservation_DoesNothing() =>
        Assert.Null(SharedDisplayLogic.HotplugTarget(null, new[] { Panel, Dell with { IsActive = false } }, "dell", intentOn: true));

    [Fact]
    public void Hotplug_ActivationOnlyChange_DoesNothing() =>
        Assert.Null(SharedDisplayLogic.HotplugTarget(Keys("panel", "dell", "vda"),
            new[] { Panel, Dell with { IsActive = false }, Virtual }, "dell", intentOn: true));

    [Fact]
    public void Hotplug_TargetConnected_ReleasesSharedTurnedOnAgainstIntent() =>
        Assert.False(SharedDisplayLogic.HotplugTarget(Keys("panel", "dell"),
            new[] { Panel, Dell, Virtual }, "dell", intentOn: false));

    [Fact]
    public void Hotplug_TargetRemoved_GrabsSharedTurnedOffAgainstIntent() =>
        Assert.True(SharedDisplayLogic.HotplugTarget(Keys("panel", "dell", "vda"),
            new[] { Panel, Dell with { IsActive = false } }, "dell", intentOn: true));

    [Fact]
    public void Hotplug_AlreadyInIntendedState_DoesNothing() =>
        Assert.Null(SharedDisplayLogic.HotplugTarget(Keys("panel", "dell"),
            new[] { Panel, Dell, Virtual }, "dell", intentOn: true));

    [Fact]
    public void Hotplug_AllMode_DoesNothing() =>
        Assert.Null(SharedDisplayLogic.HotplugTarget(Keys("panel", "dell"),
            new[] { Panel, Dell, Virtual }, "", intentOn: false));

    [Fact]
    public void Hotplug_SharedNotConnected_DoesNothing() =>
        Assert.Null(SharedDisplayLogic.HotplugTarget(Keys("panel", "dell", "vda"),
            new[] { Panel, Dell }, "gone", intentOn: false));

    [Fact]
    public void Hotplug_KeysCompareCaseInsensitively()
    {
        // The same set in another case: not a hot-plug.
        Assert.Null(SharedDisplayLogic.HotplugTarget(new HashSet<string> { "PANEL", "DELL" },
            new[] { Panel, Dell with { IsActive = false } }, "dell", intentOn: true));
        // The configured key finds its target whatever the case.
        Assert.False(SharedDisplayLogic.HotplugTarget(Keys("panel", "dell"),
            new[] { Panel, Dell, Virtual }, "DELL", intentOn: false));
    }
}
