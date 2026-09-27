namespace ModernScreenShot.Core.Imaging;

public static class FrameEffect
{
    /// <summary>Coverage (0-1) of pixel (x,y) inside a rounded rect of size w×h with radius r.</summary>
    private static double Coverage(int x, int y, int w, int h, double r)
    {
        double px = x + 0.5, py = y + 0.5;
        double cx, cy;
        if (px < r) cx = r; else if (px > w - r) cx = w - r; else return 1;
        if (py < r) cy = r; else if (py > h - r) cy = h - r; else return 1;
        double d = Math.Sqrt((px - cx) * (px - cx) + (py - cy) * (py - cy));
        return Math.Clamp(r - d + 0.5, 0, 1);
    }

    public static PixelBuffer RoundCorners(PixelBuffer src, double radius)
    {
        var dst = src.Clone();
        double r = Math.Min(radius, Math.Min(src.Width, src.Height) / 2.0);
        if (r < 0.5) return dst;
        int ri = (int)Math.Ceiling(r) + 1;
        int w = src.Width, h = src.Height;
        void Process(int x, int y)
        {
            double cov = Coverage(x, y, w, h, r);
            if (cov >= 1) return;
            int i = y * dst.Stride + x * 4 + 3;
            dst.Data[i] = (byte)Math.Round(dst.Data[i] * cov);
        }
        for (int y = 0; y < Math.Min(ri, h); y++)
            for (int x = 0; x < w; x++) { if (x >= ri && x < w - ri) continue; Process(x, y); Process(x, h - 1 - y); }
        return dst;
    }

    public static void DrawInnerBorder(PixelBuffer buf, double radius, double thickness, PixelColor color)
    {
        if (thickness <= 0 || color.A == 0) return;
        int w = buf.Width, h = buf.Height;
        double r = Math.Min(radius, Math.Min(w, h) / 2.0);
        double t = thickness;
        var overlay = new PixelBuffer(w, h);
        Parallel.For(0, h, y =>
        {
            for (int x = 0; x < w; x++)
            {
                // Distance to the rounded-rect edge from inside (signed, positive = inside).
                double px = x + 0.5, py = y + 0.5;
                double qx = Math.Max(Math.Abs(px - w / 2.0) - (w / 2.0 - r), 0);
                double qy = Math.Max(Math.Abs(py - h / 2.0) - (h / 2.0 - r), 0);
                double outside = Math.Sqrt(qx * qx + qy * qy) - r;
                double inner = Math.Max(Math.Abs(px - w / 2.0) - w / 2.0, Math.Abs(py - h / 2.0) - h / 2.0);
                double dist = -(qx > 0 || qy > 0 ? outside : Math.Max(outside, inner));
                double cov = Math.Clamp(dist + 0.5, 0, 1) * Math.Clamp(t - dist + 0.5, 0, 1);
                if (cov <= 0) continue;
                overlay.SetPixel(x, y, color with { A = (byte)(color.A * cov) });
            }
        });
        // Keep outside-of-shape alpha untouched: only draw where the image is visible.
        for (int i = 3; i < overlay.Data.Length; i += 4)
            overlay.Data[i] = (byte)(overlay.Data[i] * buf.Data[i] / 255);
        buf.DrawOver(overlay, 0, 0);
    }

    public static PixelBuffer ApplyBackground(PixelBuffer content, FrameOptions o)
    {
        int pad = Math.Max(0, (int)Math.Round(o.Padding));
        var dst = new PixelBuffer(content.Width + pad * 2, content.Height + pad * 2);
        switch (o.Background)
        {
            case BackgroundKind.Solid:
                dst.Fill(PixelColor.TryParseHex(o.BackgroundColor, out var bg) ? bg : PixelColor.White);
                break;
            case BackgroundKind.Gradient:
                FillGradient(dst,
                    PixelColor.TryParseHex(o.GradientStart, out var a) ? a : PixelColor.White,
                    PixelColor.TryParseHex(o.GradientEnd, out var b) ? b : PixelColor.Black,
                    o.GradientAngle);
                break;
        }
        dst.DrawOver(content, pad, pad);
        return dst;
    }

    public static void FillGradient(PixelBuffer dst, PixelColor start, PixelColor end, double angleDeg)
    {
        double rad = angleDeg * Math.PI / 180;
        double ux = Math.Cos(rad), uy = Math.Sin(rad);
        int w = dst.Width, h = dst.Height;
        // Project corners to find the gradient span so it covers the full canvas.
        double[] proj = [0, (w - 1) * ux, (h - 1) * uy, (w - 1) * ux + (h - 1) * uy];
        double min = proj.Min(), max = proj.Max(), span = Math.Max(1e-6, max - min);
        Parallel.For(0, h, y =>
        {
            var row = dst.Row(y);
            for (int x = 0; x < w; x++)
            {
                double t = (x * ux + y * uy - min) / span;
                var c = ColorUtil.Lerp(start, end, t);
                int i = x * 4;
                row[i] = c.B; row[i + 1] = c.G; row[i + 2] = c.R; row[i + 3] = c.A;
            }
        });
    }
}
