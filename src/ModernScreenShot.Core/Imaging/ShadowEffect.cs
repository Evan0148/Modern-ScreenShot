namespace ModernScreenShot.Core.Imaging;

public static class ShadowEffect
{
    /// <summary>Extra pixels the blur needs around the mask.</summary>
    private static int BlurExtent(ShadowOptions o) => (int)Math.Ceiling(o.BlurRadius * 1.5) + 2;

    /// <summary>Largest margin the shadow can need on any side.</summary>
    public static int Margin(ShadowOptions o) =>
        o.Enabled ? BlurExtent(o) + (int)Math.Ceiling(o.Spread) + (int)Math.Ceiling(o.Distance) : 0;

    public static (double dx, double dy) Offset(ShadowOptions o)
    {
        double rad = o.Angle * Math.PI / 180.0;
        return (Math.Cos(rad) * o.Distance, Math.Sin(rad) * o.Distance);
    }

    /// <summary>
    /// Renders src over its drop shadow. offsetX/offsetY give the position of src inside the returned buffer.
    /// </summary>
    public static PixelBuffer Apply(PixelBuffer src, ShadowOptions o, out int offsetX, out int offsetY)
    {
        var shadow = RenderShadowOnly(src, o, out offsetX, out offsetY);
        if (shadow is null) return src.Clone();
        shadow.DrawOver(src, offsetX, offsetY);
        return shadow;
    }

    /// <summary>Returns a canvas with only the shadow painted (null when disabled/invisible).</summary>
    public static PixelBuffer? RenderShadowOnly(PixelBuffer src, ShadowOptions o, out int offsetX, out int offsetY)
    {
        offsetX = offsetY = 0;
        if (!o.Enabled || o.Opacity <= 0) return null;
        var color = PixelColor.TryParseHex(o.Color, out var c) ? c : PixelColor.Black;
        double alphaScale = color.A / 255.0 * Math.Clamp(o.Opacity, 0, 1);
        if (alphaScale <= 0) return null;

        var (dx, dy) = Offset(o);
        int spread = (int)Math.Round(Math.Max(0, o.Spread));
        int extent = BlurExtent(o) + spread;
        int idx = (int)Math.Round(dx), idy = (int)Math.Round(dy);

        // Margins so the shadow is never clipped, tight on the side opposite the offset.
        int left = Math.Max(0, extent - idx);
        int right = Math.Max(0, extent + idx);
        int top = Math.Max(0, extent - idy);
        int bottom = Math.Max(0, extent + idy);

        int w = src.Width + left + right, h = src.Height + top + bottom;
        var mask = new byte[w * h];
        // Shadow mask placed at src position + offset.
        int mx = left + idx, my = top + idy;
        var sd = src.Data;
        Parallel.For(0, src.Height, y =>
        {
            int mrow = (my + y) * w + mx;
            int si = y * src.Stride + 3;
            for (int x = 0; x < src.Width; x++, si += 4) mask[mrow + x] = sd[si];
        });

        if (spread > 0) Dilate(mask, w, h, spread);
        BoxBlur.BlurAlpha(mask, w, h, Math.Max(0, o.BlurRadius));

        var outBuf = new PixelBuffer(w, h);
        var od = outBuf.Data;
        byte cr = color.R, cg = color.G, cb = color.B;
        int scale = (int)Math.Round(alphaScale * 256);
        Parallel.For(0, h, y =>
        {
            int mi = y * w, oi = y * w * 4;
            for (int x = 0; x < w; x++, mi++, oi += 4)
            {
                int a = mask[mi] * scale >> 8;
                if (a == 0) continue;
                od[oi] = cb; od[oi + 1] = cg; od[oi + 2] = cr; od[oi + 3] = (byte)Math.Min(255, a);
            }
        });

        offsetX = left;
        offsetY = top;
        return outBuf;
    }

    /// <summary>Square max-filter dilation via separable sliding max (van Herk style simplified).</summary>
    private static void Dilate(byte[] mask, int w, int h, int r)
    {
        var tmp = new byte[mask.Length];
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            for (int x = 0; x < w; x++)
            {
                byte m = 0;
                int l = Math.Max(0, x - r), rr = Math.Min(w - 1, x + r);
                for (int k = l; k <= rr; k++) if (mask[row + k] > m) { m = mask[row + k]; if (m == 255) break; }
                tmp[row + x] = m;
            }
        });
        Parallel.For(0, w, x =>
        {
            for (int y = 0; y < h; y++)
            {
                byte m = 0;
                int t = Math.Max(0, y - r), b = Math.Min(h - 1, y + r);
                for (int k = t; k <= b; k++) if (tmp[k * w + x] > m) { m = tmp[k * w + x]; if (m == 255) break; }
                mask[y * w + x] = m;
            }
        });
    }
}
