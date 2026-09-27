using System.Runtime.InteropServices;
using ModernScreenShot.App.Interop;
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
        try
        {
            if (!NativeMethods.BitBlt(dib.Dc, 0, 0, physicalRect.Width, physicalRect.Height, screenDc,
                    physicalRect.X, physicalRect.Y, NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT))
                throw new InvalidOperationException($"BitBlt failed (error {Marshal.GetLastWin32Error()}).");
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
        if (includeCursor) DrawCursor(dib.Dc, physicalRect.X, physicalRect.Y);
        return dib.ToPixelBuffer(forceOpaque: true);
    }

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
