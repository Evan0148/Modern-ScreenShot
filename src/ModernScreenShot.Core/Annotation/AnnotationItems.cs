using System.Text.Json.Serialization;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.Core.Annotation;

/// <summary>Point in image pixel coordinates (of the uncropped base image).</summary>
public readonly record struct PointD(double X, double Y);

public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public static RectD FromPoints(PointD a, PointD b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));

    public PixelRect ToPixelRect() => PixelRect.FromLTRB(
        (int)Math.Floor(X), (int)Math.Floor(Y), (int)Math.Ceiling(Right), (int)Math.Ceiling(Bottom));
}

public enum MosaicMode { Pixelate, Blur }

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(RectItem), "rect")]
[JsonDerivedType(typeof(EllipseItem), "ellipse")]
[JsonDerivedType(typeof(LineItem), "line")]
[JsonDerivedType(typeof(ArrowItem), "arrow")]
[JsonDerivedType(typeof(PenItem), "pen")]
[JsonDerivedType(typeof(HighlighterItem), "highlighter")]
[JsonDerivedType(typeof(TextItem), "text")]
[JsonDerivedType(typeof(StepItem), "step")]
[JsonDerivedType(typeof(MosaicItem), "mosaic")]
[JsonDerivedType(typeof(SpotlightItem), "spotlight")]
[JsonDerivedType(typeof(MagnifierItem), "magnifier")]
public abstract class AnnotationItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string StrokeColor { get; set; } = "#FFFF3B30";
    public double StrokeThickness { get; set; } = 4;
    public double Opacity { get; set; } = 1;

    /// <summary>Axis-aligned bounds in image coordinates, used for selection and hit testing.</summary>
    public abstract RectD GetBounds();

    /// <summary>Translate the item by (dx, dy).</summary>
    public abstract void Move(double dx, double dy);
}

/// <summary>Items defined by a rectangle (shape, mosaic, spotlight, text box area...).</summary>
public abstract class BoxItem : AnnotationItem
{
    public RectD Rect { get; set; }
    public override RectD GetBounds() => Rect;
    public override void Move(double dx, double dy) => Rect = Rect with { X = Rect.X + dx, Y = Rect.Y + dy };
}

public sealed class RectItem : BoxItem
{
    public bool Filled { get; set; }
    public string FillColor { get; set; } = "#40FF3B30";
    public double CornerRadius { get; set; }
}

public sealed class EllipseItem : BoxItem
{
    public bool Filled { get; set; }
    public string FillColor { get; set; } = "#40FF3B30";
}

public class LineItem : AnnotationItem
{
    public PointD Start { get; set; }
    public PointD End { get; set; }
    public bool Dashed { get; set; }

    public override RectD GetBounds() => RectD.FromPoints(Start, End);
    public override void Move(double dx, double dy)
    {
        Start = new PointD(Start.X + dx, Start.Y + dy);
        End = new PointD(End.X + dx, End.Y + dy);
    }
}

public sealed class ArrowItem : LineItem
{
    public bool DoubleHeaded { get; set; }
}

public class PenItem : AnnotationItem
{
    public List<PointD> Points { get; set; } = [];

    public override RectD GetBounds()
    {
        if (Points.Count == 0) return default;
        double l = Points.Min(p => p.X), t = Points.Min(p => p.Y), r = Points.Max(p => p.X), b = Points.Max(p => p.Y);
        return new RectD(l, t, r - l, b - t);
    }

    public override void Move(double dx, double dy)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = new PointD(Points[i].X + dx, Points[i].Y + dy);
    }
}
/// <summary>Semi-transparent wide stroke rendered with multiply-like appearance.</summary>
public sealed class HighlighterItem : PenItem
{
    public HighlighterItem()
    {
        StrokeColor = "#FFFFCC00";
        StrokeThickness = 18;
        Opacity = 0.45;
    }
}

public sealed class TextItem : AnnotationItem
{
    /// <summary>Top-left anchor of the text.</summary>
    public PointD Position { get; set; }
    public string Text { get; set; } = "";
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 20;
    public bool Bold { get; set; }
    /// <summary>Optional background box color; null = none.</summary>
    public string? BackgroundColor { get; set; }
    /// <summary>Measured size from the UI layer, stored so bounds are known without WPF.</summary>
    public double MeasuredWidth { get; set; }
    public double MeasuredHeight { get; set; }

    public override RectD GetBounds() => new(Position.X, Position.Y, Math.Max(MeasuredWidth, FontSize), Math.Max(MeasuredHeight, FontSize * 1.3));
    public override void Move(double dx, double dy) => Position = new PointD(Position.X + dx, Position.Y + dy);
}

public sealed class StepItem : AnnotationItem
{
    public PointD Center { get; set; }
    /// <summary>Assigned by AnnotationDocument.RenumberSteps.</summary>
    public int Number { get; set; } = 1;
    public double Radius { get; set; } = 16;
    public string TextColor { get; set; } = "#FFFFFFFF";

    public override RectD GetBounds() => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);
    public override void Move(double dx, double dy) => Center = new PointD(Center.X + dx, Center.Y + dy);
}

public sealed class MosaicItem : BoxItem
{
    public MosaicMode Mode { get; set; } = MosaicMode.Pixelate;
    /// <summary>Cell size for pixelate, radius for blur.</summary>
    public int Strength { get; set; } = 12;
}

/// <summary>Dims everything outside the union of all spotlight shapes.</summary>
public sealed class SpotlightItem : BoxItem
{
    public bool Elliptical { get; set; }
    public double DimOpacity { get; set; } = 0.6;
}

public sealed class MagnifierItem : AnnotationItem
{
    /// <summary>Area of the base image being magnified.</summary>
    public RectD SourceRect { get; set; }
    /// <summary>Center of the circular callout.</summary>
    public PointD TargetCenter { get; set; }
    public double Zoom { get; set; } = 2.5;

    public double TargetRadius => Math.Max(SourceRect.Width, SourceRect.Height) * Zoom / 2;

    public override RectD GetBounds()
    {
        var r = TargetRadius;
        var target = new RectD(TargetCenter.X - r, TargetCenter.Y - r, r * 2, r * 2);
        double l = Math.Min(target.X, SourceRect.X), t = Math.Min(target.Y, SourceRect.Y);
        return new RectD(l, t, Math.Max(target.Right, SourceRect.Right) - l, Math.Max(target.Bottom, SourceRect.Bottom) - t);
    }

    public override void Move(double dx, double dy) => TargetCenter = new PointD(TargetCenter.X + dx, TargetCenter.Y + dy);
}
