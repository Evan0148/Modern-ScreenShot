using System;
using System.Runtime.InteropServices;

namespace ModernScreenShot.Core.Imaging;

/// <summary>
/// Shared "is this frame black" statistics used by every guard that must tell a real black
/// capture from a failed one: WindowCapturer rejects PrintWindow buffers whose client area never
/// rendered, ScreenCapturer re-reads a frame that BitBlt returned during a display transition,
/// and PresentationGuard/OobeWindow compare a window's on-screen sample against its content.
/// All of them share this sampler so the near-black definition cannot drift between sites.
/// </summary>
public static class BlackFrame
{
    /// <summary>r+g+b below this counts as near-black. #202020 (dark-theme background) sums to 96,
    /// so theme backgrounds never count; true #000 and transparent (RGB=0, alpha=0) pixels do.</summary>
    public const int NearBlackSum = 30;

    /// <summary>Sampled fraction of near-black pixels (BGRA, 4 bytes per pixel, stride ignored —
    /// the caller passes a tightly-packed buffer or PixelBuffer whose Stride is Width*4).</summary>
    public static double NearBlackFraction(PixelBuffer buffer)
    {
        var d = buffer.Data;
        long total = (long)buffer.Width * buffer.Height;
        return SampleFraction(d, total);
    }

    /// <summary>Near-black fraction over a tightly-packed BGRA byte buffer of w×h pixels.</summary>
    public static double NearBlackFraction(byte[] bgra, int width, int height)
    {
        return SampleFraction(bgra, (long)width * height);
    }

    /// <summary>Near-black fraction sampled in place from unmanaged tightly-packed BGRA bits (a
    /// DIB section's buffer): a full virtual-screen copy would cost ~30 MB per probe on this
    /// machine just to sample ≤20k pixels. RGB channels only, so alpha is irrelevant.</summary>
    public static double NearBlackFraction(IntPtr bits, int width, int height)
    {
        if (bits == IntPtr.Zero || width <= 0 || height <= 0) return 1;
        long total = (long)width * height;
        long step = Math.Max(1, total / 20000); // sample ≤ ~20k pixels regardless of frame size
        long black = 0, sampled = 0;
        for (long px = 0; px < total; px += step)
        {
            IntPtr p = new IntPtr(bits.ToInt64() + px * 4);
            sampled++;
            if (Marshal.ReadByte(p) + Marshal.ReadByte(p, 1) + Marshal.ReadByte(p, 2) < NearBlackSum) black++;
        }
        return sampled == 0 ? 1 : (double)black / sampled;
    }

    private static double SampleFraction(byte[] d, long total)
    {
        if (d is null || total == 0 || d.Length < total * 4) return 1;
        long step = Math.Max(1, total / 20000); // sample ≤ ~20k pixels regardless of frame size
        long black = 0, sampled = 0;
        for (long px = 0; px < total; px += step)
        {
            int i = (int)px * 4;
            sampled++;
            if (d[i] + d[i + 1] + d[i + 2] < NearBlackSum) black++;
        }
        return sampled == 0 ? 1 : (double)black / sampled;
    }
}
