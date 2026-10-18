using System.Runtime.InteropServices;

namespace DisplayToggle;

/// <summary>
/// Enumerates monitor "targets" via the CCD API (QueryDisplayConfig) to get real EDID friendly
/// names and to tell the built-in laptop panel (INTERNAL output technology) from external monitors
/// — the Windows analogue of the Mac's CGDisplayIsBuiltin check.
/// </summary>
internal static class DisplayTargets
{
    private const uint QDC_ONLY_ACTIVE_PATHS = 0x00000002;
    private const int ERROR_INSUFFICIENT_BUFFER = 122;
    private const uint DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME = 2;
    private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL = 0x80000000;
    private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED = 11;
    private const uint DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED = 13;

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_RATIONAL { public uint Numerator; public uint Denominator; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx;
        public uint outputTechnology; public uint rotation; public uint scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate; public uint scanLineOrdering;
        public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;
    }

    // Contents unused — only the correct element size (64 bytes) matters for array marshaling.
    [StructLayout(LayoutKind.Sequential, Size = 64)]
    private struct DISPLAYCONFIG_MODE_INFO { public uint infoType; public uint id; public LUID adapterId; }

    [StructLayout(LayoutKind.Sequential)]
    private struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public uint type; public uint size; public LUID adapterId; public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAYCONFIG_TARGET_DEVICE_NAME
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

    [DllImport("user32.dll")]
    private static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPathArrayElements, out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    private static extern int QueryDisplayConfig(
        uint flags, ref uint numPathArrayElements, [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements, [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray, IntPtr currentTopologyId);

    [DllImport("user32.dll")]
    private static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME deviceName);

    public sealed record Target(string Name, bool IsInternal);

    private static bool IsInternalTech(uint tech) =>
        tech == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_INTERNAL
        || tech == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_DISPLAYPORT_EMBEDDED
        || tech == DISPLAYCONFIG_OUTPUT_TECHNOLOGY_UDI_EMBEDDED;

    /// <summary>Currently-active monitor targets (real friendly names), flagged internal vs external.</summary>
    public static List<Target> All()
    {
        var result = new List<Target>();
        try
        {
            // The topology can change between sizing and querying (e.g. mid-switch), which fails the
            // query with ERROR_INSUFFICIENT_BUFFER: size again and retry, as the API docs prescribe.
            DISPLAYCONFIG_PATH_INFO[] paths;
            uint numPaths, numModes;
            for (int attempt = 0; ; attempt++)
            {
                if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out numPaths, out numModes) != 0) return result;
                paths = new DISPLAYCONFIG_PATH_INFO[numPaths];
                var modes = new DISPLAYCONFIG_MODE_INFO[numModes];
                int rc = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref numPaths, paths, ref numModes, modes, IntPtr.Zero);
                if (rc == 0) break;
                if (rc != ERROR_INSUFFICIENT_BUFFER || attempt >= 2) return result;
            }

            var seen = new HashSet<string>();
            for (int i = 0; i < numPaths; i++)
            {
                var t = paths[i].targetInfo;
                var q = new DISPLAYCONFIG_TARGET_DEVICE_NAME();
                q.header.type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME;
                q.header.size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>();
                q.header.adapterId = t.adapterId;
                q.header.id = t.id;
                if (DisplayConfigGetDeviceInfo(ref q) != 0) continue;

                bool isInternal = IsInternalTech(q.outputTechnology);
                string name = string.IsNullOrWhiteSpace(q.monitorFriendlyDeviceName)
                    ? (isInternal ? "Built-in display" : "External display")
                    : q.monitorFriendlyDeviceName;

                string key = string.IsNullOrEmpty(q.monitorDevicePath) ? name : q.monitorDevicePath;
                if (seen.Add(key)) result.Add(new Target(name, isInternal));
            }
        }
        catch { /* CCD unavailable → caller falls back */ }
        return result;
    }

    /// <summary>Friendly names of external (non-built-in) monitors, in enumeration order.</summary>
    public static List<string> ExternalNames() =>
        All().Where(t => !t.IsInternal).Select(t => t.Name).ToList();
}
