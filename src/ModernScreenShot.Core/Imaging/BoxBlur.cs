namespace ModernScreenShot.Core.Imaging;

/// <summary>
/// Three-pass box blur approximating a gaussian. Cost per pixel is independent of the radius.
/// </summary>
public static class BoxBlur
{
    /// <summary>Box sizes whose successive application approximates a gaussian with the given sigma.</summary>
    private static int[] BoxesForGauss(double sigma, int n = 3)
    {
        double wIdeal = Math.Sqrt(12 * sigma * sigma / n + 1);
        int wl = (int)Math.Floor(wIdeal);
        if (wl % 2 == 0) wl--;
        int wu = wl + 2;
        double mIdeal = (12 * sigma * sigma - n * wl * wl - 4 * n * wl - 3 * n) / (-4 * wl - 4);
        int m = (int)Math.Round(mIdeal);
        var sizes = new int[n];
        for (int i = 0; i < n; i++) sizes[i] = Math.Max(0, ((i < m ? wl : wu) - 1) / 2);
        return sizes;
    }

    /// <summary>Blurs a single-channel buffer in place.</summary>
    public static void BlurAlpha(byte[] alpha, int w, int h, double radius)
    {
        if (radius < 0.5 || w <= 0 || h <= 0) return;
        var boxes = BoxesForGauss(radius / 2.0);
        var tmp = new byte[alpha.Length];
        foreach (var r in boxes)
        {
            if (r <= 0) continue;
            HorizontalChannel(alpha, tmp, w, h, r);
            VerticalChannel(tmp, alpha, w, h, r);
        }
    }

    private static void HorizontalChannel(byte[] src, byte[] dst, int w, int h, int r)
    {
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            int div = 2 * r + 1;
            int first = src[row], last = src[row + w - 1];
            int sum = first * (r + 1);
            for (int i = 0; i < r; i++) sum += src[row + Math.Min(i, w - 1)];
            for (int x = 0; x < w; x++)
            {
                int addIdx = x + r;
                sum += addIdx < w ? src[row + addIdx] : last;
                dst[row + x] = (byte)((sum + div / 2) / div);
                int remIdx = x - r;
                sum -= remIdx >= 0 ? src[row + remIdx] : first;
            }
        });
    }

    private static void VerticalChannel(byte[] src, byte[] dst, int w, int h, int r)
    {
        Parallel.For(0, w, x =>
        {
            int div = 2 * r + 1;
            int first = src[x], last = src[(h - 1) * w + x];
            int sum = first * (r + 1);
            for (int i = 0; i < r; i++) sum += src[Math.Min(i, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                int addIdx = y + r;
                sum += addIdx < h ? src[addIdx * w + x] : last;
                dst[y * w + x] = (byte)((sum + div / 2) / div);
                int remIdx = y - r;
                sum -= remIdx >= 0 ? src[remIdx * w + x] : first;
            }
        });
    }

    /// <summary>Alpha-correct blur of the whole buffer.</summary>
    public static void BlurBgra(PixelBuffer buf, double radius) =>
        BlurRegion(buf, new PixelRect(0, 0, buf.Width, buf.Height), radius, sampleOutside: false);

    /// <summary>Blurs only inside rect, sampling surrounding pixels so edges stay smooth.</summary>
    public static void BlurRegion(PixelBuffer buf, PixelRect rect, double radius) =>
        BlurRegion(buf, rect, radius, sampleOutside: true);

    private static void BlurRegion(PixelBuffer buf, PixelRect rect, double radius, bool sampleOutside)
    {
        var target = rect.Intersect(new PixelRect(0, 0, buf.Width, buf.Height));
        if (target.IsEmpty || radius < 0.5) return;
        int pad = sampleOutside ? (int)Math.Ceiling(radius * 1.5) : 0;
        var work = target.Inflate(pad).Intersect(new PixelRect(0, 0, buf.Width, buf.Height));
        int w = work.Width, h = work.Height;

        // Premultiplied float planes, fixed-point in int for speed.
        var planes = new int[4][];
        for (int c = 0; c < 4; c++) planes[c] = new int[w * h];
        var data = buf.Data;
        Parallel.For(0, h, y =>
        {
            int si = (work.Y + y) * buf.Stride + work.X * 4;
            int di = y * w;
            for (int x = 0; x < w; x++, si += 4, di++)
            {
                int a = data[si + 3];
                planes[0][di] = data[si] * a;
                planes[1][di] = data[si + 1] * a;
                planes[2][di] = data[si + 2] * a;
                planes[3][di] = a * 255;
            }
        });

        var boxes = BoxesForGauss(radius / 2.0);
        var tmp = new int[w * h];
        foreach (var plane in planes)
            foreach (var r in boxes)
            {
                if (r <= 0) continue;
                HorizontalInt(plane, tmp, w, h, r);
                VerticalInt(tmp, plane, w, h, r);
            }

        Parallel.For(target.Y, target.Bottom, yy =>
        {
            int y = yy - work.Y;
            int di = yy * buf.Stride + target.X * 4;
            int si = y * w + (target.X - work.X);
            for (int x = 0; x < target.Width; x++, si++, di += 4)
            {
                int a255 = planes[3][si];
                if (a255 <= 0)
                {
                    data[di] = data[di + 1] = data[di + 2] = data[di + 3] = 0;
                    continue;
                }
                int a = (a255 + 127) / 255;
                data[di] = (byte)Math.Min(255, planes[0][si] / Math.Max(1, a));
                data[di + 1] = (byte)Math.Min(255, planes[1][si] / Math.Max(1, a));
                data[di + 2] = (byte)Math.Min(255, planes[2][si] / Math.Max(1, a));
                data[di + 3] = (byte)Math.Min(255, a);
            }
        });
    }

    private static void HorizontalInt(int[] src, int[] dst, int w, int h, int r)
    {
        Parallel.For(0, h, y =>
        {
            int row = y * w;
            int div = 2 * r + 1;
            long first = src[row], last = src[row + w - 1];
            long sum = first * (r + 1);
            for (int i = 0; i < r; i++) sum += src[row + Math.Min(i, w - 1)];
            for (int x = 0; x < w; x++)
            {
                int addIdx = x + r;
                sum += addIdx < w ? src[row + addIdx] : last;
                dst[row + x] = (int)(sum / div);
                int remIdx = x - r;
                sum -= remIdx >= 0 ? src[row + remIdx] : first;
            }
        });
    }

    private static void VerticalInt(int[] src, int[] dst, int w, int h, int r)
    {
        Parallel.For(0, w, x =>
        {
            int div = 2 * r + 1;
            long first = src[x], last = src[(h - 1) * w + x];
            long sum = first * (r + 1);
            for (int i = 0; i < r; i++) sum += src[Math.Min(i, h - 1) * w + x];
            for (int y = 0; y < h; y++)
            {
                int addIdx = y + r;
                sum += addIdx < h ? src[addIdx * w + x] : last;
                dst[y * w + x] = (int)(sum / div);
                int remIdx = y - r;
                sum -= remIdx >= 0 ? src[remIdx * w + x] : first;
            }
        });
    }
}
