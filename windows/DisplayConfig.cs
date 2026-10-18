using System.Runtime.InteropServices;

namespace DisplayToggle;

/// <summary>
/// In-process topology switching via the CCD API's <c>SetDisplayConfig</c> — the same thing
/// DisplaySwitch.exe / Win+P do, without spawning a helper process.
///   extend   → SDC_TOPOLOGY_EXTEND  (grab the external monitor)
///   internal → SDC_TOPOLOGY_INTERNAL (internal panel only; release the external)
/// Uses the persisted topology database, so the extended arrangement is restored as last used.
/// </summary>
internal static class DisplayConfig
{
    [DllImport("user32.dll")]
    private static extern int SetDisplayConfig(
        uint numPathArrayElements, IntPtr pathArray,
        uint numModeInfoArrayElements, IntPtr modeInfoArray, uint flags);

    private const uint SDC_APPLY             = 0x00000080;
    private const uint SDC_TOPOLOGY_INTERNAL = 0x00000001;
    private const uint SDC_TOPOLOGY_EXTEND   = 0x00000004;

    private const int ERROR_SUCCESS = 0;

    /// <summary>Apply extend topology; returns the Win32 result (0 = success).</summary>
    public static int ExtendResult() => SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_EXTEND);

    /// <summary>Apply internal-only topology; returns the Win32 result (0 = success).</summary>
    public static int InternalResult() => SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SDC_APPLY | SDC_TOPOLOGY_INTERNAL);

    public static bool Extend() => ExtendResult() == ERROR_SUCCESS;
    public static bool Internal() => InternalResult() == ERROR_SUCCESS;
}
