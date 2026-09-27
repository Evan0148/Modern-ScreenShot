using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Settings;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Overlay;

/// <summary>
/// Draws the frozen screen slice plus the whole overlay UI (dim mask, selection border and handles,
/// hover highlight, size label, 8x magnifier with coordinate/hex readout) directly in OnRender.
/// All drawing happens in window-local DIPs.
/// </summary>
internal sealed class OverlayRenderer : FrameworkElement
{
    private const int MagSamples = 15;
    private const int MagZoom = 8;
    private const double HandleSize = 10;

    private static readonly Brush DimBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x59, 0, 0, 0)));
    private static readonly Brush HoverFillBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0x0A, 0x84, 0xFF)));
    private static readonly Brush LabelBgBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E)));
    private static readonly Brush HandleFillBrush = Freeze(Brushes.White);
    private static readonly Pen AccentPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), 2));
    private static readonly Pen HandlePen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), 1.5));
    private static readonly Pen MagGridPen = Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x3C, 0xFF, 0xFF, 0xFF)), 0.5));
    private static readonly Pen MagBorderPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), 1.5));
    private static readonly Pen CrosshairPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)), 1.5));

    private readonly OverlaySession _session;
    private readonly OverlayWindow _win;
    private readonly BitmapSource _frozenFull;
    private readonly BitmapSource _slice;

    public OverlayRenderer(OverlaySession session, OverlayWindow win, BitmapSource frozenFull)
    {
        _session = session;
        _win = win;
        _frozenFull = frozenFull;
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);

        var m = win.Monitor.Bounds;
        var src = new Int32Rect(m.X - session.Virtual.X, m.Y - session.Virtual.Y, m.Width, m.Height);
        if (src.X < 0) { src.Width += src.X; src.X = 0; }
        if (src.Y < 0) { src.Height += src.Y; src.Y = 0; }
        src.Width = Math.Min(src.Width, frozenFull.PixelWidth - src.X);
        src.Height = Math.Min(src.Height, frozenFull.PixelHeight - src.Y);
        _slice = src.Width > 0 && src.Height > 0
            ? new CroppedBitmap(frozenFull, src)
            : frozenFull;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;

        dc.DrawImage(_slice, new Rect(0, 0, w, h));

        if (_session.State == OverlayState.Idle)
        {
            dc.DrawRectangle(DimBrush, null, new Rect(0, 0, w, h));
            DrawHoverHighlight(dc, w, h);
            DrawHint(dc, w, h);
        }
        if (_session.Selection is { } sel)
            DrawSelection(dc, w, h, sel, showHandles: _session.State == OverlayState.Selected);

        DrawMagnifier(dc, w, h);
    }

    private void DrawHoverHighlight(DrawingContext dc, double w, double h)
    {
        if (_session.Hover is not { } wi) return;
        var r = _win.ToLocalDip(wi.Bounds);
        if (!r.IntersectsWith(new Rect(0, 0, w, h))) return;
        dc.DrawRectangle(HoverFillBrush, null, r);
        dc.DrawRectangle(null, AccentPen, r);
        DrawLabel(dc, L.Get("Overlay.Size", wi.Bounds.Width, wi.Bounds.Height), r.Left, r.Top - 28, w, h);
    }

    private void DrawHint(DrawingContext dc, double w, double h) =>
        DrawLabel(dc, L.Get(_session.Mode == CaptureMode.WindowPick ? "Overlay.WindowHint" : "Overlay.Hint"),
            w / 2, 18, w, h, center: true);

    private void DrawSelection(DrawingContext dc, double w, double h, PixelRect sel, bool showHandles)
    {
        var r = _win.ToLocalDip(sel);

        void Dim(Rect rect)
        {
            var c = Rect.Intersect(rect, new Rect(0, 0, w, h));
            if (c.Width > 0.5 && c.Height > 0.5) dc.DrawRectangle(DimBrush, null, c);
        }
        Dim(new Rect(0, 0, w, r.Top));
        Dim(new Rect(0, r.Bottom, w, h - r.Bottom));
        Dim(new Rect(0, r.Top, r.Left, r.Height));
        Dim(new Rect(r.Right, r.Top, w - r.Right, r.Height));

        dc.DrawRectangle(null, AccentPen, r);

        if (showHandles)
        {
            foreach (var p in HandlePoints(r))
            {
                var hr = new Rect(p.X - HandleSize / 2, p.Y - HandleSize / 2, HandleSize, HandleSize);
                if (hr.Right < 0 || hr.Left > w || hr.Bottom < 0 || hr.Top > h) continue;
                dc.DrawRectangle(HandleFillBrush, HandlePen, hr);
            }
        }

        DrawLabel(dc, L.Get("Overlay.Size", sel.Width, sel.Height), r.Left, r.Top - 28, w, h);
    }

    private void DrawMagnifier(DrawingContext dc, double w, double h)
    {
        if (!_session.MagnifierEnabled || !_session.CursorValid) return;
        var cur = _session.Cursor;
        var mb = _win.Monitor.Bounds;
        if (!mb.Contains(cur.X, cur.Y)) return;

        double scale = _win.Scale;
        double magSize = MagSamples * MagZoom / scale;
        double cell = magSize / MagSamples;

        int sx = Math.Clamp(cur.X - MagSamples / 2 - _session.Virtual.X, 0, Math.Max(0, _frozenFull.PixelWidth - MagSamples));
        int sy = Math.Clamp(cur.Y - MagSamples / 2 - _session.Virtual.Y, 0, Math.Max(0, _frozenFull.PixelHeight - MagSamples));
        var crop = new CroppedBitmap(_frozenFull, new Int32Rect(sx, sy, MagSamples, MagSamples));

        double dipX = (cur.X - mb.X) / scale;
        double dipY = (cur.Y - mb.Y) / scale;
        double mx = dipX + 22, my = dipY + 22;
        if (mx + magSize + 4 > w) mx = dipX - 22 - magSize;
        if (my + magSize + 96 > h) my = dipY - 22 - magSize - 96; // leave room for the three label rows
        mx = Math.Clamp(mx, 4, Math.Max(4, w - magSize - 4));
        my = Math.Max(4, my);

        var dest = new Rect(mx, my, magSize, magSize);
        dc.DrawImage(crop, dest);
        for (int i = 1; i < MagSamples; i++)
        {
            double off = i * cell;
            dc.DrawLine(MagGridPen, new Point(mx + off, my), new Point(mx + off, my + magSize));
            dc.DrawLine(MagGridPen, new Point(mx, my + off), new Point(mx + magSize, my + off));
        }
        dc.DrawRectangle(null, MagBorderPen, dest);
        var center = new Rect(mx + cell * (MagSamples / 2), my + cell * (MagSamples / 2), cell, cell);
        dc.DrawRectangle(null, CrosshairPen, center);

        var color = _session.SampleColor(cur);
        string hex = color is { } c ? c.ToHex(includeAlpha: false).ToUpperInvariant() : "";
        double ly = my + magSize + 6;
        DrawLabel(dc, L.Get("Overlay.Position", cur.X, cur.Y), mx, ly, w, h);
        if (color is not null) DrawLabel(dc, hex, mx, ly + 26, w, h);
        DrawLabel(dc,
            _session.ColorFlashActive ? L.Get("Toast.ColorCopied", hex) : L.Get("Overlay.CopyColorHint"),
            mx, ly + 52, w, h);
    }

    private static Point[] HandlePoints(Rect r) =>
    [
        new(r.Left, r.Top), new(r.Right, r.Top), new(r.Left, r.Bottom), new(r.Right, r.Bottom),
        new(r.Left + r.Width / 2, r.Top), new(r.Left + r.Width / 2, r.Bottom),
        new(r.Left, r.Top + r.Height / 2), new(r.Right, r.Top + r.Height / 2),
    ];

    private Rect DrawLabel(DrawingContext dc, string text, double x, double y, double w, double h, bool center = false)
    {
        var ft = MakeText(text);
        double bgW = ft.Width + 12, bgH = ft.Height + 6;
        if (center) x -= bgW / 2;
        x = Math.Clamp(x, 2, Math.Max(2, w - bgW - 2));
        y = Math.Clamp(y, 2, Math.Max(2, h - bgH - 2));
        var rect = new Rect(x, y, bgW, bgH);
        dc.DrawRoundedRectangle(LabelBgBrush, null, rect, 4, 4);
        dc.DrawText(ft, new Point(x + 6, y + 3));
        return rect;
    }

    private FormattedText MakeText(string text, double size = 12, bool bold = false) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
                bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, Brushes.White, _win.Scale);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
