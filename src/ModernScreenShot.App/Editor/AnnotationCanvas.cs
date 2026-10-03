using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Settings;

namespace ModernScreenShot.App.Editor;

public enum EditorTool { Select, Rect, Ellipse, Line, Arrow, Pen, Text, Step, Highlighter, Mosaic, Blur, Spotlight, Magnifier, Crop, Eraser }

internal enum CanvasMode { Idle, Drawing, Moving, Resizing, Panning, CropDraft, CropResize }
internal enum CanvasHandle { None, N, S, E, W, NW, NE, SW, SE, Body }

public sealed class TextEditRequestEventArgs(TextItem? existing, PointD position) : EventArgs
{
    public TextItem? Existing { get; } = existing;
    public PointD Position { get; } = position;
}

public sealed class ZoomRequestEventArgs(int delta, PointD imagePoint) : EventArgs
{
    public int Delta { get; } = delta;
    public PointD ImagePoint { get; } = imagePoint;
}

/// <summary>
/// Interactive canvas for the annotation document. Renders via <see cref="AnnotationRenderer"/> in
/// image-pixel space wrapped in a zoom transform; converts mouse input back to image pixels.
/// Undo snapshots are captured BEFORE a drag starts and pushed when it commits.
/// </summary>
public sealed class AnnotationCanvas : FrameworkElement
{
    private const double HandleScreenSize = 8;
    private const double HandleGrab = 6;
    private const double DefaultMagnifierSource = 110;

    private AnnotationDocument _doc;
    private readonly PixelBuffer _baseBuffer;
    private PixelBuffer? _mosaicPristine;
    private PixelBuffer? _mosaicBuffer;
    private BitmapSource? _mosaicSource;
    private DateTime _lastMosaicRecompute = DateTime.MinValue;

    private CanvasMode _mode = CanvasMode.Idle;
    private AnnotationItem? _preview;
    private PointD _startPoint;
    private PointD _lastPoint;
    private AnnotationItem? _selected;
    private CanvasHandle _handle = CanvasHandle.None;
    private RectD _resizeAnchor;
    private PixelRect? _cropDraft;
    private PixelRect _cropAnchor;
    private Point _panOrigin;
    private double _panOffsetX;
    private double _panOffsetY;
    private string? _pendingSnapshot;

    public AnnotationCanvas(PixelBuffer baseImage)
    {
        _baseBuffer = baseImage;
        BaseImage = baseImage.ToBitmapSource();
        _doc = new AnnotationDocument { ImageWidth = baseImage.Width, ImageHeight = baseImage.Height };
        Width = baseImage.Width;
        Height = baseImage.Height;
    }

    public BitmapSource BaseImage { get; }
    public UndoStack Undo { get; } = new();
    public AnnotationDocument Document => _doc;
    public BitmapSource? MosaicSource => _mosaicSource;
    public int ImageWidth => _doc.ImageWidth;
    public int ImageHeight => _doc.ImageHeight;
    public double Zoom { get; private set; } = 1;
    public AnnotationItem? Selected => _selected;
    public ScrollViewer? ScrollOwner { get; set; }

    public event EventHandler? DocumentChanged;
    public event EventHandler? SelectionChanged;
    public event EventHandler<TextEditRequestEventArgs>? TextEditRequested;
    public event EventHandler<ZoomRequestEventArgs>? ZoomRequested;

    // Current item defaults (mirrored from the property panel).
    public EditorTool Tool { get; set; } = EditorTool.Select;
    public bool SpaceHeld { get; set; }
    public string StrokeColor { get; set; } = "#FFFF3B30";
    public double StrokeThickness { get; set; } = 4;
    public bool FillShape { get; set; }
    public bool DashedLine { get; set; }
    public double FontSize { get; set; } = 20;
    public bool FontBold { get; set; }
    public double ItemOpacity { get; set; } = 1;
    public MosaicMode MosaicMode { get; set; } = MosaicMode.Pixelate;
    public int MosaicStrength { get; set; } = 12;
    public bool SpotlightElliptical { get; set; }
    public double SpotlightDim { get; set; } = 0.6;
    public double MagnifierZoom { get; set; } = 2.5;
    public double StepRadius { get; set; } = 16;
    private bool _erasedAny; // eraser stroke: at least one committed item was deleted

    // ---- public operations ----

    public void SetZoom(double zoom)
    {
        Zoom = Math.Clamp(zoom, 0.05, 8);
        Width = ImageWidth * Zoom;
        Height = ImageHeight * Zoom;
        InvalidateVisual();
    }

    public void Select(AnnotationItem? item)
    {
        _selected = item;
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void DeleteSelected()
    {
        if (_selected is null) return;
        BeginEdit();
        _doc.Items.Remove(_selected);
        if (_selected is StepItem) _doc.RenumberSteps();
        if (_selected is MosaicItem) RecomputeMosaic();
        _selected = null;
        CommitEdit();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void BringToFront()
    {
        if (_selected is null) return;
        BeginEdit();
        _doc.Items.Remove(_selected);
        _doc.Items.Add(_selected);
        CommitEdit();
    }

    public void SendToBack()
    {
        if (_selected is null) return;
        BeginEdit();
        _doc.Items.Remove(_selected);
        _doc.Items.Insert(0, _selected);
        CommitEdit();
    }

    public void DoUndo()
    {
        // A live _pendingSnapshot means an edit batch (slider drag, crop draft) is mid-flight even
        // though _mode is Idle; undoing now would push that stale snapshot on the next commit and
        // corrupt the undo chain. Block until the batch commits or aborts.
        if (_mode != CanvasMode.Idle || _preview is not null || _pendingSnapshot is not null) return;
        var d = Undo.Undo(_doc);
        if (d is not null) ReplaceDocument(d);
    }

    public void DoRedo()
    {
        if (_mode != CanvasMode.Idle || _preview is not null || _pendingSnapshot is not null) return;
        var d = Undo.Redo(_doc);
        if (d is not null) ReplaceDocument(d);
    }

    /// <summary>Applies the crop draft (Enter / double-click with the crop tool).</summary>
    public void ApplyCrop()
    {
        if (_cropDraft is not { } draft) return;
        if (draft.Width < 4 || draft.Height < 4)
        {
            _cropDraft = null;
            _pendingSnapshot = null; // no change happened
            InvalidateVisual();
            return;
        }
        BeginEdit();
        _doc.Crop = draft;
        _cropDraft = null;
        CommitEdit();
    }

    /// <summary>Esc with the crop tool: first press discards the draft, second removes an applied crop.</summary>
    public void CancelCrop()
    {
        if (_cropDraft is not null)
        {
            _cropDraft = null;
            _pendingSnapshot = null; // the pre-draft snapshot must not leak into the next commit
            InvalidateVisual();
            return;
        }
        if (_doc.Crop is not null)
        {
            BeginEdit();
            _doc.Crop = null;
            CommitEdit();
        }
    }

    /// <summary>Esc during any interaction: reverts in-progress drags, discards the crop draft,
    /// then falls through to crop removal / deselect.</summary>
    public void AbortInteraction()
    {
        switch (_mode)
        {
            case CanvasMode.Drawing:
                _preview = null;
                if (_erasedAny) CommitEdit(); // an erase stroke already mutated the doc: keep it undoable
                else _pendingSnapshot = null;
                _erasedAny = false;
                _mode = CanvasMode.Idle;
                InvalidateVisual();
                return;
            case CanvasMode.Moving or CanvasMode.Resizing:
                RestoreFromPending();
                _mode = CanvasMode.Idle;
                InvalidateVisual();
                return;
            case CanvasMode.CropDraft or CanvasMode.CropResize:
                _cropDraft = null;
                _pendingSnapshot = null;
                _mode = CanvasMode.Idle;
                InvalidateVisual();
                return;
        }
        if (_cropDraft is not null && Tool != EditorTool.Crop)
        {
            // The draft outlived the crop tool (tool was switched after releasing the mouse);
            // otherwise it would stay on screen forever and Esc could not dismiss it.
            _cropDraft = null;
            _pendingSnapshot = null;
            InvalidateVisual();
        }
        if (Tool == EditorTool.Crop)
        {
            CancelCrop();
            return;
        }
        Select(null);
    }

    /// <summary>Reverts live (not yet committed) mutations by restoring the pending pre-drag snapshot.</summary>
    private void RestoreFromPending()
    {
        if (_pendingSnapshot is not { } snap) return;
        _doc = UndoStack.Deserialize(snap);
        _pendingSnapshot = null;
        if (_selected is { } s) _selected = _doc.Items.FirstOrDefault(i => i.Id == s.Id);
        RecomputeMosaic();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Begins an undo batch for property edits made outside the mouse pipeline.</summary>
    public void BeginItemEdit() => BeginEdit();

    /// <summary>Discards a pending property-edit snapshot without pushing an undo step.</summary>
    public void CancelItemEdit() => _pendingSnapshot = null;

    public void EndItemEdit()
    {
        if (_selected is MosaicItem) RecomputeMosaic();
        CommitEdit();
    }

    public void CommitTextEdit(TextItem? existing, string text, PointD position)
    {
        if (_mode != CanvasMode.Idle) return;
        if (existing is null)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            BeginEdit();
            var item = new TextItem
            {
                Position = position,
                Text = text,
                FontSize = FontSize,
                Bold = FontBold,
                StrokeColor = StrokeColor,
                Opacity = ItemOpacity,
            };
            MeasureTextItem(item);
            _doc.Items.Add(item);
            CommitEdit();
            Select(item);
        }
        else
        {
            if (existing.Text == text) return;
            BeginEdit();
            if (string.IsNullOrWhiteSpace(text))
            {
                // An emptied text item is invisible but would still be selectable and renumbered; remove it.
                _doc.Items.Remove(existing);
                if (_selected == existing) Select(null);
            }
            else
            {
                existing.Text = text;
                MeasureTextItem(existing);
            }
            CommitEdit();
        }
    }

    /// <summary>Adopts a previously saved document (history re-edit) as the working document.</summary>
    public void LoadDocument(AnnotationDocument doc)
    {
        _doc = doc;
        _selected = null;
        // A doc.json that still parses can carry explicit JSON nulls that deserialize right over
        // the property initializers (null Items/Effects crash consumers downstream). Repair before
        // anything runs — the same hardening SettingsStore.Normalize does for hand-edited settings.
        doc.Items ??= [];
        doc.Items.RemoveAll(i => i is null);
        doc.Effects ??= new EffectSettings();
        doc.Effects.Shadow ??= new ShadowOptions();
        doc.Effects.Reflection ??= new ReflectionOptions();
        doc.Effects.Frame ??= new FrameOptions();
        // A zero/negative document size (legacy or corrupt doc.json that still parses) would make
        // this canvas element 0x0 — nothing ever renders and the editor shows its flat dark
        // workbench where the screenshot should be. The base image is the ground truth of the
        // capture, so fall back to it. Must run before RecomputeMosaic: the mosaic pass clips
        // against the document size, and once-skipped rects would stay unrecomputed.
        if (doc.ImageWidth <= 0 || doc.ImageHeight <= 0)
        {
            Log.Warn($"Document has invalid dimensions {doc.ImageWidth}x{doc.ImageHeight}; using the base image {_baseBuffer.Width}x{_baseBuffer.Height}.");
            doc.ImageWidth = _baseBuffer.Width;
            doc.ImageHeight = _baseBuffer.Height;
        }
        // Docs created programmatically (or saved before text measuring existed) carry no measured
        // size; the GetBounds FontSize fallback then collapses selection handles and hit-testing
        // to a tiny box at the text origin. Measure on adoption; stored sizes stay untouched.
        foreach (var item in doc.Items)
            if (item is TextItem { MeasuredWidth: <= 0 } t) MeasureTextItem(t);
        RecomputeMosaic();
        Width = doc.ImageWidth;
        Height = doc.ImageHeight;
        InvalidateVisual();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public static void MeasureTextItem(TextItem t)
    {
        var ft = AnnotationRenderer.BuildText(t);
        t.MeasuredWidth = ft.Width + 4;
        t.MeasuredHeight = ft.Height + 4;
    }

    // ---- undo plumbing ----

    private void BeginEdit() => _pendingSnapshot ??= UndoStack.Serialize(_doc);

    private void CommitEdit()
    {
        if (_pendingSnapshot is null) return;
        Undo.PushSerialized(_pendingSnapshot);
        _pendingSnapshot = null;
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    private void ReplaceDocument(AnnotationDocument doc)
    {
        _doc = doc;
        if (_selected is { } s)
        {
            var found = _doc.Items.FirstOrDefault(i => i.Id == s.Id);
            _selected = found; // null when the item no longer exists
        }
        RecomputeMosaic();
        InvalidateVisual();
        DocumentChanged?.Invoke(this, EventArgs.Empty);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RecomputeMosaic()
    {
        var items = _doc.Items.OfType<MosaicItem>().ToList();
        if (items.Count == 0)
        {
            _mosaicSource = null;
            _mosaicBuffer = null;
            _mosaicPristine = null;
            return;
        }
        _mosaicPristine ??= _baseBuffer.Clone();
        _mosaicBuffer ??= _baseBuffer.Clone();
        Array.Copy(_mosaicPristine.Data, _mosaicBuffer.Data, _mosaicPristine.Data.Length);
        var full = new PixelRect(0, 0, ImageWidth, ImageHeight);
        foreach (var m in items)
        {
            var r = m.Rect.ToPixelRect().Intersect(full);
            if (r.IsEmpty) continue;
            if (m.Mode == MosaicMode.Pixelate) Mosaic.Pixelate(_mosaicBuffer, r, m.Strength);
            else Mosaic.Blur(_mosaicBuffer, r, m.Strength);
        }
        _mosaicSource = _mosaicBuffer.ToBitmapSource();
    }

    /// <summary>
    /// Mosaic preview during item drags: each recompute copies the full image and re-runs the pixel
    /// algorithms, so throttle to ~25fps; OnMouseUp performs the final exact pass.
    /// </summary>
    private void RecomputeMosaicThrottled()
    {
        if ((DateTime.UtcNow - _lastMosaicRecompute).TotalMilliseconds < 40) return;
        _lastMosaicRecompute = DateTime.UtcNow;
        RecomputeMosaic();
    }

    // ---- rendering ----

    protected override void OnRender(DrawingContext dc)
    {
        // Transparency checkerboard (same pattern as the effects preview): the mac-style window
        // shots carry a transparent surround with a baked soft shadow, which read as a flat gray
        // card on the old solid DimGray backdrop. Screen-space tiles keep the squares 16px at
        // every zoom level; drawn before the zoom transform.
        dc.DrawRectangle(AnnotationRenderer.CheckerboardBrush(), null, new Rect(0, 0, Math.Max(Width, 0), Math.Max(Height, 0)));
        dc.PushTransform(new ScaleTransform(Zoom, Zoom));

        var full = new Rect(0, 0, ImageWidth, ImageHeight);
        dc.DrawImage(BaseImage, full);
        AnnotationRenderer.RenderDocument(dc, _doc, BaseImage, _mosaicSource);
        if (_preview is { } pv)
        {
            AnnotationRenderer.RenderItem(dc, pv, BaseImage, _mosaicSource);
            // Mosaic/spotlight drafts paint no visible pixels until commit (mosaic crops the not-
            // yet-perturbed source; spotlight only dims from RenderDocument) — show the drag.
            if (pv is MosaicItem or SpotlightItem && _mode == CanvasMode.Drawing)
                dc.DrawRectangle(null, AccentPen(), AnnotationRenderer.ToRect(pv.GetBounds()));
        }

        if (_doc.Crop is { } applied)
        {
            DimOutside(dc, PixelRectToRect(applied), full);
            dc.DrawRectangle(null, AccentPen(), PixelRectToRect(applied));
        }
        if (_cropDraft is { } draft)
        {
            var r = PixelRectToRect(draft);
            DimOutside(dc, r, full);
            dc.DrawRectangle(null, AccentPen(), r);
        }

        DrawSelectionAdorner(dc);
        dc.Pop();
    }

    private void DrawSelectionAdorner(DrawingContext dc)
    {
        if (_selected is not { } sel || Tool != EditorTool.Select || _mode == CanvasMode.Drawing) return;
        var b = AnnotationRenderer.ToRect(sel.GetBounds());
        double dash = 1.5 / Zoom;
        var style = new DashStyle([4 * dash, 3 * dash], 0);
        // Black underlay + white dash: a bare white dash vanishes on light screenshots.
        dc.DrawRectangle(null, new Pen(Brushes.Black, dash * 2) { DashStyle = style }, b);
        dc.DrawRectangle(null, new Pen(Brushes.White, dash) { DashStyle = style }, b);

        double hs = HandleScreenSize / Zoom;
        var handles = HandlePoints(b);
        if (sel is LineItem)
        {
            // A line resizes only through its endpoints (HitHandle offers no N/S either); the
            // edge-mid handles would advertise drags that do nothing.
            var topMid = new Point(b.Left + b.Width / 2, b.Top);
            var bottomMid = new Point(b.Left + b.Width / 2, b.Bottom);
            handles = handles.Where(p => p != topMid && p != bottomMid).ToArray();
        }
        foreach (var p in handles)
            dc.DrawRectangle(Brushes.White, new Pen(Brushes.Black, dash), new Rect(p.X - hs / 2, p.Y - hs / 2, hs, hs));

        if (sel is LineItem l)
        {
            foreach (var p in new[] { l.Start, l.End })
                dc.DrawEllipse(Brushes.White, new Pen(Brushes.Black, dash), new Point(p.X, p.Y), hs / 2, hs / 2);
        }
    }

    private static void DimOutside(DrawingContext dc, Rect hole, Rect full)
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        void Dim(Rect r)
        {
            var c = Rect.Intersect(r, full);
            if (c.Width > 0.5 && c.Height > 0.5) dc.DrawRectangle(brush, null, c);
        }
        Dim(new Rect(full.X, full.Y, full.Width, hole.Top - full.Y));
        Dim(new Rect(full.X, hole.Bottom, full.Width, full.Bottom - hole.Bottom));
        Dim(new Rect(full.X, hole.Top, hole.Left - full.X, hole.Height));
        Dim(new Rect(hole.Right, hole.Top, full.Right - hole.Right, hole.Height));
    }

    private Pen AccentPen() => new(new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)), 2 / Zoom);

    private static Point[] HandlePoints(Rect r) =>
    [
        new(r.Left, r.Top), new(r.Right, r.Top), new(r.Left, r.Bottom), new(r.Right, r.Bottom),
        new(r.Left + r.Width / 2, r.Top), new(r.Left + r.Width / 2, r.Bottom),
        new(r.Left, r.Top + r.Height / 2), new(r.Right, r.Top + r.Height / 2),
    ];

    // ---- coordinate helpers ----

    private PointD ToImage(Point screen) => new(screen.X / Zoom, screen.Y / Zoom);

    private static Rect PixelRectToRect(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    // ---- hit testing ----

    private AnnotationItem? HitTest(PointD p)
    {
        for (int i = _doc.Items.Count - 1; i >= 0; i--)
        {
            var item = _doc.Items[i];
            if (HitsItem(item, p)) return item;
        }
        return null;
    }

    private bool HitsItem(AnnotationItem item, PointD p)
    {
        double tol = 5 / Zoom;
        switch (item)
        {
            case LineItem l:
                return AnnotationRenderer.DistanceToSegment(p, l.Start, l.End) <= l.StrokeThickness / 2 + tol;
            case PenItem pen:
            {
                var b = pen.GetBounds();
                if (p.X < b.X - tol || p.X > b.Right + tol || p.Y < b.Y - tol || p.Y > b.Bottom + tol) return false;
                for (int i = 0; i < pen.Points.Count - 1; i++)
                    if (AnnotationRenderer.DistanceToSegment(p, pen.Points[i], pen.Points[i + 1]) <= pen.StrokeThickness / 2 + tol)
                        return true;
                return false;
            }
            case MagnifierItem mag:
                return MathHypot(p.X - mag.TargetCenter.X, p.Y - mag.TargetCenter.Y) <= mag.TargetRadius + tol;
            case SpotlightItem spot:
            {
                var b = spot.Rect;
                if (spot.Elliptical)
                {
                    double dx = (p.X - (b.X + b.Width / 2)) / (b.Width / 2 + tol);
                    double dy = (p.Y - (b.Y + b.Height / 2)) / (b.Height / 2 + tol);
                    return dx * dx + dy * dy <= 1;
                }
                return b.X - tol <= p.X && p.X <= b.Right + tol && b.Y - tol <= p.Y && p.Y <= b.Bottom + tol;
            }
            default:
            {
                var b = item.GetBounds();
                return b.X - tol <= p.X && p.X <= b.Right + tol && b.Y - tol <= p.Y && p.Y <= b.Bottom + tol;
            }
        }
    }

    private static double MathHypot(double x, double y) => Math.Sqrt(x * x + y * y);

    private CanvasHandle HitHandle(PointD p)
    {
        if (_selected is null) return CanvasHandle.None;
        var b = _selected.GetBounds();
        double grab = HandleGrab / Zoom;
        bool nearL = Math.Abs(p.X - b.X) <= grab, nearR = Math.Abs(p.X - b.Right) <= grab;
        bool nearT = Math.Abs(p.Y - b.Y) <= grab, nearB = Math.Abs(p.Y - b.Bottom) <= grab;
        bool inX = p.X >= b.X - grab && p.X <= b.Right + grab;
        bool inY = p.Y >= b.Y - grab && p.Y <= b.Bottom + grab;
        // ApplyResize has no N/S arm for lines (only endpoints move); offering those handles would
        // enter a resize that mutates nothing yet still pushes an undo entry.
        bool line = _selected is LineItem;
        if (nearL && nearT) return CanvasHandle.NW;
        if (nearR && nearT) return CanvasHandle.NE;
        if (nearL && nearB) return CanvasHandle.SW;
        if (nearR && nearB) return CanvasHandle.SE;
        if (!line && nearT && inX) return CanvasHandle.N;
        if (!line && nearB && inX) return CanvasHandle.S;
        if (nearL && inY) return CanvasHandle.W;
        if (nearR && inY) return CanvasHandle.E;
        if (inX && inY) return CanvasHandle.Body;
        return CanvasHandle.None;
    }

    private static bool IsCorner(CanvasHandle h) =>
        h is CanvasHandle.NW or CanvasHandle.NE or CanvasHandle.SW or CanvasHandle.SE;

    /// <summary>Whether the item supports bound-resize handles (others are move-only).</summary>
    private static bool IsResizable(AnnotationItem item) =>
        item is BoxItem or PenItem or LineItem;

    // ---- mouse handling ----

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var img = ToImage(e.GetPosition(this));
        if (SpaceHeld) { StartPan(e.GetPosition(this)); return; }

        if (Tool == EditorTool.Select && e.ClickCount >= 2 && HitTest(img) is TextItem t)
        {
            TextEditRequested?.Invoke(this, new TextEditRequestEventArgs(t, t.Position));
            return;
        }
        if (Tool == EditorTool.Crop && e.ClickCount >= 2) { ApplyCrop(); return; }

        switch (Tool)
        {
            case EditorTool.Select: BeginSelect(img); break;
            case EditorTool.Text: TextEditRequested?.Invoke(this, new TextEditRequestEventArgs(null, img)); break;
            case EditorTool.Step: AddStep(img); break;
            case EditorTool.Crop: BeginCrop(img); break;
            case EditorTool.Eraser: BeginErase(img); break;
            default: BeginDraw(img); break;
        }
        if (_mode != CanvasMode.Idle) CaptureMouse();
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle) StartPan(e.GetPosition(this));
    }

    private void StartPan(Point screen)
    {
        if (ScrollOwner is null) return;
        _mode = CanvasMode.Panning;
        _panOrigin = screen;
        _panOffsetX = ScrollOwner.HorizontalOffset;
        _panOffsetY = ScrollOwner.VerticalOffset;
        CaptureMouse();
        Cursor = Cursors.SizeAll;
    }

    private void BeginSelect(PointD img)
    {
        var handle = HitHandle(img);
        if (handle is not CanvasHandle.None and not CanvasHandle.Body && _selected is { } s && IsResizable(s))
        {
            _mode = CanvasMode.Resizing;
            _handle = handle;
            _resizeAnchor = s.GetBounds();
            _startPoint = img;
            BeginEdit();
            return;
        }
        var item = HitTest(img);
        Select(item);
        if (item is not null)
        {
            _mode = CanvasMode.Moving;
            _lastPoint = img;
            BeginEdit();
        }
    }

    private void BeginDraw(PointD img)
    {
        _mode = CanvasMode.Drawing;
        _startPoint = img;
        _lastPoint = img;
        _preview = Tool switch
        {
            EditorTool.Rect => new RectItem { Rect = new RectD(img.X, img.Y, 0, 0), Filled = FillShape, FillColor = FillColorFor(StrokeColor) },
            EditorTool.Ellipse => new EllipseItem { Rect = new RectD(img.X, img.Y, 0, 0), Filled = FillShape, FillColor = FillColorFor(StrokeColor) },
            EditorTool.Line => new LineItem { Start = img, End = img, Dashed = DashedLine },
            EditorTool.Arrow => new ArrowItem { Start = img, End = img, Dashed = DashedLine },
            EditorTool.Pen => new PenItem { Points = [img] },
            EditorTool.Highlighter => new HighlighterItem { Points = [img] },
            EditorTool.Mosaic => new MosaicItem { Mode = MosaicMode.Pixelate, Strength = MosaicStrength, Rect = new RectD(img.X, img.Y, 0, 0) },
            EditorTool.Blur => new MosaicItem { Mode = MosaicMode.Blur, Strength = MosaicStrength, Rect = new RectD(img.X, img.Y, 0, 0) },
            EditorTool.Spotlight => new SpotlightItem { Elliptical = SpotlightElliptical, DimOpacity = SpotlightDim, Rect = new RectD(img.X, img.Y, 0, 0) },
            EditorTool.Magnifier => new MagnifierItem { SourceRect = new RectD(img.X, img.Y, 0, 0), TargetCenter = img, Zoom = MagnifierZoom },
            _ => null,
        };
        if (_preview is not null) ApplyCommonProps(_preview);
        BeginEdit();
    }

    private void ApplyCommonProps(AnnotationItem item)
    {
        item.StrokeColor = StrokeColor;
        if (item is not HighlighterItem) item.Opacity = ItemOpacity;
        // The highlighter honors the thickness preset like the overlay does (its option bar
        // offers the thickness dots); only the general opacity override is skipped.
        if (item is PenItem { } pen) pen.StrokeThickness = StrokeThickness;
        else if (item is not MosaicItem and not SpotlightItem and not MagnifierItem and not TextItem and not StepItem)
            item.StrokeThickness = StrokeThickness;
    }

    /// <summary>Fill color derived from the stroke color (40-alpha variant).</summary>
    public static string FillColorFor(string stroke)
    {
        if (PixelColor.TryParseHex(stroke, out var c))
            return new PixelColor(0x40, c.R, c.G, c.B).ToHex();
        return "#40FF3B30";
    }

    private void AddStep(PointD img)
    {
        BeginEdit();
        var step = new StepItem { Center = img, Radius = StepRadius, StrokeColor = StrokeColor, Number = _doc.NextStepNumber() };
        _doc.Items.Add(step);
        _doc.RenumberSteps();
        CommitEdit();
        Select(step);
    }

    // ---- eraser ----

    /// <summary>Starts an eraser stroke: one drag = one undo record, deletions happen live.</summary>
    private void BeginErase(PointD img)
    {
        _mode = CanvasMode.Drawing;
        _erasedAny = false;
        _pendingSnapshot ??= UndoStack.Serialize(_doc);
        EraseAt(img);
    }

    private void EraseAt(PointD img)
    {
        for (int i = _doc.Items.Count - 1; i >= 0; i--) // topmost first
        {
            var item = _doc.Items[i];
            if (!AnnotationRenderer.HitsForErase(item, img, Math.Max(6, item.StrokeThickness / 2 + 4))) continue;
            _doc.Items.RemoveAt(i);
            if (item is StepItem) _doc.RenumberSteps();
            if (item is MosaicItem) RecomputeMosaic();
            if (ReferenceEquals(_selected, item)) Select(null);
            _erasedAny = true;
            InvalidateVisual();
            break;
        }
    }

    private void FinishErase()
    {
        bool erased = _erasedAny;
        _erasedAny = false; // consumed: must not leak into the next stroke's abort path
        if (erased) CommitEdit();
        else _pendingSnapshot = null; // nothing was hit: no undo entry
    }

    private void BeginCrop(PointD img)
    {
        var handle = HitCropHandle(img);
        BeginEdit();
        if (handle is not CanvasHandle.None and not CanvasHandle.Body && _cropDraft is { } draft)
        {
            _mode = CanvasMode.CropResize;
            _handle = handle;
            _cropAnchor = draft;
            _startPoint = img;
        }
        else
        {
            _mode = CanvasMode.CropDraft;
            _startPoint = img;
            _cropDraft = new PixelRect((int)img.X, (int)img.Y, 0, 0);
        }
    }

    private CanvasHandle HitCropHandle(PointD p)
    {
        if (_cropDraft is not { } r) return CanvasHandle.None;
        double grab = HandleGrab / Zoom;
        bool nearL = Math.Abs(p.X - r.X) <= grab, nearR = Math.Abs(p.X - r.Right) <= grab;
        bool nearT = Math.Abs(p.Y - r.Y) <= grab, nearB = Math.Abs(p.Y - r.Bottom) <= grab;
        bool inX = p.X >= r.X - grab && p.X <= r.Right + grab;
        bool inY = p.Y >= r.Y - grab && p.Y <= r.Bottom + grab;
        if (nearL && nearT) return CanvasHandle.NW;
        if (nearR && nearT) return CanvasHandle.NE;
        if (nearL && nearB) return CanvasHandle.SW;
        if (nearR && nearB) return CanvasHandle.SE;
        if (nearT && inX) return CanvasHandle.N;
        if (nearB && inX) return CanvasHandle.S;
        if (nearL && inY) return CanvasHandle.W;
        if (nearR && inY) return CanvasHandle.E;
        if (inX && inY) return CanvasHandle.Body;
        return CanvasHandle.None;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var screen = e.GetPosition(this);
        var img = ToImage(screen);
        switch (_mode)
        {
            case CanvasMode.Panning:
                if (ScrollOwner is not null)
                {
                    ScrollOwner.ScrollToHorizontalOffset(_panOffsetX - (screen.X - _panOrigin.X));
                    ScrollOwner.ScrollToVerticalOffset(_panOffsetY - (screen.Y - _panOrigin.Y));
                }
                return;
            case CanvasMode.Drawing when Tool == EditorTool.Eraser:
                EraseAt(img);
                break;
            case CanvasMode.Drawing:
                UpdatePreview(img);
                break;
            case CanvasMode.Moving:
                if (_selected is { } m)
                {
                    m.Move(img.X - _lastPoint.X, img.Y - _lastPoint.Y);
                    _lastPoint = img;
                    if (m is MosaicItem) RecomputeMosaicThrottled();
                }
                break;
            case CanvasMode.Resizing:
                ApplyResize(img);
                break;
            case CanvasMode.CropDraft:
                _cropDraft = RectToPixelRect(RectD.FromPoints(_startPoint, img));
                break;
            case CanvasMode.CropResize:
                _cropDraft = ResizePixelRect(_cropAnchor, _handle, _startPoint, img);
                break;
            case CanvasMode.Idle:
                UpdateCursor(img);
                break;
        }
        InvalidateVisual();
    }

    private void UpdatePreview(PointD img)
    {
        switch (_preview)
        {
            case BoxItem b:
                b.Rect = RectD.FromPoints(_startPoint, img);
                break;
            case LineItem l:
                l.End = ShiftFor45(l.Start, img);
                break;
            case PenItem pen:
            {
                var last = pen.Points[^1];
                if (MathHypot(img.X - last.X, img.Y - last.Y) >= 1 / Zoom) pen.Points.Add(img);
                break;
            }
            case MagnifierItem mag:
                mag.SourceRect = RectD.FromPoints(_startPoint, img);
                mag.TargetCenter = new PointD(_startPoint.X, _startPoint.Y);
                break;
        }
    }

    /// <summary>Shift constrains lines to 45° steps.</summary>
    private PointD ShiftFor45(PointD start, PointD end)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return end;
        double dx = end.X - start.X, dy = end.Y - start.Y;
        double angle = Math.Atan2(dy, dx);
        double snap = Math.Round(angle / (Math.PI / 4)) * (Math.PI / 4);
        double len = MathHypot(dx, dy);
        return new PointD(start.X + Math.Cos(snap) * len, start.Y + Math.Sin(snap) * len);
    }

    private void ApplyResize(PointD img)
    {
        if (_selected is null) return;
        var d = new PointD(img.X - _startPoint.X, img.Y - _startPoint.Y);
        switch (_selected)
        {
            case BoxItem b:
                b.Rect = ResizeRectD(_resizeAnchor, _handle, d);
                break;
            case PenItem pen:
            {
                double oldW = Math.Max(1, _resizeAnchor.Width), oldH = Math.Max(1, _resizeAnchor.Height);
                var (nx, ny, nw, nh) = NewEdges(_resizeAnchor, _handle, d);
                double sx = nw / oldW, sy = nh / oldH;
                for (int i = 0; i < pen.Points.Count; i++)
                    pen.Points[i] = new PointD(nx + (pen.Points[i].X - _resizeAnchor.X) * sx, ny + (pen.Points[i].Y - _resizeAnchor.Y) * sy);
                break;
            }
            case LineItem l when _handle is CanvasHandle.NW or CanvasHandle.W or CanvasHandle.SW:
                l.Start = new PointD(_resizeAnchor.X + d.X, _resizeAnchor.Y + d.Y);
                break;
            case LineItem l when _handle is CanvasHandle.NE or CanvasHandle.E or CanvasHandle.SE:
                l.End = new PointD(_resizeAnchor.Right + d.X, _resizeAnchor.Bottom + d.Y);
                break;
        }
        if (_selected is MosaicItem) RecomputeMosaicThrottled();
    }

    private static (double X, double Y, double W, double H) NewEdges(RectD a, CanvasHandle h, PointD d)
    {
        const double min = 2;
        double l = a.X, t = a.Y, r = a.Right, b = a.Bottom;
        if (h is CanvasHandle.W or CanvasHandle.NW or CanvasHandle.SW) l = Math.Min(a.X + d.X, r - min);
        if (h is CanvasHandle.E or CanvasHandle.NE or CanvasHandle.SE) r = Math.Max(a.Right + d.X, l + min);
        if (h is CanvasHandle.N or CanvasHandle.NW or CanvasHandle.NE) t = Math.Min(a.Y + d.Y, b - min);
        if (h is CanvasHandle.S or CanvasHandle.SW or CanvasHandle.SE) b = Math.Max(a.Bottom + d.Y, t + min);
        return (l, t, r - l, b - t);
    }

    private static RectD ResizeRectD(RectD a, CanvasHandle h, PointD d)
    {
        var (x, y, w, hh) = NewEdges(a, h, d);
        return new RectD(x, y, w, hh);
    }

    private static PixelRect ResizePixelRect(PixelRect a, CanvasHandle h, PointD pressPoint, PointD cursor)
    {
        var (x, y, w, hh) = NewEdges(
            new RectD(a.X, a.Y, a.Width, a.Height), h,
            new PointD(cursor.X - pressPoint.X, cursor.Y - pressPoint.Y));
        return new PixelRect((int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(w), (int)Math.Round(hh));
    }

    private static PixelRect RectToPixelRect(RectD r) => new(
        (int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Middle && _mode == CanvasMode.Panning)
        {
            _mode = CanvasMode.Idle;
            UpdateCursor(ToImage(e.GetPosition(this)));
            ReleaseMouseCapture();
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        switch (_mode)
        {
            case CanvasMode.Drawing when Tool == EditorTool.Eraser:
                FinishErase();
                break;
            case CanvasMode.Drawing:
                CommitPreview();
                break;
            case CanvasMode.Moving or CanvasMode.Resizing:
                if (_selected is MosaicItem) { _lastMosaicRecompute = DateTime.MinValue; RecomputeMosaic(); }
                CommitEdit();
                break;
            case CanvasMode.CropDraft or CanvasMode.CropResize:
                if (_cropDraft is { } d && (d.Width < 4 || d.Height < 4))
                {
                    _cropDraft = null;
                    _pendingSnapshot = null; // no change happened
                }
                break;
        }
        if (_mode != CanvasMode.Idle)
        {
            _mode = CanvasMode.Idle;
            ReleaseMouseCapture();
        }
        InvalidateVisual();
    }

    private void CommitPreview()
    {
        if (_preview is null)
        {
            // Only reachable when the tool was switched mid-stroke. An eraser stroke has already
            // deleted items — keep the deletion undoable; a draw stroke committed nothing — discard.
            if (_erasedAny) CommitEdit();
            else _pendingSnapshot = null;
            _erasedAny = false;
            return;
        }
        var item = _preview;
        bool valid = item switch
        {
            PenItem pen => pen.Points.Count >= 2,
            LineItem l => MathHypot(l.End.X - l.Start.X, l.End.Y - l.Start.Y) >= 2 / Zoom,
            BoxItem b => b.Rect.Width >= 2 / Zoom && b.Rect.Height >= 2 / Zoom,
            // Match DrawMagnifier's render floor (source ≥4px, radius ≥4): anything smaller passes
            // validity, commits, then renders nothing yet stays hit-testable as a ghost selection.
            MagnifierItem m => m.SourceRect.Width >= 4 / Zoom && m.SourceRect.Height >= 4 / Zoom && m.TargetRadius >= 4,
            _ => false,
        };
        if (valid)
        {
            if (item is MagnifierItem mag) PlaceMagnifierTarget(mag);
            if (item is TextItem t) MeasureTextItem(t);
            _doc.Items.Add(item);
            if (item is StepItem) _doc.RenumberSteps();
            if (item is MosaicItem) RecomputeMosaic();
            CommitEdit();
        }
        else
        {
            _pendingSnapshot = null;
        }
        _preview = null;
    }

    /// <summary>Places the magnifier callout circle next to the source rect, clamped into the image.</summary>
    private void PlaceMagnifierTarget(MagnifierItem mag)
    {
        double r = mag.TargetRadius;
        double x = Math.Clamp(mag.SourceRect.Right + r + 12, r, Math.Max(r, ImageWidth - r));
        double y = Math.Clamp(mag.SourceRect.Y, r, Math.Max(r, ImageHeight - r));
        mag.TargetCenter = new PointD(x, y);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            ZoomRequested?.Invoke(this, new ZoomRequestEventArgs(e.Delta, ToImage(e.GetPosition(this))));
            e.Handled = true;
        }
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.LeftShift || e.Key == Key.RightShift) InvalidateVisual();
        base.OnKeyUp(e);
    }

    private void UpdateCursor(PointD img)
    {
        if (SpaceHeld) { Cursor = Cursors.SizeAll; return; }
        switch (Tool)
        {
            case EditorTool.Select:
                var h = HitHandle(img);
                Cursor = h switch
                {
                    CanvasHandle.N or CanvasHandle.S => Cursors.SizeNS,
                    CanvasHandle.E or CanvasHandle.W => Cursors.SizeWE,
                    CanvasHandle.NW or CanvasHandle.SE => Cursors.SizeNWSE,
                    CanvasHandle.NE or CanvasHandle.SW => Cursors.SizeNESW,
                    CanvasHandle.Body when _selected is not null => Cursors.SizeAll,
                    _ => Cursors.Arrow,
                };
                break;
            case EditorTool.Crop:
                var ch = HitCropHandle(img);
                Cursor = ch switch
                {
                    CanvasHandle.N or CanvasHandle.S => Cursors.SizeNS,
                    CanvasHandle.E or CanvasHandle.W => Cursors.SizeWE,
                    CanvasHandle.NW or CanvasHandle.SE => Cursors.SizeNWSE,
                    CanvasHandle.NE or CanvasHandle.SW => Cursors.SizeNESW,
                    CanvasHandle.Body => Cursors.SizeAll,
                    _ => Cursors.Cross,
                };
                break;
            case EditorTool.Text:
                Cursor = Cursors.IBeam;
                break;
            default:
                Cursor = Cursors.Cross;
                break;
        }
    }
}
