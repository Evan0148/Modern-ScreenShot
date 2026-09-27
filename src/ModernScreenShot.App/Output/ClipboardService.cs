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
                ok = NativeMethods.SetClipboardData(NativeMethods.CF_DIBV5, ToHGlobal(dibV5)) != IntPtr.Zero
                     && NativeMethods.SetClipboardData(PngFormat, ToHGlobal(png)) != IntPtr.Zero;
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
        WriteInt32(data, 104, 4);                              // bV5Intent = LCS_GM_IMAGES

        // Flip rows bottom-up so the DIB matches the classic layout.
        for (int y = 0; y < image.Height; y++)
        {
            int src = y * image.Stride;
            int dst = headerSize + (image.Height - 1 - y) * image.Stride;
            Array.Copy(image.Data, src, data, dst, image.Stride);
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
