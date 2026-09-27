namespace ModernScreenShot.Core.Imaging;

public static class Mosaic
{
    public static void Pixelate(PixelBuffer buf, PixelRect rect, int cellSize)
    {
        var r = rect.Intersect(new PixelRect(0, 0, buf.Width, buf.Height));
        if (r.IsEmpty) return;
        int cell = Math.Max(2, cellSize);
        int cellsY = (r.Height + cell - 1) / cell;
        Parallel.For(0, cellsY, cy =>
        {
            int y0 = r.Y + cy * cell, y1 = Math.Min(r.Bottom, y0 + cell);
            for (int x0 = r.X; x0 < r.Right; x0 += cell)
            {
                int x1 = Math.Min(r.Right, x0 + cell);
                long sb = 0, sg = 0, sr = 0, sa = 0; int n = 0;
                for (int y = y0; y < y1; y++)
                {
                    int i = y * buf.Stride + x0 * 4;
                    for (int x = x0; x < x1; x++, i += 4)
                    {
                        int a = buf.Data[i + 3];
                        sb += buf.Data[i] * a; sg += buf.Data[i + 1] * a; sr += buf.Data[i + 2] * a; sa += a; n++;
                    }
                }
                byte b = sa > 0 ? (byte)(sb / sa) : (byte)0, g = sa > 0 ? (byte)(sg / sa) : (byte)0, rr = sa > 0 ? (byte)(sr / sa) : (byte)0;
                byte aa = (byte)(sa / Math.Max(1, n));
                for (int y = y0; y < y1; y++)
                {
                    int i = y * buf.Stride + x0 * 4;
                    for (int x = x0; x < x1; x++, i += 4)
                    {
                        buf.Data[i] = b; buf.Data[i + 1] = g; buf.Data[i + 2] = rr; buf.Data[i + 3] = aa;
                    }
                }
            }
        });
    }

    /// <summary>Heavy blur (two passes) inside rect for privacy.</summary>
    public static void Blur(PixelBuffer buf, PixelRect rect, int radius)
    {
        double r = Math.Max(2, radius);
        BoxBlur.BlurRegion(buf, rect, r);
        BoxBlur.BlurRegion(buf, rect, r);
    }
}
