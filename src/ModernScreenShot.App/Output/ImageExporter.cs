using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Output;
using ModernScreenShot.Core.Settings;

namespace ModernScreenShot.App.Output;

/// <summary>Saves images to disk. T7 adds WebP (SkiaSharp) and history integration.</summary>
public sealed class ImageExporter
{
    private readonly SettingsStore _settings;

    public ImageExporter(SettingsStore settings) => _settings = settings;

    public static string ExtensionFor(ImageFormat format) => format switch
    {
        ImageFormat.Jpg => ".jpg",
        ImageFormat.WebP => ".webp",
        _ => ".png",
    };

    /// <summary>Saves to the configured directory using the file-name template. Returns the written path.</summary>
    public string QuickSave(PixelBuffer image, string? windowTitle, string mode)
    {
        var output = _settings.Current.Output;
        string dir = string.IsNullOrWhiteSpace(output.SaveDirectory) ? AppPaths.DefaultSaveDir : output.SaveDirectory;
        string baseName = FileNameTemplate.Format(output.FileNameTemplate, DateTime.Now, output.Counter, windowTitle, mode);
        string path = FileNameTemplate.GetUniquePath(dir, baseName, ExtensionFor(output.Format));
        Save(image, path, output.Format, output.JpgQuality);
        try
        {
            // The image file is already written; a failure here must not report the save as failed.
            output.Counter++;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Warn($"Persisting the output counter/settings failed: {ex.Message}");
        }
        return path;
    }

    /// <summary>Saves with a SaveFileDialog. Returns the path, or null when cancelled.</summary>
    public string? SaveAs(PixelBuffer image, string suggestedName)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            FileName = suggestedName,
            Filter = "PNG (*.png)|*.png|JPEG (*.jpg)|*.jpg|WebP (*.webp)|*.webp",
            FilterIndex = _settings.Current.Output.Format switch { ImageFormat.Jpg => 2, ImageFormat.WebP => 3, _ => 1 },
        };
        if (dialog.ShowDialog() != true) return null;
        var format = Path.GetExtension(dialog.FileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => ImageFormat.Jpg,
            ".webp" => ImageFormat.WebP,
            _ => ImageFormat.Png,
        };
        Save(image, dialog.FileName, format, _settings.Current.Output.JpgQuality);
        return dialog.FileName;
    }

    public void Save(PixelBuffer image, string path, ImageFormat format, int jpgQuality)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        try
        {
            switch (format)
            {
                case ImageFormat.Jpg:
                    SaveWithEncoder(new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpgQuality, 1, 100) }, image, path);
                    break;
                case ImageFormat.WebP:
                    SaveWebP(image, path, _settings.Current.Output.WebPQuality);
                    break;
                default:
                    SaveWithEncoder(new PngBitmapEncoder(), image, path);
                    break;
            }
            Log.Info($"Saved image to {path}");
        }
        catch (Exception ex)
        {
            Log.Error($"Saving {path} failed", ex);
            throw;
        }
    }

    /// <summary>Encodes WebP via SkiaSharp. Screenshot pixels are straight BGRA which maps 1:1.</summary>
    private static void SaveWebP(PixelBuffer image, string path, int quality)
    {
        using var bitmap = new SkiaSharp.SKBitmap(image.Width, image.Height,
            SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Unpremul);
        Marshal.Copy(image.Data, 0, bitmap.GetPixels(), image.Data.Length);
        using var skImage = SkiaSharp.SKImage.FromBitmap(bitmap);
        using var data = skImage.Encode(SkiaSharp.SKEncodedImageFormat.Webp, Math.Clamp(quality, 1, 100));
        using var fs = File.Create(Path.GetFullPath(path));
        data.SaveTo(fs);
    }

    private static void SaveWithEncoder(BitmapEncoder encoder, PixelBuffer image, string path)
    {
        if (encoder is JpegBitmapEncoder)
        {
            // JPEG has no alpha channel; flatten onto opaque Bgr32 first.
            var converted = new FormatConvertedBitmap(image.ToBitmapSource(), System.Windows.Media.PixelFormats.Bgr32, null, 0);
            converted.Freeze();
            encoder.Frames.Add(BitmapFrame.Create(converted));
        }
        else
        {
            encoder.Frames.Add(BitmapFrame.Create(image.ToBitmapSource()));
        }
        using var fs = new FileStream(Path.GetFullPath(path), FileMode.Create, FileAccess.Write, FileShare.None);
        encoder.Save(fs);
    }
}
