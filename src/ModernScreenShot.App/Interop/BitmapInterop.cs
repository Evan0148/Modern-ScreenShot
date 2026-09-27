using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Interop;

/// <summary>Conversions between Core <see cref="PixelBuffer"/> (straight BGRA32) and WPF bitmaps, plus PNG helpers.</summary>
public static class BitmapInterop
{
    /// <summary>Creates a frozen Bgra32 BitmapSource. At 96 dpi one image pixel maps to one DIP.</summary>
    public static BitmapSource ToBitmapSource(this PixelBuffer buffer, double dpi = 96)
    {
        var bmp = BitmapSource.Create(buffer.Width, buffer.Height, dpi, dpi, PixelFormats.Bgra32, null, buffer.Data, buffer.Stride);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Converts any BitmapSource to a straight-alpha BGRA32 PixelBuffer.</summary>
    public static PixelBuffer FromBitmapSource(BitmapSource source)
    {
        BitmapSource src = source;
        if (src.Format != PixelFormats.Bgra32)
        {
            // Pbgra32 must be un-premultiplied; FormatConvertedBitmap handles this and all other formats.
            var converted = new FormatConvertedBitmap(src, PixelFormats.Bgra32, null, 0);
            converted.Freeze();
            src = converted;
        }
        var buf = new PixelBuffer(src.PixelWidth, src.PixelHeight);
        src.CopyPixels(buf.Data, buf.Stride, 0);
        return buf;
    }

    public static byte[] EncodePng(PixelBuffer buffer)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(buffer.ToBitmapSource()));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    public static void SavePng(PixelBuffer buffer, string path)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(buffer.ToBitmapSource()));
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(fs);
    }

    public static PixelBuffer DecodeImage(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
        return FromBitmapSource(decoder.Frames[0]);
    }

    public static PixelBuffer LoadPng(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return DecodeImage(fs);
    }

    /// <summary>Scales a physical-pixel length to DIPs for a given DPI.</summary>
    public static double ToDip(double physical, double dpi) => physical * 96.0 / dpi;
    public static double ToPhysical(double dip, double dpi) => dip * dpi / 96.0;

    public static Rect ToDipRect(PixelRect r, double dpi) =>
        new(ToDip(r.X, dpi), ToDip(r.Y, dpi), ToDip(r.Width, dpi), ToDip(r.Height, dpi));

    public static PixelRect ToPhysicalRect(Rect r, double dpi)
    {
        int l = (int)Math.Round(ToPhysical(r.Left, dpi)), t = (int)Math.Round(ToPhysical(r.Top, dpi));
        int rr = (int)Math.Round(ToPhysical(r.Right, dpi)), b = (int)Math.Round(ToPhysical(r.Bottom, dpi));
        return PixelRect.FromLTRB(l, t, rr, b);
    }
}
