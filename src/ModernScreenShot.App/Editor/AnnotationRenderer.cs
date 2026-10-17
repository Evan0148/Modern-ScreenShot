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

    private static readonly Brush Checkerboard = BuildCheckerboard();

    /// <summary>Transparency checkerboard for image backdrops (editor canvas, effects preview):
    /// light squares keep black shadows and dark content readable over transparent areas.</summary>
    public static Brush CheckerboardBrush() => Checkerboard;

    private static Brush BuildCheckerboard()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing
        {
            Brush = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0xE2, 0xE6))),
            Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)),
        });
        var dark = Freeze(new SolidColorBrush(Color.FromRgb(0xC2, 0xC2, 0xC8)));
        foreach (var (x, y) in new[] { (0, 0), (8, 8) })
            group.Children.Add(new GeometryDrawing
            {
                Brush = dark,
                Geometry = new RectangleGeometry(new Rect(x, y, 8, 8)),
            });
        return Freeze(new DrawingBrush
        {
            Drawing = group,
            Viewport = new Rect(0, 0, 16, 16),
            ViewportUnits = BrushMappingMode.Absolute,
            TileMode = TileMode.Tile,
        });
    }

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
        var start = new Point(a.Start.X, a.Start.Y);
        var end = new Point(a.End.X, a.End.Y);
        var d = end - start;
        double len = d.Length;
        if (len < 0.001) return;
        d /= len;
        var perp = new Vector(-d.Y, d.X);

        // The head sits at the drag END (the release point), like every other screenshot tool.
        // Short arrows get a proportionally smaller head so it never swamps the shaft.
        double scale = ArrowItem.HeadScale(len, th);
        double headLen = ArrowItem.HeadLength(th) * scale;
        double headW = ArrowItem.HeadHalfWidth(th) * scale;

        // Dart-shaped head: tip, two base corners and a back notch. The base is much wider than
        // the shaft, so the joint is covered; the shaft terminates in the notch.
        Point DrawHead(Point tip, Vector dir)
        {
            var c1 = tip - dir * headLen + perp * headW;
            var c2 = tip - dir * headLen - perp * headW;
            var notch = tip - dir * headLen * ArrowItem.NotchRatio;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(tip, true, true);
                ctx.LineTo(c1, true, false);
                ctx.LineTo(notch, true, false);
                ctx.LineTo(c2, true, false);
            }
            dc.DrawGeometry(brush, null, geo);
            return notch;
        }

        var shaftStart = a.DoubleHeaded ? DrawHead(start, -d) : start;
        var shaftEnd = DrawHead(end, d);
        var pen = new Pen(brush, th)
        {
            // A round cap at the head side would poke past the dart's notch; the free tail stays round.
            StartLineCap = a.DoubleHeaded ? PenLineCap.Flat : PenLineCap.Round,
            EndLineCap = PenLineCap.Flat,
            LineJoin = PenLineJoin.Round,
        };
        if (a.Dashed) pen.DashStyle = new DashStyle([3, 2], 0);
        dc.DrawLine(pen, shaftStart, shaftEnd);
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

    private static double Dist(PointD a, PointD b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>True when p falls inside a head's base triangle (tip + two base corners), loosened
    /// by the eraser tolerance. Mirrors DrawArrow's geometry, including the short-arrow shrink.</summary>
    private static bool HeadTriangleContains(PointD tip, PointD from, double thickness, double scale, PointD p)
    {
        double dx = tip.X - from.X, dy = tip.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6) return false;
        double ux = dx / len, uy = dy / len, px = -uy, py = ux;
        double l = ArrowItem.HeadLength(Math.Max(1, thickness)) * scale;
        double hw = ArrowItem.HeadHalfWidth(Math.Max(1, thickness)) * scale;
        var c1 = new PointD(tip.X - ux * l + px * hw, tip.Y - uy * l + py * hw);
        var c2 = new PointD(tip.X - ux * l - px * hw, tip.Y - uy * l - py * hw);
        double s1 = Cross(c1.X - tip.X, c1.Y - tip.Y, p.X - tip.X, p.Y - tip.Y);
        double s2 = Cross(c2.X - c1.X, c2.Y - c1.Y, p.X - c1.X, p.Y - c1.Y);
        double s3 = Cross(tip.X - c2.X, tip.Y - c2.Y, p.X - c2.X, p.Y - c2.Y);
        return (s1 >= 0 && s2 >= 0 && s3 >= 0) || (s1 <= 0 && s2 <= 0 && s3 <= 0);
    }

    private static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;

    /// <summary>
    /// Geometry-aware hit test for the eraser: true stroke geometry for lines/pens (a shallow
    /// diagonal's bounding box is far larger than its ink), the head triangles for arrows,
    /// circle for the magnifier callout, bounding box for area shapes. <paramref name="tol"/> is in image pixels.
    /// </summary>
    public static bool HitsForErase(AnnotationItem item, PointD p, double tol)
    {
        switch (item)
        {
            case ArrowItem a: // ArrowItem : LineItem — must precede LineItem
                if (DistanceToSegment(p, a.Start, a.End) <= a.StrokeThickness / 2 + tol) return true;
                double alen = Dist(a.Start, a.End);
                double ascale = ArrowItem.HeadScale(alen, Math.Max(1, a.StrokeThickness));
                if (HeadTriangleContains(a.End, a.Start, a.StrokeThickness, ascale, p)) return true;
                if (a.DoubleHeaded && HeadTriangleContains(a.Start, a.End, a.StrokeThickness, ascale, p)) return true;
                return false;
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
