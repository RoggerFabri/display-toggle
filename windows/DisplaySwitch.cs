using System.Diagnostics;

namespace DisplayToggle;

/// <summary>
/// Drives the shared monitor via Windows' built-in <c>DisplaySwitch.exe</c> — the exact mechanism
/// behind Win+P (Extend / PC screen only):
///   /extend   → extend the desktop onto the external monitor (grab the external monitor)
///   /internal → show only the laptop's internal panel (release the external monitor → the Mac can take it)
/// This is Windows' own topology switch, so it handles primary reassignment for us.
/// </summary>
internal static class DisplaySwitch
{
    public static bool Extend() => Run("/extend");
    public static bool Internal() => Run("/internal");

    /// <summary>
    /// Launch DisplaySwitch.exe; true if it started. Its exit code says nothing about whether the
    /// topology changed, so callers verify the result by re-reading the display state.
    /// </summary>
    private static bool Run(string arg)
    {
        try
        {
            var psi = new ProcessStartInfo("DisplaySwitch.exe", arg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            return p is not null;
        }
        catch { return false; }
    }
}
