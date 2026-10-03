using System.Runtime.InteropServices;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Capture;

/// <summary>Captures arbitrary physical-pixel rectangles of the virtual screen via GDI BitBlt.</summary>
public sealed class ScreenCapturer
{
    public PixelBuffer Capture(PixelRect physicalRect, bool includeCursor)
    {
        if (physicalRect.IsEmpty) throw new ArgumentException("Capture rectangle is empty.", nameof(physicalRect));
        using var dib = new DibSection(physicalRect.Width, physicalRect.Height);
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC(NULL) failed.");
        bool reRead = false;
        try
        {
            if (!BitBltFrom(dib, screenDc, physicalRect))
                throw new InvalidOperationException($"BitBlt failed (error {Marshal.GetLastWin32Error()}).");
            // A BitBlt issued while the display/session is transitioning (sleep, resolution or DPI
            // change, secure-desktop switch) can succeed and return a fully black frame. One short
            // re-read filters the transient case; a genuinely black screen reads black twice and is
            // kept as the truth — never rejected. The re-read must run while screenDc is still held.
            // Probe the DIB in place (≤20k samples, no buffer copy) — a managed copy here would
            // materialize the full frame (≈30 MB at virtual-screen size) on every capture.
            if (BlackFrame.NearBlackFraction(dib.Bits, dib.Width, dib.Height) > 0.995)
            {
                Log.Warn($"Screen capture of {physicalRect} read fully black; re-reading once.");
                System.Threading.Thread.Sleep(120);
                if (!BitBltFrom(dib, screenDc, physicalRect))
                    throw new InvalidOperationException($"BitBlt failed (error {Marshal.GetLastWin32Error()}).");
                reRead = true;
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
        // The cursor must be painted into the DIB before the pixels are extracted, otherwise
        // "capture cursor" silently bakes nothing into the result.
        if (reRead)
        {
            // Sample before the cursor is drawn so the log reflects screen content, not the cursor.
            var sample = dib.ToPixelBuffer(forceOpaque: true);
            Log.Warn($"Screen capture re-read: {(BlackFrame.NearBlackFraction(sample) > 0.995 ? "still fully black (kept as-is)" : "content recovered on the second read")}.");
        }
        if (includeCursor) DrawCursor(dib.Dc, physicalRect.X, physicalRect.Y);
        var buffer = dib.ToPixelBuffer(forceOpaque: true);
        return buffer;
    }

    private static bool BitBltFrom(DibSection dib, IntPtr screenDc, PixelRect rect) =>
        NativeMethods.BitBlt(dib.Dc, 0, 0, rect.Width, rect.Height, screenDc,
            rect.X, rect.Y, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT);

    /// <summary>Draws the current cursor into a DC whose origin corresponds to (originX, originY) on screen.</summary>
    internal static void DrawCursor(IntPtr hdc, int originX, int originY)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!NativeMethods.GetCursorInfo(ref ci) || (ci.flags & NativeMethods.CURSOR_SHOWING) == 0 || ci.hCursor == IntPtr.Zero) return;
        int hx = 0, hy = 0;
        if (NativeMethods.GetIconInfo(ci.hCursor, out var ii))
        {
            hx = ii.xHotspot; hy = ii.yHotspot;
            if (ii.hbmMask != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmMask);
            if (ii.hbmColor != IntPtr.Zero) NativeMethods.DeleteObject(ii.hbmColor);
        }
        NativeMethods.DrawIconEx(hdc, ci.ptScreenPos.X - hx - originX, ci.ptScreenPos.Y - hy - originY, ci.hCursor, 0, 0, 0, IntPtr.Zero, NativeMethods.DI_NORMAL);
    }
}
