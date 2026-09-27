using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Capture;

/// <summary>Captures a single window's true content (even when partially occluded) using PrintWindow, with screen fallback.</summary>
public sealed class WindowCapturer
{
    private readonly ScreenCapturer _screen;

    public WindowCapturer(ScreenCapturer screen) => _screen = screen;

    public static bool IsWindows11 => Environment.OSVersion.Version.Build >= 22000;

    /// <summary>Captures <paramref name="hwnd"/> without activating it. Returns null if the window is gone or minimized.</summary>
    public PixelBuffer? CaptureWindow(IntPtr hwnd, bool transparentCorners, out string title)
    {
        title = "";
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd) || NativeMethods.IsIconic(hwnd)) return null;
        title = NativeMethods.GetWindowTitle(hwnd);

        if (!NativeMethods.GetWindowRect(hwnd, out var wr) || wr.Width <= 0 || wr.Height <= 0) return null;
        var frame = NativeMethods.GetFrameBounds(hwnd).ToPixelRect();
        var windowRect = wr.ToPixelRect();
        // Frame bounds relative to the full window rect (strips invisible resize borders / DWM shadow margins).
        var cropRel = frame.Offset(-windowRect.X, -windowRect.Y).Intersect(new PixelRect(0, 0, windowRect.Width, windowRect.Height));
        if (cropRel.IsEmpty) cropRel = new PixelRect(0, 0, windowRect.Width, windowRect.Height);

        PixelBuffer? image = TryPrintWindow(hwnd, windowRect.Width, windowRect.Height, cropRel);
        if (image is null)
        {
            Log.Warn($"PrintWindow produced no content for '{title}' (0x{hwnd:X}); falling back to screen capture.");
            var onScreen = frame.IsEmpty ? windowRect : frame;
            image = _screen.Capture(onScreen, includeCursor: false);
        }

        if (transparentCorners && ShouldRoundCorners(hwnd))
        {
            uint dpi = NativeMethods.GetDpiForWindow(hwnd);
            double scale = dpi > 0 ? dpi / 96.0 : 1.0;
            double radius = NativeMethods.GetCornerPreference(hwnd) == NativeMethods.DWMWCP_ROUNDSMALL ? 4 * scale : 8 * scale;
            image = CornerRounding.Apply(image, radius);
        }
        return image;
    }

    /// <summary>Captures the current foreground window (resolved to its root), skipping this app's own windows.</summary>
    public PixelBuffer? CaptureActiveWindow(bool transparentCorners, out string title, out PixelRect bounds, out IntPtr hwnd)
    {
        bounds = default;
        hwnd = ResolveActiveWindow();
        if (hwnd == IntPtr.Zero)
        {
            title = "";
            return null;
        }
        bounds = NativeMethods.GetFrameBounds(hwnd).ToPixelRect();
        return CaptureWindow(hwnd, transparentCorners, out title);
    }

    /// <summary>Foreground root window, or the topmost non-own window under z-order if our own window is foreground.</summary>
    public static IntPtr ResolveActiveWindow()
    {
        var fg = NativeMethods.GetForegroundWindow();
        if (fg != IntPtr.Zero)
        {
            var root = NativeMethods.GetAncestor(fg, NativeMethods.GA_ROOT);
            if (root != IntPtr.Zero) fg = root;
            NativeMethods.GetWindowThreadProcessId(fg, out int pid);
            if (pid != Environment.ProcessId && NativeMethods.IsWindowVisible(fg) && fg != NativeMethods.GetShellWindow()) return fg;
        }
        var list = new WindowEnumerator().GetTopLevelWindows();
        return list.Count > 0 ? list[0].Handle : IntPtr.Zero;
    }

    private static bool ShouldRoundCorners(IntPtr hwnd)
    {
        if (!IsWindows11) return false;
        if (NativeMethods.IsZoomed(hwnd)) return false;
        if (NativeMethods.GetCornerPreference(hwnd) == NativeMethods.DWMWCP_DONOTROUND) return false;
        long style = NativeMethods.GetStyle(hwnd);
        return (style & NativeMethods.WS_CHILD) == 0;
    }

    private static PixelBuffer? TryPrintWindow(IntPtr hwnd, int width, int height, PixelRect cropRel)
    {
        try
        {
            PixelBuffer full;
            using (var dib = new DibSection(width, height))
            {
                if (!NativeMethods.PrintWindow(hwnd, dib.Dc, NativeMethods.PW_RENDERFULLCONTENT)) return null;
                full = dib.ToPixelBuffer(forceOpaque: true);
            }
            var cropped = cropRel.X == 0 && cropRel.Y == 0 && cropRel.Width == width && cropRel.Height == height ? full : full.Crop(cropRel);
            return IsBlank(cropped) ? null : cropped;
        }
        catch (InvalidOperationException ex)
        {
            Log.Error("PrintWindow capture failed", ex);
            return null;
        }
    }

    /// <summary>True if every sampled pixel is pure black (typical PrintWindow failure for GPU surfaces).</summary>
    private static bool IsBlank(PixelBuffer img)
    {
        var d = img.Data;
        int step = Math.Max(1, (img.Width * img.Height) / 20000) * 4;
        for (int i = 0; i < d.Length; i += step)
            if (d[i] != 0 || d[i + 1] != 0 || d[i + 2] != 0) return false;
        return true;
    }
}
