using System.Runtime.InteropServices;

namespace DisplayToggle;

/// <summary>
/// Thin P/Invoke layer over <c>EnumDisplayDevices</c> to enumerate connected monitors and their
/// attached/primary state. (Topology changes go through <see cref="DisplayConfig"/>; richer
/// per-monitor identity comes from <see cref="DisplayTargets"/>.)
/// </summary>
internal static class NativeDisplay
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public uint StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [Flags]
    private enum DisplayDeviceStateFlags : uint
    {
        AttachedToDesktop = 0x00000001,
        MirroringDriver   = 0x00000008,
        PrimaryDevice     = 0x00000004,
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(
        string? lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

    public sealed record DisplayInfo(
        string DeviceName,   // e.g. \\.\DISPLAY2  — stable device key
        string FriendlyName, // monitor model / "Display 2"
        bool   IsPrimary,
        bool   IsAttached);  // attached to desktop == currently "on"

    /// <summary>All real (non-mirror) display adapters that have a monitor attached.</summary>
    public static List<DisplayInfo> Enumerate()
    {
        var result = new List<DisplayInfo>();
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };

        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++)
        {
            var state = (DisplayDeviceStateFlags)dd.StateFlags;
            if (state.HasFlag(DisplayDeviceStateFlags.MirroringDriver)) continue;

            string adapter = dd.DeviceName;
            bool attached  = state.HasFlag(DisplayDeviceStateFlags.AttachedToDesktop);
            bool primary   = state.HasFlag(DisplayDeviceStateFlags.PrimaryDevice);

            // Resolve the monitor child device for a friendly name (and to confirm a
            // physical monitor is present — adapters with no monitor are skipped).
            string friendly = FriendlyName(adapter, out bool hasMonitor);
            if (!attached && !hasMonitor) continue; // empty adapter slot

            result.Add(new DisplayInfo(adapter, friendly, primary, attached));
            dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>();
        }
        return result;
    }

    private static string FriendlyName(string adapter, out bool hasMonitor)
    {
        var mon = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        hasMonitor = EnumDisplayDevices(adapter, 0, ref mon, 0);
        if (hasMonitor && !string.IsNullOrWhiteSpace(mon.DeviceString))
            return mon.DeviceString;
        // Fall back to a human-ish version of \\.\DISPLAYn
        string tail = adapter.Replace(@"\\.\DISPLAY", "");
        return $"Display {tail}";
    }
}
