using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModernScreenShot.App.Interop;
using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Editor;

/// <summary>
/// Renders annotations into the captured pixels at 1:1 (image px = buffer px). Used when an output
/// path needs the flat image directly (overlay copy/save/pin); the editor instead renders the
/// document live so annotations stay editable.
/// </summary>
internal static class AnnotationFlattener
{
    public static PixelBuffer Flatten(PixelBuffer baseImage, AnnotationDocument doc)
    {
        var baseBitmap = baseImage.ToBitmapSource();
        var mosaic = BuildMosaicLayer(baseImage, doc.Items.OfType<MosaicItem>().ToList());

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(baseBitmap, new Rect(0, 0, baseImage.Width, baseImage.Height));
            AnnotationRenderer.RenderDocument(dc, doc, baseBitmap, mosaic);
        }
        var rtb = new RenderTargetBitmap(baseImage.Width, baseImage.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return BitmapInterop.FromBitmapSource(rtb);
    }

    /// <summary>Pixels the / blurs every mosaic rect into a copy of the base image (null when no mosaic items).</summary>
    public static BitmapSource? BuildMosaicLayer(PixelBuffer baseImage, List<MosaicItem> items)
    {
        if (items.Count == 0) return null;
        var scratch = baseImage.Clone();
        var full = new PixelRect(0, 0, baseImage.Width, baseImage.Height);
        foreach (var m in items)
        {
            var r = m.Rect.ToPixelRect().Intersect(full);
            if (r.IsEmpty) continue;
            if (m.Mode == MosaicMode.Pixelate) Mosaic.Pixelate(scratch, r, m.Strength);
            else Mosaic.Blur(scratch, r, m.Strength);
        }
        return scratch.ToBitmapSource();
    }
}
