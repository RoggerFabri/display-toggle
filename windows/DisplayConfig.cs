using System.Runtime.InteropServices;
using static DisplayToggle.Ccd;

namespace DisplayToggle;

/// <summary>
/// In-process display switching via the CCD API's <c>SetDisplayConfig</c>.
/// Whole topology — the same thing DisplaySwitch.exe / Win+P do, without spawning a helper process:
///   extend   → SDC_TOPOLOGY_EXTEND  (grab the external monitor)
///   internal → SDC_TOPOLOGY_INTERNAL (internal panel only; release the external)
/// Uses the persisted topology database, so the extended arrangement is restored as last used.
/// One display (<see cref="SetTargetActive"/>) — a per-path edit planned by <see cref="DisplayPathPlan"/>,
/// so every other display (a virtual monitor, a second external) stays as it is. Switching one off
/// saves the configuration it was on in (<see cref="ReleasedLayouts"/>); switching it back on
/// restores that, else places it right of the desktop.
/// </summary>
internal static class DisplayConfig
{
    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements, [In] DISPLAYCONFIG_PATH_INFO[]? pathArray,
        uint numModeInfoArrayElements, [In] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray, uint flags);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_PREFERRED_MODE preferredMode);

    private const uint SDC_TOPOLOGY_INTERNAL           = 0x00000001;
    private const uint SDC_TOPOLOGY_EXTEND             = 0x00000004;
    private const uint SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020;
    private const uint SDC_APPLY                       = 0x00000080;
    private const uint SDC_SAVE_TO_DATABASE            = 0x00000200;
    private const uint SDC_ALLOW_CHANGES               = 0x00000400;

    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE = 3;

    private const int ERROR_SUCCESS = 0;

    /// <summary>Apply extend topology; returns the Win32 result (0 = success).</summary>
    public static int ExtendResult() => SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_EXTEND);

    /// <summary>Apply internal-only topology; returns the Win32 result (0 = success).</summary>
    public static int InternalResult() => SetDisplayConfig(0, null, 0, null, SDC_APPLY | SDC_TOPOLOGY_INTERNAL);

    public static bool Extend() => ExtendResult() == ERROR_SUCCESS;
    public static bool Internal() => InternalResult() == ERROR_SUCCESS;

    /// <summary>
    /// Turn exactly one display (<see cref="DisplayTargets.Target.Key"/>) on or off, leaving every
    /// other display as is. True = it is now in the requested state (including "already was");
    /// false = not connected, rejected by Windows, or it would leave no active display.
    /// </summary>
    public static bool SetTargetActive(string key, bool on)
    {
        if (!DisplayTargets.TryResolve(key, out var adapterId, out uint targetId)) return false;
        return on ? TurnOn(key, adapterId, targetId) : TurnOff(key, adapterId, targetId);
    }

    /// <summary>
    /// Drop the target's active path(s) and apply the rest as given; if it was the primary, the
    /// plan moves another display to the origin. The configuration as it was is saved first, so
    /// switching back on can restore it exactly. Saved to the database so Win+P / a re-plug keep it.
    /// </summary>
    private static bool TurnOff(string key, LUID adapterId, uint targetId)
    {
        if (!DisplayTargets.Query(DisplayTargets.QDC_ONLY_ACTIVE_PATHS, out var paths, out var modes)) return false;
        if (!DisplayPathPlan.TargetActive(paths, adapterId, targetId)) return true;
        if (DisplayPathPlan.WithoutTarget(paths, modes, adapterId, targetId) is not { } plan) return false;
        var others = ActiveKeys().Where(k => !string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
        ReleasedLayouts.Remember(key, new DisplayPathPlan.Config(paths, modes), others);
        return Apply(plan, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_SAVE_TO_DATABASE | SDC_ALLOW_CHANGES);
    }

    /// <summary>
    /// Bring the target back: restore the configuration saved when it was switched off, if the same
    /// other displays are on now; else activate one of its paths on a free source, placed right of
    /// the desktop at its preferred size.
    /// </summary>
    private static bool TurnOn(string key, LUID adapterId, uint targetId)
    {
        if (!DisplayTargets.Query(DisplayTargets.QDC_ALL_PATHS, out var paths, out var modes)) return false;
        if (DisplayPathPlan.TargetActive(paths, adapterId, targetId)) return true;
        if (RestoreReleased(key)) return true;

        int candidate = DisplayPathPlan.FreeSourceCandidate(paths, adapterId, targetId);
        if (candidate < 0) return false;

        if (!PreferredSize(paths[candidate].targetInfo, out uint width, out uint height)) return false;
        var placed = DisplayPathPlan.WithTargetPlaced(paths, modes, candidate, width, height);
        return Apply(placed, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE);
    }

    private static bool Apply(DisplayPathPlan.Config config, uint flags) =>
        SetDisplayConfig((uint)config.Paths.Length, config.Paths, (uint)config.Modes.Length, config.Modes, flags) == ERROR_SUCCESS;

    /// <summary>
    /// Apply the configuration saved for <paramref name="key"/> when the displays on now are exactly
    /// the others it was saved with; the entry is dropped once restored. False = nothing applied.
    /// </summary>
    private static bool RestoreReleased(string key)
    {
        if (!ReleasedLayouts.TryGet(key, out var saved, out var others)) return false;
        if (!DisplayPathPlan.SameOthers(others, ActiveKeys())) return false;
        if (!Apply(saved, SDC_APPLY | SDC_USE_SUPPLIED_DISPLAY_CONFIG | SDC_ALLOW_CHANGES | SDC_SAVE_TO_DATABASE)) return false;
        ReleasedLayouts.Forget(key);
        return true;
    }

    /// <summary>Keys of the displays on right now (<see cref="DisplayTargets"/> resolves the device paths).</summary>
    private static IEnumerable<string> ActiveKeys() =>
        DisplayTargets.All().Where(t => t.IsActive).Select(t => t.Key);

    /// <summary>The target's preferred (native) resolution, from its EDID via the driver.</summary>
    private static bool PreferredSize(in DISPLAYCONFIG_PATH_TARGET_INFO target, out uint width, out uint height)
    {
        var q = new DISPLAYCONFIG_TARGET_PREFERRED_MODE();
        q.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_PREFERRED_MODE;
        q.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_PREFERRED_MODE>();
        q.header.adapterId = target.adapterId;
        q.header.id = target.id;
        bool ok = DisplayConfigGetDeviceInfo(ref q) == ERROR_SUCCESS && q.width > 0 && q.height > 0;
        width = q.width;
        height = q.height;
        return ok;
    }
}
