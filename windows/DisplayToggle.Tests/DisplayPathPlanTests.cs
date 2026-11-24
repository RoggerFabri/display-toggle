using Xunit;
using static DisplayToggle.Ccd;

namespace DisplayToggle.Tests;

// Per-display switching plans: what goes back to SetDisplayConfig when one display turns off or on.
public class DisplayPathPlanTests
{
    private static readonly LUID Gpu = new() { LowPart = 1 };
    private static readonly LUID OtherGpu = new() { LowPart = 2 };
    private const uint Internal = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL;
    private const uint Hdmi = 5;
    private const uint Invalid = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;

    private sealed record Display(uint Source, uint Target, uint Tech, int X, int Y, uint W = 1920, uint H = 1080);

    /// <summary>Active paths in the given order; modes as [source, target] pairs, one pair per display.</summary>
    private static (DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes) Active(params Display[] displays)
    {
        var paths = new List<DISPLAYCONFIG_PATH_INFO>();
        var modes = new List<DISPLAYCONFIG_MODE_INFO>();
        foreach (var d in displays)
        {
            var p = MakePath(d.Source, d.Target, d.Tech, active: true);
            p.sourceInfo.modeInfoIdx = (uint)modes.Count;
            modes.Add(SourceMode(d.Source, d.X, d.Y, d.W, d.H));
            p.targetInfo.modeInfoIdx = (uint)modes.Count;
            modes.Add(TargetMode(d.Target));
            paths.Add(p);
        }
        return (paths.ToArray(), modes.ToArray());
    }

    private static DISPLAYCONFIG_PATH_INFO MakePath(uint source, uint target, uint tech, bool active, LUID? sourceAdapter = null) => new()
    {
        sourceInfo = new() { adapterId = sourceAdapter ?? Gpu, id = source, modeInfoIdx = Invalid },
        targetInfo = new() { adapterId = Gpu, id = target, modeInfoIdx = Invalid, outputTechnology = tech, targetAvailable = 1 },
        flags = active ? DISPLAYCONFIG_PATH_ACTIVE : 0,
    };

    private static DISPLAYCONFIG_MODE_INFO SourceMode(uint source, int x, int y, uint w, uint h) => new()
    {
        infoType = DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE, id = source, adapterId = Gpu,
        sourceMode = new() { width = w, height = h, pixelFormat = DISPLAYCONFIG_PIXELFORMAT_32BPP, position = new() { x = x, y = y } },
    };

    private static DISPLAYCONFIG_MODE_INFO TargetMode(uint target) => new() { infoType = 2, id = target, adapterId = Gpu };

    private static (int X, int Y) PositionOf(DisplayPathPlan.Config c, uint target)
    {
        var p = c.Paths.Single(p => p.targetInfo.id == target);
        var pos = c.Modes[p.sourceInfo.modeInfoIdx].sourceMode.position;
        return (pos.x, pos.y);
    }

    [Fact]
    public void TurningOffPrimary_MovesFirstRemainingToOrigin_KeepingRelativeLayout()
    {
        var (paths, modes) = Active(
            new Display(0, 10, Hdmi, 0, 0),
            new Display(1, 11, Hdmi, 1920, 0),
            new Display(2, 12, Hdmi, -1280, 200));

        var plan = DisplayPathPlan.WithoutTarget(paths, modes, Gpu, 10)!.Value;

        Assert.Equal(2, plan.Paths.Length);
        Assert.Equal((0, 0), PositionOf(plan, 11));
        Assert.Equal((-3200, 200), PositionOf(plan, 12));
    }

    [Fact]
    public void TurningOffPrimary_PrefersInternalPanelAsNewOrigin()
    {
        var (paths, modes) = Active(
            new Display(0, 10, Hdmi, 0, 0),
            new Display(1, 11, Hdmi, 1920, 0),
            new Display(2, 12, Internal, -1536, 300));

        var plan = DisplayPathPlan.WithoutTarget(paths, modes, Gpu, 10)!.Value;

        Assert.Equal((0, 0), PositionOf(plan, 12));
        Assert.Equal((3456, -300), PositionOf(plan, 11));
    }

    [Fact]
    public void TurningOffNonPrimary_LeavesOtherPositionsAlone()
    {
        var (paths, modes) = Active(
            new Display(0, 10, Internal, 0, 0),
            new Display(1, 11, Hdmi, 1920, 0),
            new Display(2, 12, Hdmi, 3840, -100));

        var plan = DisplayPathPlan.WithoutTarget(paths, modes, Gpu, 11)!.Value;

        Assert.Equal((0, 0), PositionOf(plan, 10));
        Assert.Equal((3840, -100), PositionOf(plan, 12));
    }

    [Fact]
    public void TurningOff_CompactsModes_AndRepointsIndices()
    {
        // Modes deliberately out of path order, and one path without a target mode.
        var modes = new[]
        {
            TargetMode(12), SourceMode(1, 1920, 0, 1920, 1080), SourceMode(2, 3840, 0, 1920, 1080),
            TargetMode(10), SourceMode(0, 0, 0, 1920, 1080), TargetMode(11),
        };
        var paths = new[]
        {
            MakePath(0, 10, Internal, active: true), MakePath(1, 11, Hdmi, active: true), MakePath(2, 12, Hdmi, active: true),
        };
        (paths[0].sourceInfo.modeInfoIdx, paths[0].targetInfo.modeInfoIdx) = (4, 3);
        (paths[1].sourceInfo.modeInfoIdx, paths[1].targetInfo.modeInfoIdx) = (1, 5);
        (paths[2].sourceInfo.modeInfoIdx, paths[2].targetInfo.modeInfoIdx) = (2, Invalid);

        var plan = DisplayPathPlan.WithoutTarget(paths, modes, Gpu, 11)!.Value;

        Assert.Equal(3, plan.Modes.Length);
        Assert.Equal(new[] { 10u, 12u }, plan.Paths.Select(p => p.targetInfo.id));
        foreach (var p in plan.Paths)
        {
            var source = plan.Modes[p.sourceInfo.modeInfoIdx];
            Assert.Equal(DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE, source.infoType);
            Assert.Equal(p.sourceInfo.id, source.id);
        }
        Assert.Equal(10u, plan.Modes[plan.Paths[0].targetInfo.modeInfoIdx].id);
        Assert.Equal(Invalid, plan.Paths[1].targetInfo.modeInfoIdx);
        // The caller's arrays are left untouched.
        Assert.Equal(4u, paths[0].sourceInfo.modeInfoIdx);
    }

    [Fact]
    public void TurningOffTheLastActiveDisplay_IsRefused()
    {
        var (paths, modes) = Active(new Display(0, 10, Hdmi, 0, 0));
        Assert.Null(DisplayPathPlan.WithoutTarget(paths, modes, Gpu, 10));
    }

    [Fact]
    public void Candidate_NeverReusesASourceDrivingAnActivePath()
    {
        var all = new[]
        {
            MakePath(0, 10, Internal, active: true),
            MakePath(1, 11, Hdmi, active: true),
            MakePath(0, 12, Hdmi, active: false),
            MakePath(1, 12, Hdmi, active: false),
            MakePath(2, 12, Hdmi, active: false),
        };

        Assert.Equal(4, DisplayPathPlan.FreeSourceCandidate(all, Gpu, 12));
        Assert.Equal(-1, DisplayPathPlan.FreeSourceCandidate(all[..4], Gpu, 12));
    }

    [Fact]
    public void Candidate_SameSourceIdOnAnotherAdapter_IsFree_ButAnUnavailableTargetIsNot()
    {
        var all = new[]
        {
            MakePath(0, 10, Internal, active: true),
            MakePath(0, 12, Hdmi, active: false, sourceAdapter: OtherGpu),
        };
        Assert.Equal(1, DisplayPathPlan.FreeSourceCandidate(all, Gpu, 12));

        all[1].targetInfo.targetAvailable = 0;
        Assert.Equal(-1, DisplayPathPlan.FreeSourceCandidate(all, Gpu, 12));
    }

    [Fact]
    public void SavedLayout_Restores_WhenTheSameOthersAreOn_InAnyOrderAndCase()
    {
        string[] saved = [@"\\?\DISPLAY#INTERNAL#1", @"\\?\DISPLAY#SUDOVDA#2"];
        Assert.True(DisplayPathPlan.SameOthers(saved, [@"\\?\display#sudovda#2", @"\\?\DISPLAY#INTERNAL#1"]));
    }

    [Fact]
    public void SavedLayout_DoesNotRestore_WhenAnotherDisplayIsAlsoOn()
    {
        string[] saved = [@"\\?\DISPLAY#INTERNAL#1"];
        Assert.False(DisplayPathPlan.SameOthers(saved, [@"\\?\DISPLAY#INTERNAL#1", @"\\?\DISPLAY#SUDOVDA#2"]));
    }

    [Fact]
    public void SavedLayout_DoesNotRestore_WhenOneOfItsDisplaysIsOff()
    {
        string[] saved = [@"\\?\DISPLAY#INTERNAL#1", @"\\?\DISPLAY#SUDOVDA#2"];
        Assert.False(DisplayPathPlan.SameOthers(saved, [@"\\?\DISPLAY#INTERNAL#1"]));
    }

    [Fact]
    public void SavedLayout_StructBytes_RoundTripThroughBase64()
    {
        var (paths, modes) = Active(
            new Display(0, 10, Internal, 0, 0),
            new Display(1, 11, Hdmi, -2560, -360, 2560, 1440));
        modes[1].targetMode.targetVideoSignalInfo.pixelRate = 148_500_000;
        modes[1].targetMode.targetVideoSignalInfo.vSyncFreq = new() { Numerator = 60_000, Denominator = 1_001 };

        var backPaths = ReleasedLayouts.Decode<DISPLAYCONFIG_PATH_INFO>(ReleasedLayouts.Encode(paths));
        var backModes = ReleasedLayouts.Decode<DISPLAYCONFIG_MODE_INFO>(ReleasedLayouts.Encode(modes));

        Assert.Equal(paths, backPaths);
        Assert.Equal(modes, backModes);
        Assert.Equal((-2560, -360), (backModes![2].sourceMode.position.x, backModes[2].sourceMode.position.y));
        Assert.Equal(148_500_000ul, backModes[1].targetMode.targetVideoSignalInfo.pixelRate);
    }

    [Fact]
    public void SavedLayout_TruncatedOrInvalidBytes_DoNotDecode()
    {
        var (paths, _) = Active(new Display(0, 10, Internal, 0, 0));
        byte[] bytes = Convert.FromBase64String(ReleasedLayouts.Encode(paths));

        Assert.Null(ReleasedLayouts.Decode<DISPLAYCONFIG_PATH_INFO>(Convert.ToBase64String(bytes[..^1])));
        Assert.Null(ReleasedLayouts.Decode<DISPLAYCONFIG_PATH_INFO>("not base64!"));
    }

    [Fact]
    public void Placed_RightOfDesktopBoundingBox_AtTopEdge()
    {
        var (active, modes) = Active(
            new Display(0, 10, Internal, -1920, 100),
            new Display(1, 11, Hdmi, 0, 0, 2560, 1440));
        var all = active.Append(MakePath(2, 12, Hdmi, active: false)).ToArray();

        var plan = DisplayPathPlan.WithTargetPlaced(all, modes, 2, 1366, 1024);

        Assert.Equal(3, plan.Paths.Length);
        var added = plan.Paths[2];
        Assert.True(DisplayPathPlan.IsActive(added));
        Assert.Equal(Invalid, added.targetInfo.modeInfoIdx);
        var mode = plan.Modes[added.sourceInfo.modeInfoIdx];
        Assert.Equal(DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE, mode.infoType);
        Assert.Equal(2u, mode.id);
        Assert.Equal((1366u, 1024u), (mode.sourceMode.width, mode.sourceMode.height));
        Assert.Equal((2560, 0), (mode.sourceMode.position.x, mode.sourceMode.position.y));
        Assert.Equal((-1920, 100), PositionOf(plan, 10));
        Assert.Equal((0, 0), PositionOf(plan, 11));
    }
}
