using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ModernScreenShot.App.Interop;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Services;

/// <summary>
/// Detects and repairs the "window presents black while its visual tree is healthy" failure:
/// DWM can keep compositing a stale, fully black surface for a window whose first frames were
/// requested while the session/display was unavailable or the GPU surface could not be allocated
/// (observed live as a completely black welcome window that only repainted when an external
/// WM_PRINT forced one). Sampling the window's own on-screen pixels — the same GDI read path as a
/// real capture — exposes the stall, and escalating kicks (frame change → 1-DIP size round-trip →
/// hide/show) force the surface to be reallocated and re-presented.
///
/// Used by the floating image windows (PinWindow, FloatingThumbnailWindow); OobeWindow carries an
/// equivalent inline copy while that file is under separate edits. Topmost no-activate windows are
/// sampled without a foreground check because nothing can occlude them short of another topmost
/// popup; kick decisions additionally require the content itself to contain non-black pixels, so a
/// user who genuinely pinned a black region is never "repaired".
/// </summary>
internal static class PresentationGuard
{
    private const double BlackPresentThreshold = 0.985;
    private const int FirstCheckDelayMs = 1500;  // after the entrance animation has fully settled
    private const int RecheckDelayMs = 700;
    private const int MaxAttempts = 3;

    /// <summary>Arms a black-presentation self-check for <paramref name="window"/>: starting
    /// <paramref name="delayMs"/> after the call it samples what DWM actually presents for the
    /// window's on-screen rect and, when that is ~fully black while <paramref name="expectedBlack"/>
    /// says the content itself is not, kicks the presentation pipeline and re-checks. The provider
    /// is evaluated per check so content swapped after arming (pin paste/replace) stays accurate.
    /// Idle/stopped once the window closes; never throws.</summary>
    internal static void Arm(Window window, Func<double> expectedBlack, int delayMs = FirstCheckDelayMs)
    {
        int attempts = 0;
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (!window.IsLoaded || !window.IsVisible) return; // closed/closing: nothing to guard
                var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return;
                if (window.Opacity < 0.99) return; // mid fade: the sample would read the desktop behind
                double onScreen = SampleBlackFraction(hwnd);
                if (onScreen < BlackPresentThreshold) return; // presentation healthy (or genuinely dark)
                double contentBlack = expectedBlack();
                if (contentBlack >= BlackPresentThreshold) return; // the content itself is black — truth
                attempts++;
                Log.Warn($"Presentation guard: '{window.Title}' presents black on screen (screen black {onScreen:P1}, content black {contentBlack:P1}, attempt {attempts}); kicking the presentation pipeline.");
                Kick(window, hwnd, attempts);
                if (attempts < MaxAttempts)
                {
                    timer.Interval = TimeSpan.FromMilliseconds(RecheckDelayMs);
                    timer.Start();
                }
                else
                {
                    Log.Error($"Presentation guard: '{window.Title}' still presents black after {attempts} kicks; giving up (content is valid).");
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"Presentation guard check failed: {ex.Message}");
            }
        };
        timer.Start();
    }

    /// <summary>Near-black fraction of the bitmap a window is about to show, so the guard can tell
    /// "presentation stalled black" from "the content really is black". Unreadable → 0 (assume visible).</summary>
    internal static double ExpectedBlackFraction(BitmapSource source)
    {
        try
        {
            var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = converted.PixelWidth, h = converted.PixelHeight;
            var data = new byte[w * h * 4];
            converted.CopyPixels(data, w * 4, 0);
            return BlackFrame.NearBlackFraction(data, w, h);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Near-black fraction of a BGRA pixel buffer (transparent surround pixels count as
    /// black — the same pixels read as black on screen when a dark desktop shows through).</summary>
    internal static double BlackFraction(PixelBuffer buffer) => BlackFrame.NearBlackFraction(buffer);

    /// <summary>Near-black fraction of the window's composed on-screen pixels; 0 when unreadable
    /// (a failed sample must never trigger a kick).</summary>
    internal static double SampleBlackFraction(IntPtr hwnd)
    {
        try
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return 0;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return 0;
            using var dib = new DibSection(w, h);
            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return 0;
            try
            {
                if (!NativeMethods.BitBlt(dib.Dc, 0, 0, w, h, screenDc, r.Left, r.Top,
                        NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT)) return 0;
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
            var data = dib.ToPixelBuffer(forceOpaque: false).Data;
            return BlackFrame.NearBlackFraction(data, w, h);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>Escalating repaint kicks: frame change, a 1-DIP size round-trip (forces a new
    /// redirection surface), then a hide/show cycle as the last resort.</summary>
    private static void Kick(Window window, IntPtr hwnd, int attempt)
    {
        switch (attempt)
        {
            case 1:
                NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
                break;
            case 2:
                if (NativeMethods.GetWindowRect(hwnd, out var r))
                {
                    int w = r.Right - r.Left, h = r.Bottom - r.Top;
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w + 1, h + 1,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, w, h,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
                }
                break;
            default:
                window.Hide();
                window.Show();
                break;
        }
    }
}
