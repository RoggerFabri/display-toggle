using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace DisplayToggle;

/// <summary>
/// Draws the tray glyph in code (no embedded .ico), matching the Mac app's two states:
/// ON  = solid monitor outline, OFF = dimmed. The glyph colour follows the taskbar theme
/// (dark glyph on a light taskbar, light glyph on a dark taskbar) so it stays visible in both.
/// A green dot marks a live peer connection. Rendered at the tray's exact pixel size for the
/// current DPI (see <see cref="Make"/>), so the shell never rescales it.
/// </summary>
internal static class TrayIcons
{
    /// <summary>
    /// A rendered glyph that owns its GDI+-created HICON. The handle is used as-is. It isn't
    /// <c>Icon.Clone()</c>d, because that goes through <c>CopyImage</c>, which re-creates the bitmap
    /// against the current display. A copy made while the session is disconnected or being switched
    /// to or from RDP can come out blank, and the shell keeps showing that blank copy.
    /// </summary>
    internal sealed class Glyph : IDisposable
    {
        private IntPtr _hicon;
        public Icon Icon { get; }

        public Glyph(IntPtr hicon) { _hicon = hicon; Icon = Icon.FromHandle(hicon); }

        public void Dispose()
        {
            Icon.Dispose(); // FromHandle doesn't own the handle, so this doesn't destroy it
            if (_hicon != IntPtr.Zero) { DestroyIcon(_hicon); _hicon = IntPtr.Zero; }
        }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    private const int SM_CXSMICON = 49;

    /// <summary>
    /// The tray's small-icon size in pixels at the taskbar's current DPI (16 at 100%, 20 at 125%,
    /// 24 at 150%, 32 at 200%). Queried live: the app is PerMonitorV2, so the DPI-unaware
    /// <c>SystemInformation.SmallIconSize</c> would stay frozen at the startup scale.
    /// </summary>
    public static int CurrentTraySize()
    {
        try
        {
            uint dpi = 0;
            IntPtr tray = FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero) dpi = GetDpiForWindow(tray);
            if (dpi == 0) dpi = GetDpiForSystem();
            if (dpi == 0) dpi = 96;
            int size = GetSystemMetricsForDpi(SM_CXSMICON, dpi);
            return size > 0 ? size : 16;
        }
        catch { return 16; } // pre-1607 Windows: no per-DPI metrics
    }

    /// <summary>True when the taskbar/system uses the light theme (so we should draw a dark glyph).</summary>
    public static bool IsLightTaskbar()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            // SystemUsesLightTheme controls the taskbar/tray; 1 = light, 0/absent = dark.
            return key?.GetValue("SystemUsesLightTheme") is int v && v != 0;
        }
        catch { return false; }
    }

    /// <summary>
    /// The glyph's geometry at <paramref name="size"/> px, laid out on the pixel grid (from the
    /// original 32px design: screen 22×15 at (5,6), neck 4×4, foot 14 wide) with whole-pixel strokes,
    /// so it stays crisp at every scale instead of being a 32px bitmap the shell resamples.
    /// </summary>
    internal static Rectangle[] MonitorParts(int size, out int stroke)
    {
        int Px(float design) => (int)MathF.Round(design * size / 32f);
        stroke = Math.Max(1, (int)MathF.Round(size / 11f));        // 16→1, 20/24→2, 32→3
        int left = Px(5), right = size - left;                     // horizontally symmetric
        int top = Px(6), bottom = Math.Max(top + 2 * stroke + 1, Px(21));
        int footW = Math.Max(3 * stroke, Px(14));
        if ((size - footW) % 2 != 0) footW++;                      // centre on whole pixels
        int footTop = Math.Min(size - stroke - Math.Max(1, size / 16), Math.Max(bottom + 1, Px(25)));
        int neckW = Math.Max(stroke, Px(4));
        if ((size - neckW) % 2 != 0) neckW++;
        return new[]
        {
            new Rectangle(left, top, right - left, stroke),                    // screen: top edge
            new Rectangle(left, bottom - stroke, right - left, stroke),        //         bottom edge
            new Rectangle(left, top, stroke, bottom - top),                    //         left edge
            new Rectangle(right - stroke, top, stroke, bottom - top),          //         right edge
            new Rectangle((size - neckW) / 2, bottom, neckW, footTop - bottom),// neck
            new Rectangle((size - footW) / 2, footTop, footW, stroke),         // foot
        };
    }

    public static Glyph Make(bool on, bool peerConnected, int size)
    {
        size = Math.Clamp(size, 16, 256);
        using var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.None;          // whole-pixel rectangles: no AA blur
            g.PixelOffsetMode = PixelOffsetMode.Half;

            // Follow the taskbar theme: dark glyph on a light bar, light glyph on a dark bar. The
            // theme flag doesn't always match what the tray actually shows (accent-coloured or
            // otherwise restyled taskbars, RDP), so the glyph also gets a 1px contrasting halo — like
            // the connection dot's ring — that keeps it visible on any background.
            bool light = IsLightTaskbar();
            Color fg = light ? Color.FromArgb(32, 32, 32) : Color.White;
            Color halo = light ? Color.FromArgb(150, Color.White) : Color.FromArgb(150, 0, 0, 0);
            Color color = on ? fg : Color.FromArgb(150, fg); // dim (not near-invisible) when off

            var parts = MonitorParts(size, out _);
            using (var haloBrush = new SolidBrush(halo))
                foreach (var r in parts) g.FillRectangle(haloBrush, Rectangle.Inflate(r, 1, 1));
            // Punch the glyph's own pixels out of the halo first, so the dimmed OFF colour isn't
            // composited over it, then paint the glyph.
            g.CompositingMode = CompositingMode.SourceCopy;
            using (var clear = new SolidBrush(Color.Transparent))
                foreach (var r in parts) g.FillRectangle(clear, r);
            using (var brush = new SolidBrush(color))
                foreach (var r in parts) g.FillRectangle(brush, r);
            g.CompositingMode = CompositingMode.SourceOver;

            if (peerConnected)
            {
                // Green connection dot, top-right, ~1/3 of the icon, with a dark ring so it reads
                // on any taskbar. Anti-aliased: a circle this small needs it.
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int d = Math.Max(5, (int)MathF.Round(size * 11f / 32f));
                float ring = Math.Max(1f, size / 32f);
                var dot = new RectangleF(size - d - ring, ring, d, d);
                using var ringBrush = new SolidBrush(Color.FromArgb(160, 0, 0, 0));
                using var green = new SolidBrush(Color.FromArgb(52, 199, 89)); // Apple systemGreen
                g.FillEllipse(ringBrush, dot.X - ring, dot.Y - ring, dot.Width + 2 * ring, dot.Height + 2 * ring);
                g.FillEllipse(green, dot);
            }
        }

        return new Glyph(bmp.GetHicon());
    }
}
