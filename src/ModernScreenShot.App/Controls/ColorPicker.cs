using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.App.Controls;

/// <summary>
/// Compact HSV color picker drawn in OnRender: saturation/value square, hue bar and alpha bar
/// (both 20 DIP). Raises <see cref="ColorChanged"/> on every interaction.
/// </summary>
public sealed class ColorPicker : FrameworkElement
{
    private const double BarHeight = 20;
    private const double BarGap = 8;

    private double _hue = 210;   // 0-360
    private double _sat = 1;     // 0-1
    private double _val = 1;     // 0-1
    private byte _alpha = 255;
    private bool _dragging;

    public static readonly RoutedEvent ColorChangedEvent = EventManager.RegisterRoutedEvent(
        nameof(ColorChanged), RoutingStrategy.Bubble, typeof(EventHandler), typeof(ColorPicker));

    public event EventHandler ColorChanged
    {
        add => AddHandler(ColorChangedEvent, value);
        remove => RemoveHandler(ColorChangedEvent, value);
    }

    public ColorPicker()
    {
        MinWidth = 220;
        MinHeight = 220;
        Cursor = Cursors.Cross;
    }

    /// <summary>Gets or sets the current color (raises ColorChanged on programmatic set as well).</summary>
    public Color Color
    {
        get => CurrentMediaColor();
        set
        {
            var pc = new PixelColor(value.A, value.R, value.G, value.B);
            (_hue, _sat, _val) = ColorUtil.ToHsv(pc);
            _alpha = pc.A;
            InvalidateVisual();
            RaiseEvent(new RoutedEventArgs(ColorChangedEvent));
        }
    }

    public PixelColor PixelColorValue => CurrentPixelColor();

    private PixelColor CurrentPixelColor() => ColorUtil.FromHsv(_hue, _sat, _val, _alpha);

    private Color CurrentMediaColor()
    {
        var c = CurrentPixelColor();
        return Color.FromArgb(c.A, c.R, c.G, c.B);
    }

    private Rect SvRect => new(0, 0, Math.Max(1, ActualWidth), Math.Max(1, ActualHeight - 2 * BarHeight - 2 * BarGap));
    private Rect HueRect => new(0, SvRect.Bottom + BarGap, ActualWidth, BarHeight);
    private Rect AlphaRect => new(0, HueRect.Bottom + BarGap, ActualWidth, BarHeight);

    protected override void OnRender(DrawingContext dc)
    {
        if (ActualWidth < 20 || ActualHeight < 60) return;
        var sv = SvRect;
        var hueColor = MediaFromHsv(_hue, 1, 1, 255);

        dc.DrawRoundedRectangle(Brushes.White, LightBorder, sv, 2, 2);
        dc.PushClip(new RectangleGeometry(sv, 2, 2));
        dc.DrawRectangle(new SolidColorBrush(hueColor), null, sv);
        var whiteFade = new LinearGradientBrush(Colors.White, Colors.Transparent, 0);
        dc.DrawRectangle(whiteFade, null, sv);
        var blackFade = new LinearGradientBrush(Colors.Transparent, Colors.Black, 90);
        dc.DrawRectangle(blackFade, null, sv);

        // selection thumb
        double tx = sv.X + _sat * sv.Width, ty = sv.Y + (1 - _val) * sv.Height;
        dc.DrawEllipse(Brushes.Transparent, ThumbPen, new Point(tx, ty), 7, 7);
        dc.Pop();

        DrawBar(dc, HueRect, HueGradient());
        var hx = HueRect.X + _hue / 360 * HueRect.Width;
        dc.DrawLine(ThumbPen, new Point(hx, HueRect.Top - 1), new Point(hx, HueRect.Bottom + 1));

        var rgb = MediaFromHsv(_hue, _sat, _val, 255);
        var alphaGrad = new LinearGradientBrush(Color.FromArgb(0, rgb.R, rgb.G, rgb.B), rgb, 0);
        DrawCheckerboard(dc, AlphaRect);
        dc.DrawRectangle(alphaGrad, null, AlphaRect);
        var ax = AlphaRect.X + _alpha / 255.0 * AlphaRect.Width;
        dc.DrawLine(ThumbPen, new Point(ax, AlphaRect.Top - 1), new Point(ax, AlphaRect.Bottom + 1));
    }

    private static void DrawBar(DrawingContext dc, Rect rect, Brush fill)
    {
        dc.DrawRoundedRectangle(fill, LightBorder, rect, 3, 3);
    }

    private static Brush HueGradient()
    {
        var grad = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        for (int i = 0; i <= 6; i++)
            grad.GradientStops.Add(new GradientStop(MediaFromHsv(i * 60, 1, 1, 255), i / 6.0));
        return grad;
    }

    private static void DrawCheckerboard(DrawingContext dc, Rect rect)
    {
        var light = new SolidColorBrush(Color.FromRgb(0xC8, 0xC8, 0xC8));
        var dark = new SolidColorBrush(Color.FromRgb(0x90, 0x90, 0x90));
        dc.DrawRectangle(light, null, rect);
        dc.PushClip(new RectangleGeometry(rect));
        const double tile = 6;
        for (int y = 0; y * tile < rect.Height; y++)
            for (int x = 0; x * tile < rect.Width; x++)
                if ((x + y) % 2 == 0)
                    dc.DrawRectangle(dark, null, new Rect(rect.X + x * tile, rect.Y + y * tile, tile, tile));
        dc.Pop();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _dragging = true;
        CaptureMouse();
        HandlePoint(e.GetPosition(this));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_dragging) HandlePoint(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragging = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void HandlePoint(Point p)
    {
        var sv = SvRect;
        if (p.Y <= sv.Bottom)
        {
            _sat = Math.Clamp((p.X - sv.X) / sv.Width, 0, 1);
            _val = Math.Clamp(1 - (p.Y - sv.Y) / sv.Height, 0, 1);
        }
        else if (HueRect.Contains(p))
        {
            _hue = Math.Clamp((p.X - HueRect.X) / HueRect.Width, 0, 0.9999) * 360;
        }
        else if (AlphaRect.Contains(p))
        {
            _alpha = (byte)Math.Round(Math.Clamp((p.X - AlphaRect.X) / AlphaRect.Width, 0, 1) * 255);
        }
        else return;
        InvalidateVisual();
        RaiseEvent(new RoutedEventArgs(ColorChangedEvent));
    }

    private static Color MediaFromHsv(double h, double s, double v, byte a)
    {
        var c = ColorUtil.FromHsv(h, s, v, a);
        return Color.FromArgb(c.A, c.R, c.G, c.B);
    }

    private static readonly Pen ThumbPen = new(Brushes.White, 2);
    private static readonly Pen LightBorder = new(new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)), 1);

    static ColorPicker()
    {
        ThumbPen.Freeze();
        LightBorder.Freeze();
    }
}
