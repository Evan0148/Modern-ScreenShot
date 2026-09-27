namespace ModernScreenShot.Core.Imaging;

/// <summary>
/// Straight-alpha (non-premultiplied) BGRA32 image. Stride is always Width * 4.
/// </summary>
public sealed class PixelBuffer
{
    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public byte[] Data { get; }

    public PixelBuffer(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        Width = width;
        Height = height;
        Data = new byte[width * height * 4];
    }

    public PixelBuffer(int width, int height, byte[] data)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Image dimensions must be positive.");
        if (data.Length < width * height * 4) throw new ArgumentException("Buffer too small.", nameof(data));
        Width = width;
        Height = height;
        Data = data;
    }

    public Span<byte> Row(int y) => Data.AsSpan(y * Stride, Stride);

    public PixelColor GetPixel(int x, int y)
    {
        int i = y * Stride + x * 4;
        return new PixelColor(Data[i + 3], Data[i + 2], Data[i + 1], Data[i]);
    }

    public void SetPixel(int x, int y, PixelColor c)
    {
        int i = y * Stride + x * 4;
        Data[i] = c.B; Data[i + 1] = c.G; Data[i + 2] = c.R; Data[i + 3] = c.A;
    }

    public PixelBuffer Clone() => new(Width, Height, (byte[])Data.Clone());

    public PixelBuffer Crop(PixelRect rect)
    {
        var r = rect.Intersect(new PixelRect(0, 0, Width, Height));
        if (r.IsEmpty) throw new ArgumentException("Crop rectangle is outside the image.", nameof(rect));
        var dst = new PixelBuffer(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++)
            Data.AsSpan((r.Y + y) * Stride + r.X * 4, r.Width * 4).CopyTo(dst.Row(y));
        return dst;
    }

    public void Fill(PixelColor c)
    {
        var span = Data.AsSpan();
        for (int i = 0; i < span.Length; i += 4)
        {
            span[i] = c.B; span[i + 1] = c.G; span[i + 2] = c.R; span[i + 3] = c.A;
        }
    }

    /// <summary>Copies src into this buffer at (dx, dy) without blending.</summary>
    public void Blit(PixelBuffer src, int dx, int dy)
    {
        var r = new PixelRect(dx, dy, src.Width, src.Height).Intersect(new PixelRect(0, 0, Width, Height));
        if (r.IsEmpty) return;
        for (int y = r.Y; y < r.Bottom; y++)
            src.Data.AsSpan((y - dy) * src.Stride + (r.X - dx) * 4, r.Width * 4).CopyTo(Data.AsSpan(y * Stride + r.X * 4));
    }

    /// <summary>Alpha-composites src over this buffer at (dx, dy) with an extra opacity multiplier.</summary>
    public void DrawOver(PixelBuffer src, int dx, int dy, double opacity = 1.0)
    {
        var r = new PixelRect(dx, dy, src.Width, src.Height).Intersect(new PixelRect(0, 0, Width, Height));
        if (r.IsEmpty) return;
        int op = (int)Math.Round(Math.Clamp(opacity, 0, 1) * 255);
        Parallel.For(r.Y, r.Bottom, y =>
        {
            var s = src.Data.AsSpan((y - dy) * src.Stride + (r.X - dx) * 4, r.Width * 4);
            var d = Data.AsSpan(y * Stride + r.X * 4, r.Width * 4);
            for (int i = 0; i < s.Length; i += 4)
            {
                int sa = s[i + 3] * op / 255;
                if (sa == 0) continue;
                int da = d[i + 3];
                if (sa == 255 || da == 0)
                {
                    d[i] = s[i]; d[i + 1] = s[i + 1]; d[i + 2] = s[i + 2]; d[i + 3] = (byte)sa;
                    continue;
                }
                int outA = sa + da * (255 - sa) / 255;
                for (int c = 0; c < 3; c++)
                    d[i + c] = (byte)((s[i + c] * sa + d[i + c] * da * (255 - sa) / 255) / outA);
                d[i + 3] = (byte)outA;
            }
        });
    }
}

public readonly record struct PixelColor(byte A, byte R, byte G, byte B)
{
    public static PixelColor Transparent => new(0, 0, 0, 0);
    public static PixelColor Black => new(255, 0, 0, 0);
    public static PixelColor White => new(255, 255, 255, 255);

    public uint ToArgb() => (uint)(A << 24 | R << 16 | G << 8 | B);
    public static PixelColor FromArgb(uint argb) => new((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>"#AARRGGBB" or "#RRGGBB".</summary>
    public string ToHex(bool includeAlpha = true) => includeAlpha ? $"#{A:X2}{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}";

    public static bool TryParseHex(string? s, out PixelColor color)
    {
        color = Black;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim().TrimStart('#');
        if (s.Length == 6) s = "FF" + s;
        if (s.Length != 8 || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v)) return false;
        color = FromArgb(v);
        return true;
    }

    public static PixelColor ParseHex(string s) => TryParseHex(s, out var c) ? c : throw new FormatException($"Invalid color '{s}'.");
    public override string ToString() => ToHex();
}

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;

    public static PixelRect FromLTRB(int l, int t, int r, int b) => new(l, t, r - l, b - t);

    public PixelRect Intersect(PixelRect o)
    {
        int l = Math.Max(X, o.X), t = Math.Max(Y, o.Y), r = Math.Min(Right, o.Right), b = Math.Min(Bottom, o.Bottom);
        return r <= l || b <= t ? default : FromLTRB(l, t, r, b);
    }

    public PixelRect Union(PixelRect o) => IsEmpty ? o : o.IsEmpty ? this :
        FromLTRB(Math.Min(X, o.X), Math.Min(Y, o.Y), Math.Max(Right, o.Right), Math.Max(Bottom, o.Bottom));

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;
    public PixelRect Offset(int dx, int dy) => new(X + dx, Y + dy, Width, Height);
    public PixelRect Inflate(int d) => new(X - d, Y - d, Width + 2 * d, Height + 2 * d);
}
