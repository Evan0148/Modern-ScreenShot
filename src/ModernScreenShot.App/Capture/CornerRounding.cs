using System.Reflection;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Capture;

/// <summary>
/// Rounds window-capture corners. Prefers Core's <c>FrameEffect.RoundCorners(PixelBuffer, double)</c> when present
/// (resolved at runtime so App builds independently of Core's T2 progress); otherwise uses a private anti-aliased mask.
/// </summary>
internal static class CornerRounding
{
    private static readonly Func<PixelBuffer, double, PixelBuffer>? CoreImpl = ResolveCore();

    public static bool UsesCore => CoreImpl is not null;

    public static PixelBuffer Apply(PixelBuffer src, double radius) =>
        CoreImpl is not null ? CoreImpl(src, radius) : ApplyFallback(src, radius);

    private static Func<PixelBuffer, double, PixelBuffer>? ResolveCore()
    {
        var type = typeof(PixelBuffer).Assembly.GetType("ModernScreenShot.Core.Imaging.FrameEffect");
        var method = type?.GetMethod("RoundCorners", BindingFlags.Public | BindingFlags.Static, [typeof(PixelBuffer), typeof(double)]);
        if (method is null || method.ReturnType != typeof(PixelBuffer))
        {
            Log.Info("FrameEffect.RoundCorners not available in Core; using App fallback corner mask.");
            return null;
        }
        return method.CreateDelegate<Func<PixelBuffer, double, PixelBuffer>>();
    }

    /// <summary>Straight-alpha corner mask with 4x4 supersampled coverage along the arc.</summary>
    private static PixelBuffer ApplyFallback(PixelBuffer src, double radius)
    {
        var dst = src.Clone();
        double r = Math.Min(radius, Math.Min(src.Width, src.Height) / 2.0);
        if (r <= 0.5) return dst;
        int ri = (int)Math.Ceiling(r);
        for (int y = 0; y < ri; y++)
        {
            for (int x = 0; x < ri; x++)
            {
                double cov = Coverage(x, y, r);
                if (cov >= 1) continue;
                Scale(dst, x, y, cov);
                Scale(dst, dst.Width - 1 - x, y, cov);
                Scale(dst, x, dst.Height - 1 - y, cov);
                Scale(dst, dst.Width - 1 - x, dst.Height - 1 - y, cov);
            }
        }
        return dst;
    }

    private static double Coverage(int px, int py, double r)
    {
        const int n = 4;
        int inside = 0;
        for (int sy = 0; sy < n; sy++)
            for (int sx = 0; sx < n; sx++)
            {
                double dx = r - (px + (sx + 0.5) / n);
                double dy = r - (py + (sy + 0.5) / n);
                if (dx <= 0 || dy <= 0 || dx * dx + dy * dy <= r * r) inside++;
            }
        return inside / (double)(n * n);
    }

    private static void Scale(PixelBuffer b, int x, int y, double cov)
    {
        int i = y * b.Stride + x * 4 + 3;
        b.Data[i] = (byte)Math.Round(b.Data[i] * cov);
    }
}
