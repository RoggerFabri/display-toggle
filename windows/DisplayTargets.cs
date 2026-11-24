using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using static DisplayToggle.Ccd;

namespace DisplayToggle;

/// <summary>
/// Enumerates connected monitor "targets" via the CCD API (QueryDisplayConfig over all paths, so a
/// display that's switched off is still listed) to get real EDID friendly names, a stable key, and
/// to tell the built-in laptop panel (INTERNAL output technology) from external monitors — the
/// Windows analogue of the Mac's CGDisplayIsBuiltin check. Also the one place that queries CCD,
/// for <see cref="DisplayConfig"/> too.
/// </summary>
internal static class DisplayTargets
{
    public const uint QDC_ALL_PATHS = 0x00000001;
    public const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME deviceName);

    /// <summary>
    /// A connected monitor. <paramref name="Key"/> is its CCD monitor device path (stable across
    /// reboots and switches), or the friendly name when the driver gives no path.
    /// </summary>
    public sealed record Target(string Key, string Name, bool IsInternal, bool IsActive);

    private readonly record struct Found(LUID AdapterId, uint TargetId, Target Target);

    /// <summary>
    /// Every connected target, active or not, deduped by key, in enumeration order; empty if CCD fails.
    /// </summary>
    public static List<Target> All() => Connected().Select(f => f.Target).ToList();

    /// <summary>The CCD adapter + target id of the connected target with this key.</summary>
    public static bool TryResolve(string key, out LUID adapterId, out uint targetId)
    {
        foreach (var f in Connected())
        {
            if (f.Target.Key != key) continue;
            adapterId = f.AdapterId;
            targetId = f.TargetId;
            return true;
        }
        adapterId = default;
        targetId = 0;
        return false;
    }

    /// <summary>
    /// QueryDisplayConfig with arrays trimmed to what it returned. The topology can change between
    /// sizing and querying (e.g. mid-switch), which fails the query with ERROR_INSUFFICIENT_BUFFER:
    /// size again and retry, as the API docs prescribe.
    /// </summary>
    public static bool Query(uint flags, out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
    {
        for (int attempt = 0; ; attempt++)
        {
            paths = [];
            modes = [];
            if (GetDisplayConfigBufferSizes(flags, out uint numPaths, out uint numModes) != 0) return false;
            paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
            modes = new DISPLAYCONFIG_MODE_INFO[numModes];
            int rc = QueryDisplayConfig(flags, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
            if (rc == 0)
            {
                Array.Resize(ref paths, (int)numPaths);
                Array.Resize(ref modes, (int)numModes);
                return true;
            }
            if (rc != ERROR_INSUFFICIENT_BUFFER || attempt >= 2) return false;
        }
    }

    /// <summary>
    /// One entry per connected target (all paths with <c>targetAvailable</c>; a target has one per
    /// source that could drive it), active if any of its paths is. Targets that share a key merge.
    /// </summary>
    private static List<Found> Connected()
    {
        var found = new List<Found>();
        try
        {
            if (!Query(QDC_ALL_PATHS, out var paths, out _)) return found;

            var byTarget = new Dictionary<(uint, int, uint), int>(); // target → index in found, -1 = unnamed
            var byKey = new Dictionary<string, int>();
            foreach (ref readonly var p in paths.AsSpan())
            {
                var t = p.targetInfo;
                if (t.targetAvailable == 0) continue;
                bool active = DisplayPathPlan.IsActive(p);

                var id = (t.adapterId.LowPart, t.adapterId.HighPart, t.id);
                if (!byTarget.TryGetValue(id, out int at))
                {
                    at = -1;
                    if (Describe(t, out var target) && !byKey.TryGetValue(target.Key, out at))
                    {
                        at = found.Count;
                        byKey[target.Key] = at;
                        found.Add(new Found(t.adapterId, t.id, target));
                    }
                    byTarget[id] = at;
                }
                if (at >= 0 && active && !found[at].Target.IsActive)
                    found[at] = found[at] with { Target = found[at].Target with { IsActive = true } };
            }
        }
        catch { /* CCD unavailable → caller falls back */ }
        return found;
    }

    /// <summary>The target's name, key and kind (inactive for now); false if the driver won't say.</summary>
    private static bool Describe(in DISPLAYCONFIG_PATH_TARGET_INFO t, [NotNullWhen(true)] out Target? target)
    {
        target = null;
        var q = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
        q.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
        q.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
        q.header.adapterId = t.adapterId;
        q.header.id = t.id;
        if (DisplayConfigGetDeviceInfo(ref q) != 0) return false;

        bool isInternal = DisplayPathPlan.IsInternalTech(q.outputTechnology);
        string name = string.IsNullOrWhiteSpace(q.monitorFriendlyDeviceName)
            ? (isInternal ? "Built-in display" : "External display")
            : q.monitorFriendlyDeviceName;
        string key = string.IsNullOrEmpty(q.monitorDevicePath) ? name : q.monitorDevicePath;
        target = new Target(key, name, isInternal, IsActive: false);
        return true;
    }
}
