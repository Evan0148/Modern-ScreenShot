namespace ModernScreenShot.Core.Imaging;

public static class ReflectionEffect
{
    public static int ReflectionHeight(PixelBuffer src, ReflectionOptions o) =>
        Math.Clamp((int)Math.Round(src.Height * Math.Clamp(o.Height, 0, 1)), 0, src.Height);

    /// <summary>Returns only the faded, mirrored strip (null if disabled or empty).</summary>
    public static PixelBuffer? RenderReflectionOnly(PixelBuffer src, ReflectionOptions o)
    {
        if (!o.Enabled) return null;
        int rh = ReflectionHeight(src, o);
        if (rh <= 0) return null;
        var r = new PixelBuffer(src.Width, rh);
        double start = Math.Clamp(o.StartOpacity, 0, 1), end = Math.Clamp(o.EndOpacity, 0, 1);
        Parallel.For(0, rh, y =>
        {
            double t = rh == 1 ? 0 : (double)y / (rh - 1);
            double eased = Math.Pow(t, 1.5);
            double k = start + (end - start) * eased;
            src.Row(src.Height - 1 - y).CopyTo(r.Row(y));
            var row = r.Row(y);
            for (int i = 3; i < row.Length; i += 4) row[i] = (byte)(row[i] * k);
        });
        if (o.Blur >= 0.5) BoxBlur.BlurBgra(r, o.Blur);
        return r;
    }

    /// <summary>Image with its reflection below it on a taller transparent canvas.</summary>
    public static PixelBuffer Apply(PixelBuffer src, ReflectionOptions o)
    {
        var r = RenderReflectionOnly(src, o);
        if (r is null) return src.Clone();
        int gap = Math.Max(0, (int)Math.Round(o.Gap));
        var dst = new PixelBuffer(src.Width, src.Height + gap + r.Height);
        dst.Blit(src, 0, 0);
        dst.Blit(r, 0, src.Height + gap);
        return dst;
    }
}
