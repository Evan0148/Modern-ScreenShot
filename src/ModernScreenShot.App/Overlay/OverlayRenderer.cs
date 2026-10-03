using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModernScreenShot.App.Editor;
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
    private BitmapSource _frozenFull;   // swapped in place when the session refreshes the frozen frame (F5)
    private BitmapSource _slice;
    private long _lastSignature = long.MinValue;

    // Reused across frames: a new Typeface/FontFamily per label caused steady per-move GC pressure.
    private static readonly Typeface LabelTypeface = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    // The magnifier crop follows the cursor; caching it by source origin avoids a new CroppedBitmap
    // (+ Freeze) allocation on every single mouse-move frame.
    private CroppedBitmap? _magCache;
    private int _magCacheX = int.MinValue, _magCacheY = int.MinValue;

    // Hover-box snap animation: when the detected element changes, the highlight glides from its old
    // rect to the new one instead of jumping. Time-based exponential easing keeps the speed constant
    // regardless of frame rate. Rects are window-local DIPs (what OnRender draws).
    private const double HoverAnimSeconds = 0.14;     // ~140 ms to essentially settle
    private IntPtr _hoverAnimHandle = IntPtr.Zero;    // the element _animRect is easing toward
    private Rect _animRect;                            // currently drawn (interpolated) rect
    private bool _animActive;                          // false until the first hover establishes a rect
    private long _animLastTicks;                       // Stopwatch ticks of the previous advance
    /// <summary>True while the hover box is still gliding toward its target; the window keeps the
    /// render loop repainting this monitor until it settles.</summary>
    internal bool HoverAnimating { get; private set; }

    /// <summary>
    /// True when anything this monitor draws may have changed since the last render. Lets idle
    /// monitors skip a frame during a drag while never dropping a frame the window actually needs.
    /// Conservative: any relevance to this monitor (cursor here, or selection/hover overlapping it)
    /// forces a redraw; the early-out only triggers when this monitor stays uninvolved.
    /// </summary>
    internal bool StateChanged()
    {
        long sig = ComputeSignature();
        // long.MinValue == "involved / uncertain": always redraw and never cache it as clean.
        if (sig == long.MinValue) { _lastSignature = long.MinValue; return true; }
        if (sig == _lastSignature) return false;
        _lastSignature = sig;
        return true;
    }

    private long ComputeSignature()
    {
        var mb = _win.Monitor.Bounds;
        // The magnifier, hover box and selection all move with the cursor/drag and can straddle
        // monitor edges, so if any of them touches this monitor, treat every frame as dirty.
        if (_session.CursorValid && mb.Contains(_session.Cursor.X, _session.Cursor.Y)) return long.MinValue;
        if (_session.Selection is { } sel && !mb.Intersect(sel).IsEmpty) return long.MinValue;
        if (_session.Hover is { } hv && !mb.Intersect(hv.Bounds).IsEmpty) return long.MinValue;
        if (_session.HoverMonitorBounds is { } hmb && !mb.Intersect(hmb).IsEmpty) return long.MinValue;
        // Nothing on this monitor: a stable, cheap signature so repeated idle frames coalesce.
        // The transient hint is session-wide (visible on every monitor), so it must be part of the
        // signature — otherwise its expiry repaint would be skipped as "unchanged".
        return ((long)_session.State << 2) | (_session.Selection is null ? 0L : 1L) | (_session.TransientHintActive ? 2L : 0L);
    }

    public OverlayRenderer(OverlaySession session, OverlayWindow win, BitmapSource frozenFull)
    {
        _session = session;
        _win = win;
        _frozenFull = frozenFull;
        Focusable = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        _slice = BuildSlice(frozenFull);
    }

    /// <summary>Slice of the frozen frame covering this window's monitor, or the whole frame when
    /// the monitor does not lie inside it (degenerate topology fallback).</summary>
    private BitmapSource BuildSlice(BitmapSource frozenFull)
    {
        var m = _win.Monitor.Bounds;
        var src = new Int32Rect(m.X - _session.Virtual.X, m.Y - _session.Virtual.Y, m.Width, m.Height);
        if (src.X < 0) { src.Width += src.X; src.X = 0; }
        if (src.Y < 0) { src.Height += src.Y; src.Y = 0; }
        src.Width = Math.Min(src.Width, frozenFull.PixelWidth - src.X);
        src.Height = Math.Min(src.Height, frozenFull.PixelHeight - src.Y);
        return src.Width > 0 && src.Height > 0
            ? new CroppedBitmap(frozenFull, src)
            : frozenFull;
    }

    /// <summary>Swaps in the refreshed frozen frame (F5 / ` / !): rebuilds this monitor's slice and
    /// drops the magnifier cache, which still references the old frame's pixels.</summary>
    internal void Reslice(BitmapSource frozenFull)
    {
        _frozenFull = frozenFull;
        _slice = BuildSlice(frozenFull);
        _magCache = null;
        _magCacheX = int.MinValue;
        _magCacheY = int.MinValue;
        InvalidateVisual();
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
        {
            DrawSelectionFrame(dc, w, h, sel);
            DrawAnnotations(dc, sel);
            DrawSelectionDecorations(dc, w, h, sel, showHandles: _session.State == OverlayState.Selected);
        }

        if (_session.TransientHint is { } hint) // e.g. the detection mode after Tab (timestamp-gated)
        {
            DrawLabel(dc, hint, w / 2, 46, w, h, center: true);
        }

        DrawMagnifier(dc, w, h);
    }

    private void DrawHoverHighlight(DrawingContext dc, double w, double h)
    {
        var wi = _session.Hover;
        PixelRect? monitorTarget = wi is null ? _session.HoverMonitorBounds : null;
        if (wi is null && monitorTarget is null)
        {
            _animActive = false;
            _hoverAnimHandle = IntPtr.Zero;
            HoverAnimating = false;
            return;
        }
        PixelRect bounds = wi?.Bounds ?? monitorTarget!.Value;
        var target = _win.ToLocalDip(bounds);
        // The whole-monitor target is not a window; a synthetic handle keeps its glide animation
        // independent from every real hwnd.
        var handle = wi?.Handle ?? new IntPtr(-1);
        var r = AdvanceHoverAnim(handle, target);
        if (!r.IntersectsWith(new Rect(0, 0, w, h))) return;
        dc.DrawRectangle(HoverFillBrush, null, r);
        dc.DrawRectangle(null, AccentPen, r);
        // The size label reads the true element size (not the mid-animation rect) so it never flickers.
        DrawLabel(dc, L.Get("Overlay.Size", bounds.Width, bounds.Height), r.Left, r.Top - 28, w, h);
    }

    /// <summary>Eases the drawn hover rect toward <paramref name="target"/>. A new element snaps the
    /// animation origin to the current rect and glides; the same element continues its glide. Returns
    /// the rect to draw this frame and updates <see cref="HoverAnimating"/>.</summary>
    private Rect AdvanceHoverAnim(IntPtr handle, Rect target)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        // Reset the clock baseline on first hover AND on a switch to a new element, so the elapsed
        // time never includes however long the cursor rested on the previous element (which would
        // otherwise make dt huge and snap straight to the new target with no glide). The switch frame
        // itself moves ~0; the glide plays out over the following render frames.
        if (!_animActive || handle != _hoverAnimHandle)
        {
            if (!_animActive) _animRect = target; // first hover: start already on the target
            _hoverAnimHandle = handle;
            _animActive = true;
            _animLastTicks = now;
        }
        double dt = (now - _animLastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        _animLastTicks = now;
        if (dt < 0) dt = 0;

        // Exponential smoothing: fraction of the remaining distance to cover this frame. Frame-rate
        // independent — a longer dt covers proportionally more ground.
        double a = 1 - Math.Exp(-dt / (HoverAnimSeconds / 4.0));
        _animRect = new Rect(
            _animRect.X + (target.X - _animRect.X) * a,
            _animRect.Y + (target.Y - _animRect.Y) * a,
            _animRect.Width + (target.Width - _animRect.Width) * a,
            _animRect.Height + (target.Height - _animRect.Height) * a);

        // Settle: once within sub-pixel distance, snap exactly and stop requesting frames.
        bool settled = Math.Abs(_animRect.X - target.X) < 0.5 && Math.Abs(_animRect.Y - target.Y) < 0.5
            && Math.Abs(_animRect.Width - target.Width) < 0.5 && Math.Abs(_animRect.Height - target.Height) < 0.5;
        if (settled)
        {
            _animRect = target;
            HoverAnimating = false;
        }
        else HoverAnimating = true;
        return _animRect;
    }

    private void DrawHint(DrawingContext dc, double w, double h) =>
        DrawLabel(dc, L.Get(_session.Mode == CaptureMode.WindowPick ? "Overlay.WindowHint" : "Overlay.Hint"),
            w / 2, 18, w, h, center: true);

    /// <summary>Dim mask outside the selection plus the selection border (under the annotations).</summary>
    private void DrawSelectionFrame(DrawingContext dc, double w, double h, PixelRect sel)
    {
        var r = _win.ToLocalDip(sel);

        // Build each dim band from clamped edges: a selection lying partly (or wholly) outside this
        // monitor makes the naive new Rect(0, r.Bottom, w, h - r.Bottom) negative-sized, which throws
        // ArgumentException from the Rect ctor and kills the whole frame (looked like flicker/jank).
        void Dim(double left, double top, double right, double bottom)
        {
            double l = Math.Max(0, left), t = Math.Max(0, top);
            double rr = Math.Min(w, right), bb = Math.Min(h, bottom);
            if (rr - l > 0.5 && bb - t > 0.5) dc.DrawRectangle(DimBrush, null, new Rect(l, t, rr - l, bb - t));
        }
        Dim(0, 0, w, r.Top);              // above
        Dim(0, r.Bottom, w, h);          // below
        Dim(0, r.Top, r.Left, r.Bottom); // left
        Dim(r.Right, r.Top, w, r.Bottom);// right

        if (new Rect(0, 0, w, h).IntersectsWith(r)) dc.DrawRectangle(null, AccentPen, r);
    }

    /// <summary>Inline annotations + mosaic layer, clipped to the selection. Annotation coordinates
    /// are image pixels relative to the selection's top-left; 1 image px = 1/Scale DIP on screen.</summary>
    private void DrawAnnotations(DrawingContext dc, PixelRect sel)
    {
        if (_session.Doc.Items.Count == 0 && _session.PreviewItem is null) return;
        var local = _win.ToLocalDip(sel);
        double s = _win.Scale;
        var transform = new TransformGroup();
        transform.Children.Add(new ScaleTransform(1 / s, 1 / s));
        transform.Children.Add(new TranslateTransform(local.X, local.Y));
        dc.PushClip(new RectangleGeometry(local));
        dc.PushTransform(transform);
        AnnotationRenderer.RenderDocument(dc, _session.Doc, _slice, _session.MosaicSource);
        if (_session.PreviewItem is { } preview)
            AnnotationRenderer.RenderItem(dc, preview, _slice, _session.MosaicSource);
        dc.Pop();
        dc.Pop();
    }

    /// <summary>Resize handles and the size label, drawn above the annotations.</summary>
    private void DrawSelectionDecorations(DrawingContext dc, double w, double h, PixelRect sel, bool showHandles)
    {
        var r = _win.ToLocalDip(sel);
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
        if (_magCache is null || sx != _magCacheX || sy != _magCacheY)
        {
            var crop = new CroppedBitmap(_frozenFull, new Int32Rect(sx, sy, MagSamples, MagSamples));
            crop.Freeze(); // a frozen bitmap skips per-frame change-tracking overhead
            _magCache = crop;
            _magCacheX = sx;
            _magCacheY = sy;
        }
        var magBitmap = _magCache;

        double dipX = (cur.X - mb.X) / scale;
        double dipY = (cur.Y - mb.Y) / scale;
        double mx = dipX + 22, my = dipY + 22;
        if (mx + magSize + 4 > w) mx = dipX - 22 - magSize;
        if (my + magSize + 96 > h) my = dipY - 22 - magSize - 96; // leave room for the three label rows
        mx = Math.Clamp(mx, 4, Math.Max(4, w - magSize - 4));
        my = Math.Max(4, my);

        var dest = new Rect(mx, my, magSize, magSize);
        dc.DrawImage(magBitmap, dest);
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
            bold ? BoldTypeface : LabelTypeface,
            size, Brushes.White, _win.Scale);

    private static readonly Typeface BoldTypeface = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
