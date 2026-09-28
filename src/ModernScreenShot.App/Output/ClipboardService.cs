using System.Runtime.InteropServices;
using System.Windows;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Output;

/// <summary>
/// Places images on the clipboard as CF_DIBV5 plus a "PNG" stream so both classic consumers
/// (Office, Paint) and modern ones (browsers, chat apps) get alpha-preserving pixels.
/// OpenClipboard fails while another app holds the clipboard, hence the retry loop.
/// </summary>
public sealed class ClipboardService
{
    private static readonly uint PngFormat = NativeMethods.RegisterClipboardFormatW("PNG");
    private const int Retries = 10;

    public bool TryPutImage(PixelBuffer image)
    {
        byte[] png = BitmapInterop.EncodePng(image);
        byte[] dibV5 = CreateDibV5(image);

        for (int attempt = 1; attempt <= Retries; attempt++)
        {
            if (!NativeMethods.OpenClipboard(IntPtr.Zero))
            {
                Thread.Sleep(50);
                continue;
            }
            bool ok = false;
            try
            {
                if (!NativeMethods.EmptyClipboard())
                {
                    NativeMethods.CloseClipboard();
                    Thread.Sleep(50);
                    continue;
                }
                // SetClipboardData transfers ownership on success; on failure we still own the
                // handle and must free it to avoid leaking GMEM_MOVEABLE memory per attempt.
                var hDib = ToHGlobal(dibV5);
                var hPng = ToHGlobal(png);
                bool dibOk = NativeMethods.SetClipboardData(NativeMethods.CF_DIBV5, hDib) != IntPtr.Zero;
                if (!dibOk) NativeMethods.GlobalFree(hDib);
                bool pngOk = NativeMethods.SetClipboardData(PngFormat, hPng) != IntPtr.Zero;
                if (!pngOk) NativeMethods.GlobalFree(hPng);
                ok = dibOk && pngOk;
                if (!ok)
                    Log.Warn($"SetClipboardData failed (error {Marshal.GetLastWin32Error()}).");
            }
            finally
            {
                NativeMethods.CloseClipboard();
            }
            if (ok)
            {
                Log.Info($"Capture {image.Width}x{image.Height} copied to clipboard (PNG + DIBV5).");
                return true;
            }
            Thread.Sleep(50);
        }
        Log.Error("Clipboard stayed locked after retries; copy aborted.");
        return false;
    }

    private static IntPtr ToHGlobal(byte[] data)
    {
        IntPtr h = NativeMethods.GlobalAlloc(NativeMethods.GMEM_MOVEABLE, (nuint)data.Length);
        if (h == IntPtr.Zero) throw new OutOfMemoryException("GlobalAlloc failed for clipboard payload.");
        IntPtr p = NativeMethods.GlobalLock(h);
        try
        {
            Marshal.Copy(data, 0, p, data.Length);
        }
        finally
        {
            NativeMethods.GlobalUnlock(h);
        }
        return h;
    }

    /// <summary>Builds a bottom-up 32bpp BITMAPV5HEADER + pixels (BI_BITFIELDS with alpha mask).</summary>
    internal static byte[] CreateDibV5(PixelBuffer image)
    {
        const int headerSize = 124;
        int pixelBytes = image.Height * image.Stride;
        var data = new byte[headerSize + pixelBytes];

        WriteInt32(data, 0, headerSize);                       // bV5Size
        WriteInt32(data, 4, image.Width);                      // bV5Width
        WriteInt32(data, 8, image.Height);                     // bV5Height (positive = bottom-up)
        WriteInt16(data, 12, 1);                               // bV5Planes
        WriteInt16(data, 14, 32);                              // bV5BitCount
        WriteInt32(data, 16, 3);                               // bV5Compression = BI_BITFIELDS
        WriteInt32(data, 20, pixelBytes);                      // bV5SizeImage
        WriteInt32(data, 24, 3780);                            // bV5XPelsPerMeter
        WriteInt32(data, 28, 3780);                            // bV5YPelsPerMeter
        WriteInt32(data, 40, 0x00FF0000);                      // bV5RedMask
        WriteInt32(data, 44, 0x0000FF00);                      // bV5GreenMask
        WriteInt32(data, 48, 0x000000FF);                      // bV5BlueMask
        WriteInt32(data, 52, unchecked((int)0xFF000000));      // bV5AlphaMask
        WriteInt32(data, 56, 0x73524742);                      // bV5CSType = 'sRGB'
        WriteInt32(data, 108, 4);                              // bV5Intent = LCS_GM_IMAGES

        // Flip rows bottom-up to the classic DIB layout AND premultiply the RGB channels. A DIBV5
        // carrying an alpha mask is consumed as premultiplied by alpha-aware apps; handing them
        // straight-alpha pixels makes soft edges un-premultiply into saturated random hues (the
        // "rainbow halo" around mac-style shadows), while alpha-ignoring apps draw the raw RGB as
        // an opaque tinted stroke. The clipboard "PNG" stream stays straight-alpha (PNG semantics).
        for (int y = 0; y < image.Height; y++)
        {
            var s = image.Data.AsSpan(y * image.Stride, image.Stride);
            var d = data.AsSpan(headerSize + (image.Height - 1 - y) * image.Stride, image.Stride);
            for (int x = 0; x < image.Width; x++)
            {
                int i = x * 4;
                int a = s[i + 3];
                if (a == 255)
                {
                    d[i] = s[i]; d[i + 1] = s[i + 1]; d[i + 2] = s[i + 2]; d[i + 3] = 255;
                }
                else if (a == 0)
                {
                    d[i] = 0; d[i + 1] = 0; d[i + 2] = 0; d[i + 3] = 0;
                }
                else
                {
                    d[i] = (byte)((s[i] * a + 127) / 255);
                    d[i + 1] = (byte)((s[i + 1] * a + 127) / 255);
                    d[i + 2] = (byte)((s[i + 2] * a + 127) / 255);
                    d[i + 3] = (byte)a;
                }
            }
        }
        return data;
    }

    private static void WriteInt32(byte[] b, int offset, int value)
    {
        b[offset] = (byte)value;
        b[offset + 1] = (byte)(value >> 8);
        b[offset + 2] = (byte)(value >> 16);
        b[offset + 3] = (byte)(value >> 24);
    }

    private static void WriteInt16(byte[] b, int offset, short value)
    {
        b[offset] = (byte)value;
        b[offset + 1] = (byte)(value >> 8);
    }
}
