using System.Runtime.InteropServices;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Interop;

/// <summary>
/// A top-down 32bpp DIB section selected into its own memory DC. Dispose releases all GDI handles.
/// </summary>
internal sealed class DibSection : IDisposable
{
    public int Width { get; }
    public int Height { get; }
    public IntPtr Dc { get; }
    public IntPtr Bits { get; }

    private readonly IntPtr _bitmap;
    private readonly IntPtr _oldBitmap;
    private bool _disposed;

    public DibSection(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "DIB dimensions must be positive.");
        Width = width;
        Height = height;
        IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            Dc = NativeMethods.CreateCompatibleDC(screenDc);
            if (Dc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC failed.");
            var bmi = NativeMethods.CreateTopDownBitmapInfo(width, height);
            _bitmap = NativeMethods.CreateDIBSection(screenDc, ref bmi, NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                NativeMethods.DeleteDC(Dc);
                throw new InvalidOperationException($"CreateDIBSection failed for {width}x{height}.");
            }
            Bits = bits;
            _oldBitmap = NativeMethods.SelectObject(Dc, _bitmap);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    /// <summary>Copies the DIB into a new PixelBuffer, optionally forcing alpha to 255.</summary>
    public PixelBuffer ToPixelBuffer(bool forceOpaque)
    {
        var buf = new PixelBuffer(Width, Height);
        Marshal.Copy(Bits, buf.Data, 0, buf.Data.Length);
        if (forceOpaque)
        {
            var d = buf.Data;
            for (int i = 3; i < d.Length; i += 4) d[i] = 255;
        }
        return buf;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_oldBitmap != IntPtr.Zero) NativeMethods.SelectObject(Dc, _oldBitmap);
        if (_bitmap != IntPtr.Zero) NativeMethods.DeleteObject(_bitmap);
        if (Dc != IntPtr.Zero) NativeMethods.DeleteDC(Dc);
    }
}
