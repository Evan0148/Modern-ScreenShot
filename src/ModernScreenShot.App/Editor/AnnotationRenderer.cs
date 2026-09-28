using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Editor;

/// <summary>
/// Draws annotation items in image-pixel coordinates (1 image px = 1 DIP at 96 dpi). Shared by the
/// editor canvas, which wraps it in a zoom transform, and by the 1:1 export render — so the editor
/// preview and the exported image are produced by the same code.
/// </summary>
internal static class AnnotationRenderer
{
    private static readonly ConcurrentDictionary<string, Brush> BrushCache = new(StringComparer.OrdinalIgnoreCase);

    public static Brush BrushFor(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var cached)) return cached;
        PixelColor.TryParseHex(hex, out var c);
        var brush = Freeze(new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B)));
        BrushCache[hex] = brush;
        return brush;
    }

    public static void RenderDocument(DrawingContext dc, AnnotationDocument doc, BitmapSource baseImage, BitmapSource? mosaicSource)
    {
        bool spotlightDimDrawn = false;
        foreach (var item in doc.Items)
        {
            // The dim is drawn once at the first spotlight's z-position, using the union of all spotlights.
            if (item is SpotlightItem)
            {
                if (!spotlightDimDrawn)
                {
                    spotlightDimDrawn = true;
                    DrawSpotlightDim(dc, doc.Items.OfType<SpotlightItem>(), baseImage);
                }
                continue;
            }
            RenderItem(dc, item, baseImage, mosaicSource);
        }
    }

    public static void RenderItem(DrawingContext dc, AnnotationItem item, BitmapSource baseImage, BitmapSource? mosaicSource)
    {
        double opacity = Math.Clamp(item.Opacity, 0, 1);
        if (opacity <= 0) return;
        dc.PushOpacity(opacity);
        switch (item)
        {
            case RectItem r: DrawRect(dc, r); break;
            case EllipseItem e: DrawEllipse(dc, e); break;
            case ArrowItem a: DrawArrow(dc, a); break;
            case LineItem l: DrawLine(dc, l); break;
            case HighlighterItem h: DrawPen(dc, h, flatCaps: true); break;
            case PenItem p: DrawPen(dc, p, flatCaps: false); break;
            case TextItem t: DrawText(dc, t); break;
            case StepItem s: DrawStep(dc, s); break;
            case MosaicItem m: DrawMosaic(dc, m, baseImage, mosaicSource); break;
            case MagnifierItem mag: DrawMagnifier(dc, mag, baseImage); break;
        }
        dc.Pop();
    }

    private static void DrawRect(DrawingContext dc, RectItem r)
    {
        var rect = ToRect(r.Rect);
        Geometry geo = r.CornerRadius > 0
            ? new RectangleGeometry(rect, r.CornerRadius, r.CornerRadius)
            : new RectangleGeometry(rect);
        dc.DrawGeometry(r.Filled ? BrushFor(r.FillColor) : null, StrokePen(r.StrokeColor, r.StrokeThickness), geo);
    }

    private static void DrawEllipse(DrawingContext dc, EllipseItem e)
    {
        var rect = ToRect(e.Rect);
        var geo = new EllipseGeometry(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2);
        dc.DrawGeometry(e.Filled ? BrushFor(e.FillColor) : null, StrokePen(e.StrokeColor, e.StrokeThickness), geo);
    }

    private static void DrawLine(DrawingContext dc, LineItem l)
    {
        dc.DrawLine(StrokePen(l.StrokeColor, l.StrokeThickness, l.Dashed), new Point(l.Start.X, l.Start.Y), new Point(l.End.X, l.End.Y));
    }

    private static void DrawArrow(DrawingContext dc, ArrowItem a)
    {
        var brush = BrushFor(a.StrokeColor);
        double th = Math.Max(1, a.StrokeThickness);
        double head = Math.Max(th * 3.2, 10);
        var start = new Point(a.Start.X, a.Start.Y);
        var end = new Point(a.End.X, a.End.Y);
        var d = end - start;
        double len = d.Length;
        if (len < 0.001) return;
        d /= len;
        var perp = new Vector(-d.Y, d.X);

        var lineStart = a.DoubleHeaded ? start + d * head * 0.8 : start;
        var lineEnd = end - d * head * 0.8;
        dc.DrawLine(StrokePen(a.StrokeColor, th, a.Dashed), lineStart, lineEnd);
        DrawHead(start, a.DoubleHeaded ? -d : d);
        if (a.DoubleHeaded) DrawHead(end, d);
        return;

        void DrawHead(Point tip, Vector dir)
        {
            var baseCenter = tip - dir * head;
            var p1 = baseCenter + perp * head * 0.42;
            var p2 = baseCenter - perp * head * 0.42;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(tip, true, true);
                ctx.LineTo(p1, true, false);
                ctx.LineTo(p2, true, false);
            }
            dc.DrawGeometry(brush, null, geo);
        }
    }

    private static void DrawPen(DrawingContext dc, PenItem item, bool flatCaps)
    {
        if (item.Points.Count == 0) return;
        var cap = flatCaps ? PenLineCap.Flat : PenLineCap.Round;
        var pen = new Pen(BrushFor(item.StrokeColor), Math.Max(1, item.StrokeThickness))
        {
            StartLineCap = cap,
            EndLineCap = cap,
            LineJoin = PenLineJoin.Round,
        };
        if (item.Points.Count == 1)
        {
            var c = new Point(item.Points[0].X, item.Points[0].Y);
            dc.DrawEllipse(null, pen, c, item.StrokeThickness / 2, item.StrokeThickness / 2);
            return;
        }
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(new Point(item.Points[0].X, item.Points[0].Y), false, false);
            ctx.PolyLineTo(item.Points.Skip(1).Select(p => new Point(p.X, p.Y)).ToArray(), true, false);
        }
        dc.DrawGeometry(null, pen, geo);
    }

    public static FormattedText BuildText(TextItem t)
    {
        return new FormattedText(
            string.IsNullOrEmpty(t.Text) ? " " : t.Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily(t.FontFamily), FontStyles.Normal,
                t.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            t.FontSize,
            BrushFor(t.StrokeColor),
            1.0);
    }

    private static void DrawText(DrawingContext dc, TextItem t)
    {
        if (t.BackgroundColor is { } bg)
            dc.DrawRoundedRectangle(BrushFor(bg), null,
                new Rect(t.Position.X, t.Position.Y, Math.Max(4, t.MeasuredWidth), Math.Max(4, t.MeasuredHeight)), 3, 3);
        var ft = BuildText(t);
        dc.DrawText(ft, new Point(t.Position.X + 2, t.Position.Y + 2));
    }

    private static void DrawStep(DrawingContext dc, StepItem s)
    {
        var center = new Point(s.Center.X, s.Center.Y);
        double r = Math.Max(6, s.Radius);
        dc.DrawEllipse(BrushFor(s.StrokeColor), null, center, r, r);
        var ft = new FormattedText(s.Number.ToString(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            r * 1.15, BrushFor(s.TextColor), 1.0);
        dc.DrawText(ft, new Point(center.X - ft.Width / 2, center.Y - ft.Height / 2));
    }

    private static void DrawMosaic(DrawingContext dc, MosaicItem m, BitmapSource baseImage, BitmapSource? mosaicSource)
    {
        if (mosaicSource is null) return;
        // The mosaic layer is a crop of the base image (the selection in the overlay), so a mosaic
        // rect overhanging the base/monitor bounds must be clipped to the LAYER, not to baseImage —
        // in the overlay the base is monitor-sized while the layer is only selection-sized.
        var pr = m.Rect.ToPixelRect().Intersect(new PixelRect(0, 0, mosaicSource.PixelWidth, mosaicSource.PixelHeight));
        if (pr.IsEmpty) return;
        var crop = new CroppedBitmap(mosaicSource, new Int32Rect(pr.X, pr.Y, pr.Width, pr.Height));
        dc.DrawImage(crop, new Rect(pr.X, pr.Y, pr.Width, pr.Height));
    }

    private static void DrawSpotlightDim(DrawingContext dc, IEnumerable<SpotlightItem> spots, BitmapSource baseImage)
    {
        var list = spots.ToList();
        if (list.Count == 0) return;
        var full = new Rect(0, 0, baseImage.PixelWidth, baseImage.PixelHeight);
        var shapes = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var s in list)
        {
            var rect = ToRect(s.Rect);
            shapes.Children.Add(s.Elliptical
                ? new EllipseGeometry(new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2), rect.Width / 2, rect.Height / 2)
                : new RectangleGeometry(rect));
        }
        var outside = Geometry.Combine(new RectangleGeometry(full), shapes, GeometryCombineMode.Exclude, null);
        double dim = Math.Clamp(list[0].DimOpacity, 0, 1);
        dc.PushClip(outside);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)Math.Round(dim * 255), 0, 0, 0)), null, full);
        dc.Pop();
    }

    private static void DrawMagnifier(DrawingContext dc, MagnifierItem mag, BitmapSource baseImage)
    {
        var source = ToRect(mag.SourceRect);
        if (source.Width < 4 || source.Height < 4) return;
        double r = mag.TargetRadius;
        if (r < 4) return;
        var target = new Point(mag.TargetCenter.X, mag.TargetCenter.Y);
        var srcCenter = new Point(source.X + source.Width / 2, source.Y + source.Height / 2);
        var brush = BrushFor(mag.StrokeColor);

        dc.DrawLine(new Pen(brush, Math.Max(1.5, mag.StrokeThickness / 2)), srcCenter, target);

        double s = r * 2 / Math.Max(source.Width, source.Height);
        var tg = new TransformGroup();
        tg.Children.Add(new ScaleTransform(s, s));
        tg.Children.Add(new TranslateTransform(target.X - srcCenter.X * s, target.Y - srcCenter.Y * s));
        dc.PushTransform(tg);
        dc.PushClip(new EllipseGeometry(target, r, r));
        dc.DrawImage(baseImage, new Rect(0, 0, baseImage.PixelWidth, baseImage.PixelHeight));
        dc.Pop();
        dc.Pop();

        dc.DrawEllipse(null, new Pen(brush, Math.Max(2, mag.StrokeThickness)), target, r, r);
    }

    private static Pen StrokePen(string color, double thickness, bool dashed = false)
    {
        var pen = new Pen(BrushFor(color), Math.Max(1, thickness));
        if (dashed) pen.DashStyle = new DashStyle([3, 2], 0);
        return pen;
    }

    public static Rect ToRect(RectD r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>Perpendicular distance from p to segment ab.</summary>
    public static double DistanceToSegment(PointD p, PointD a, PointD b)
    {
        double abx = b.X - a.X, aby = b.Y - a.Y;
        double lenSq = abx * abx + aby * aby;
        if (lenSq < 1e-9) return Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
        double t = Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / lenSq, 0, 1);
        double dx = p.X - (a.X + t * abx), dy = p.Y - (a.Y + t * aby);
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>
    /// Geometry-aware hit test for the eraser: true stroke geometry for lines/pens (a shallow
    /// diagonal's bounding box is far larger than its ink), circle for the magnifier callout,
    /// bounding box for area shapes. <paramref name="tol"/> is in image pixels.
    /// </summary>
    public static bool HitsForErase(AnnotationItem item, PointD p, double tol)
    {
        switch (item)
        {
            case LineItem l:
                return DistanceToSegment(p, l.Start, l.End) <= l.StrokeThickness / 2 + tol;
            case PenItem pen:
            {
                var b = pen.GetBounds();
                if (p.X < b.X - tol || p.X > b.Right + tol || p.Y < b.Y - tol || p.Y > b.Bottom + tol) return false;
                for (int i = 0; i < pen.Points.Count - 1; i++)
                    if (DistanceToSegment(p, pen.Points[i], pen.Points[i + 1]) <= pen.StrokeThickness / 2 + tol)
                        return true;
                return false;
            }
            case MagnifierItem mag:
                double dx = p.X - mag.TargetCenter.X, dy = p.Y - mag.TargetCenter.Y;
                return Math.Sqrt(dx * dx + dy * dy) <= mag.TargetRadius + tol;
            default:
            {
                var b = item.GetBounds();
                return b.X - tol <= p.X && p.X <= b.Right + tol && b.Y - tol <= p.Y && p.Y <= b.Bottom + tol;
            }
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
