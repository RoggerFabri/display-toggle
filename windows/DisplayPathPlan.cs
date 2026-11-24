using System.Runtime.InteropServices;
using static DisplayToggle.Ccd;

namespace DisplayToggle;

/// <summary>
/// The CCD (Connecting and Configuring Displays) structs, laid out as in wingdi.h so the arrays
/// QueryDisplayConfig fills can be edited and handed back to SetDisplayConfig. Pure data: the
/// P/Invoke declarations live with their callers (<see cref="DisplayTargets"/>, <see cref="DisplayConfig"/>).
/// </summary>
internal static class Ccd
{
    public const uint DISPLAYCONFIG_PATH_ACTIVE = 0x00000001;
    public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xffffffff;
    public const uint DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE = 1;
    public const uint DISPLAYCONFIG_PIXELFORMAT_32BPP = 4;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
    public const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECTL { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_2DREGION { public uint cx; public uint cy; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx;
        public uint outputTechnology; public uint rotation; public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public DISPLAYCONFIG_RATIONAL hSyncFreq; public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public DISPLAYCONFIG_2DREGION activeSize; public DISPLAYCONFIG_2DREGION totalSize;
        public uint videoStandard; public uint scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_MODE { public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_MODE { public uint width; public uint height; public uint pixelFormat; public POINTL position; }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO { public POINTL PathSourceSize; public RECTL DesktopImageRegion; public RECTL DesktopImageClip; }

    /// <summary>
    /// 64 bytes: the header, then a union whose member <c>infoType</c> selects. The source member's
    /// position is what places a display on the desktop, so the union has to be real, not padding.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 64)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        [FieldOffset(16)] public DISPLAYCONFIG_TARGET_MODE targetMode;
        [FieldOffset(16)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
        [FieldOffset(16)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type; public uint size; public LUID adapterId; public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public uint outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_PREFERRED_MODE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint width; public uint height;
        public DISPLAYCONFIG_TARGET_MODE targetMode;
    }
}

/// <summary>
/// The decisions behind switching one display on or off with a CCD per-path edit, kept free of
/// P/Invoke so they can be unit-tested: which paths and modes go back to SetDisplayConfig, where
/// the desktop origin moves when the primary goes away, whether the layout saved when a display was
/// released may be restored, which source a display can borrow, and where a display lands when no
/// saved layout applies.
/// Mode indices are plain array indices: callers never query with QDC_VIRTUAL_MODE_AWARE.
/// </summary>
internal static class DisplayPathPlan
{
    /// <summary>A path array and the mode array its <c>modeInfoIdx</c> fields index into.</summary>
    public readonly record struct Config(DISPLAYCONFIG_PATH_INFO[] Paths, DISPLAYCONFIG_MODE_INFO[] Modes);

    /// <summary>Built-in panel output technologies — the Windows analogue of the Mac's CGDisplayIsBuiltin.</summary>
    public static bool IsInternalTech(uint tech) =>
        tech == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
        || tech == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
        || tech == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED;

    public static bool SameAdapter(LUID a, LUID b) => a.LowPart == b.LowPart && a.HighPart == b.HighPart;

    public static bool IsActive(in DISPLAYCONFIG_PATH_INFO path) => (path.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0;

    /// <summary>True if the path ends at the target (adapter + target id).</summary>
    public static bool Drives(in DISPLAYCONFIG_PATH_INFO path, LUID adapterId, uint targetId) =>
        path.targetInfo.id == targetId && SameAdapter(path.targetInfo.adapterId, adapterId);

    /// <summary>True if some active path drives the target.</summary>
    public static bool TargetActive(DISPLAYCONFIG_PATH_INFO[] paths, LUID adapterId, uint targetId)
    {
        foreach (ref readonly var p in paths.AsSpan())
            if (IsActive(p) && Drives(p, adapterId, targetId)) return true;
        return false;
    }

    /// <summary>
    /// Turning a display off: the active configuration minus every path to the target, with the
    /// mode array cut down to what the remaining paths reference. If the removed display held the
    /// desktop origin (it was the primary), every remaining source moves by the same offset so a new
    /// one sits at (0,0) — the internal panel if it's still on, else the first remaining — and the
    /// relative layout is kept. Null when no active path would remain: Windows needs a display.
    /// </summary>
    public static Config? WithoutTarget(DISPLAYCONFIG_PATH_INFO[] activePaths, DISPLAYCONFIG_MODE_INFO[] modes,
        LUID adapterId, uint targetId)
    {
        var kept = new List<DISPLAYCONFIG_PATH_INFO>(activePaths.Length);
        foreach (ref readonly var p in activePaths.AsSpan())
            if (IsActive(p) && !Drives(p, adapterId, targetId)) kept.Add(p);
        if (kept.Count == 0) return null;

        var config = Compact(kept.ToArray(), modes);
        KeepOrigin(config);
        return config;
    }

    /// <summary>
    /// Index into <paramref name="allPaths"/> (a QDC_ALL_PATHS result) of an inactive path to the
    /// connected target whose source (adapter + source id) no active path uses — a source can drive
    /// only one display outside clone mode. -1 when there is none.
    /// </summary>
    public static int FreeSourceCandidate(DISPLAYCONFIG_PATH_INFO[] allPaths, LUID adapterId, uint targetId)
    {
        for (int i = 0; i < allPaths.Length; i++)
        {
            ref readonly var p = ref allPaths[i];
            if (IsActive(p) || p.targetInfo.targetAvailable == 0 || !Drives(p, adapterId, targetId)) continue;
            if (!SourceInUse(allPaths, p.sourceInfo)) return i;
        }
        return -1;
    }

    /// <summary>
    /// Whether the configuration saved when a display was released (<see cref="ReleasedLayouts"/>)
    /// may be restored: only if exactly the displays that were on with it are on now, so restoring
    /// turns on, off or moves nothing else. Keys compare ignoring order and case.
    /// </summary>
    public static bool SameOthers(IEnumerable<string> savedOthers, IEnumerable<string> activeKeys) =>
        new HashSet<string>(savedOthers, StringComparer.OrdinalIgnoreCase).SetEquals(activeKeys);

    /// <summary>
    /// Turning a display on when no saved layout applies: the active paths keep their
    /// modes, and the candidate gets a new source mode of the given (preferred) size placed right
    /// of the current desktop, top-aligned with it (<see cref="RightOfDesktop"/>). Its target mode
    /// is left for Windows to pick (<c>SDC_ALLOW_CHANGES</c>).
    /// </summary>
    public static Config WithTargetPlaced(DISPLAYCONFIG_PATH_INFO[] allPaths, DISPLAYCONFIG_MODE_INFO[] modes,
        int candidate, uint width, uint height)
    {
        var active = new List<DISPLAYCONFIG_PATH_INFO>(allPaths.Length);
        foreach (ref readonly var p in allPaths.AsSpan())
            if (IsActive(p)) active.Add(p);
        var config = Compact(active.ToArray(), modes);

        var added = allPaths[candidate];
        var mode = new DISPLAYCONFIG_MODE_INFO
        {
            infoType = DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE,
            id = added.sourceInfo.id,
            adapterId = added.sourceInfo.adapterId,
            sourceMode = new DISPLAYCONFIG_SOURCE_MODE
            {
                width = width, height = height, pixelFormat = DISPLAYCONFIG_PIXELFORMAT_32BPP,
                position = RightOfDesktop(config.Modes),
            },
        };
        added.flags |= DISPLAYCONFIG_PATH_ACTIVE;
        added.sourceInfo.modeInfoIdx = (uint)config.Modes.Length;
        added.targetInfo.modeInfoIdx = DISPLAYCONFIG_PATH_MODE_IDX_INVALID;
        return new Config([.. config.Paths, added], [.. config.Modes, mode]);
    }

    /// <summary>
    /// Where a new display goes: x = the right edge of the bounding box of every source mode, y = its
    /// top. (0,0) when there is no source mode.
    /// </summary>
    public static POINTL RightOfDesktop(DISPLAYCONFIG_MODE_INFO[] modes)
    {
        bool any = false;
        int right = 0, top = 0;
        foreach (ref readonly var m in modes.AsSpan())
        {
            if (m.infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) continue;
            var s = m.sourceMode;
            int r = s.position.x + (int)s.width;
            right = any ? Math.Max(right, r) : r;
            top = any ? Math.Min(top, s.position.y) : s.position.y;
            any = true;
        }
        return new POINTL { x = right, y = top };
    }

    private static bool SourceInUse(DISPLAYCONFIG_PATH_INFO[] paths, in DISPLAYCONFIG_PATH_SOURCE_INFO source)
    {
        foreach (ref readonly var p in paths.AsSpan())
            if (IsActive(p) && p.sourceInfo.id == source.id && SameAdapter(p.sourceInfo.adapterId, source.adapterId))
                return true;
        return false;
    }

    /// <summary>
    /// Keeps only the modes <paramref name="paths"/> reference (in their original order) and
    /// re-points the paths' mode indices at the new positions; an index out of range becomes invalid.
    /// Edits <paramref name="paths"/> in place.
    /// </summary>
    private static Config Compact(DISPLAYCONFIG_PATH_INFO[] paths, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var used = new bool[modes.Length];
        foreach (ref readonly var p in paths.AsSpan())
        {
            if (p.sourceInfo.modeInfoIdx < (uint)modes.Length) used[p.sourceInfo.modeInfoIdx] = true;
            if (p.targetInfo.modeInfoIdx < (uint)modes.Length) used[p.targetInfo.modeInfoIdx] = true;
        }

        var map = new uint[modes.Length];
        var kept = new List<DISPLAYCONFIG_MODE_INFO>(modes.Length);
        for (int i = 0; i < modes.Length; i++)
        {
            if (!used[i]) continue;
            map[i] = (uint)kept.Count;
            kept.Add(modes[i]);
        }

        foreach (ref var p in paths.AsSpan())
        {
            p.sourceInfo.modeInfoIdx = Remap(p.sourceInfo.modeInfoIdx, map);
            p.targetInfo.modeInfoIdx = Remap(p.targetInfo.modeInfoIdx, map);
        }
        return new Config(paths, kept.ToArray());
    }

    private static uint Remap(uint idx, uint[] map) =>
        idx < (uint)map.Length ? map[idx] : DISPLAYCONFIG_PATH_MODE_IDX_INVALID;

    /// <summary>
    /// If no source sits at the desktop origin any more, shift every source so one does: the one
    /// driving an internal panel if any, else the first path's. Offsets between displays are kept.
    /// </summary>
    private static void KeepOrigin(Config config)
    {
        var modes = config.Modes;
        int first = -1, inner = -1;
        foreach (ref readonly var p in config.Paths.AsSpan())
        {
            uint idx = p.sourceInfo.modeInfoIdx;
            if (idx >= (uint)modes.Length || modes[idx].infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) continue;
            var pos = modes[idx].sourceMode.position;
            if (pos.x == 0 && pos.y == 0) return;
            if (first < 0) first = (int)idx;
            if (inner < 0 && IsInternalTech(p.targetInfo.outputTechnology)) inner = (int)idx;
        }

        int origin = inner >= 0 ? inner : first;
        if (origin < 0) return;
        var shift = modes[origin].sourceMode.position;
        foreach (ref var m in modes.AsSpan())
        {
            if (m.infoType != DISPLAYCONFIG_MODE_INFO_TYPE_SOURCE) continue;
            m.sourceMode.position.x -= shift.x;
            m.sourceMode.position.y -= shift.y;
        }
    }
}
