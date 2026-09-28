using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Editor;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Settings;
using CaptureMode = ModernScreenShot.Core.Settings.CaptureMode;

namespace ModernScreenShot.App.Overlay;

/// <summary>What the user picked in the overlay toolbar.</summary>
public enum OverlayIntent { Edit, Copy, Save, Pin }

public sealed class OverlayOutcome
{
    public required bool Confirmed { get; init; }
    /// <summary>Selected region (or picked window bounds) in virtual-screen physical pixels.</summary>
    public PixelRect Region { get; init; }
    /// <summary>Set when a window was confirmed in WindowPick mode (captured via PrintWindow).</summary>
    public IntPtr WindowHandle { get; init; }
    public WindowInfo? PickedWindow { get; init; }
    public OverlayIntent Intent { get; init; } = OverlayIntent.Edit;
    /// <summary>Annotations drawn inline in the overlay; image pixels relative to Region. Null when none were drawn.</summary>
    public AnnotationDocument? AnnotationDocument { get; init; }
}

internal enum OverlayState { Idle, Dragging, Selected }
internal enum DragAction { None, NewRegion, Move, Resize }
internal enum HandleDir { None, Body, N, S, E, W, NW, NE, SW, SE }

internal readonly record struct VPoint(int X, int Y)
{
    public static VPoint operator +(VPoint a, VPoint b) => new(a.X + b.X, a.Y + b.Y);
    public static VPoint operator -(VPoint a, VPoint b) => new(a.X - b.X, a.Y - b.Y);
}

/// <summary>
/// Shared controller for one overlay session spanning all monitor windows. Every coordinate is a
/// virtual-screen physical pixel. The caller must freeze the screen and refresh the window
/// enumerator snapshot BEFORE calling <see cref="Show"/>, so hover/snapping can never see the overlay.
/// </summary>
internal sealed class OverlaySession
{
    private const int ClickThreshold = 4;
    private const int MinSelection = 4;
    private const int HandleGrab = 7;

    private readonly List<OverlayWindow> _windows = [];
    private readonly MonitorService _monitors;
    private readonly WindowEnumerator _windowEnum;
    private readonly EditorSettings _editor;
    private Action? _finished;

    // ---- inline annotation state (Snipaste-style drawing directly on the frozen frame) ----
    public AnnotationDocument Doc { get; } = new();
    public UndoStack Undo { get; } = new();
    private EditorTool? _tool;                       // active annotation tool; null = adjust selection
    private AnnotationItem? _drawPreview;            // in-flight stroke, rendered but not committed
    private VPoint _drawStartVirtual;
    private PointD _drawStartImage;
    private string? _pendingSnapshot;                // pre-stroke doc state, pushed on commit
    private PixelBuffer? _cropPristine;              // selection crop without mosaic
    private PixelRect _cropCacheRect;
    private PixelBuffer? _mosaicScratch;
    private BitmapSource? _mosaicSource;
    private DateTime _lastMosaicRecompute = DateTime.MinValue;
    /// <summary>Raised when annotation/undo state changed (toolbar highlight, undo/redo enablement).</summary>
    public event EventHandler? AnnotationChanged;
    /// <summary>Text tool pressed; the window shows a text box at this image-space position.</summary>
    public event EventHandler<TextEditRequestEventArgs>? TextEditRequested;

    private OverlayState _state = OverlayState.Idle;
    private DragAction _drag = DragAction.None;
    private HandleDir _handle = HandleDir.None;
    private PixelRect _selection;
    private PixelRect _dragAnchor;
    private VPoint _pressPoint;
    private VPoint _grabOffset;
    private WindowInfo? _hover;
    private VPoint _cursor;
    private bool _cursorValid;
    private OverlayOutcome? _outcome;
    private bool _ended;
    private readonly bool _autoConfirm;
    private DateTime _colorFlashUntil = DateTime.MinValue;

    internal PixelBuffer Frozen { get; }
    internal PixelRect Virtual { get; }
    internal BitmapSource FrozenSource { get; }
    internal CaptureMode Mode { get; }
    internal bool MagnifierEnabled { get; }
    /// <summary>When true a finished selection immediately confirms as Edit (scrolling capture); the toolbar is hidden.</summary>
    internal bool AutoConfirmOnSelect => _autoConfirm;

    internal OverlayState State => _state;
    internal PixelRect? Selection => _selection.IsEmpty ? null : _selection;
    internal WindowInfo? Hover => _hover;
    internal VPoint Cursor => _cursor;
    internal bool CursorValid => _cursorValid;
    internal bool ColorFlashActive => DateTime.UtcNow < _colorFlashUntil;

    /// <summary>Active inline-annotation tool; null means the selection itself is being adjusted.</summary>
    internal EditorTool? Tool
    {
        get => _tool;
        set
        {
            if (_autoConfirm || value == _tool || _drawPreview is not null) return;
            _tool = value is EditorTool.Select ? null : value;
            AnnotationChanged?.Invoke(this, EventArgs.Empty);
            InvalidateAll();
        }
    }
    internal BitmapSource? MosaicSource => _mosaicSource;
    internal AnnotationItem? PreviewItem => _drawPreview;
    internal double EditorFontSize => _editor.FontSize;
    internal string EditorFontFamily => _editor.FontFamily;
    /// <summary>Bold text is not configurable yet; the editor starts unbolded as well.</summary>
    internal bool EditorFontBold => false;
    internal string EditorStrokeColor => _editor.StrokeColor;
    /// <summary>Inline annotation is available: region mode, a selection exists, toolbar visible.</summary>
    internal bool CanAnnotate => !_autoConfirm && Mode == CaptureMode.Region
        && _state == OverlayState.Selected && !_selection.IsEmpty;

    public OverlaySession(CaptureMode mode, PixelBuffer frozen, PixelRect virtualScreen, BitmapSource frozenSource,
        MonitorService monitors, WindowEnumerator windowEnum, bool magnifierEnabled, EditorSettings editor,
        bool autoConfirmOnSelect = false)
    {
        Mode = mode is CaptureMode.Region or CaptureMode.WindowPick ? mode : CaptureMode.Region;
        Frozen = frozen;
        Virtual = virtualScreen;
        FrozenSource = frozenSource;
        _monitors = monitors;
        _windowEnum = windowEnum;
        _editor = editor;
        MagnifierEnabled = magnifierEnabled;
        _autoConfirm = autoConfirmOnSelect;
    }

    /// <summary>Shows one overlay per monitor and pumps the dispatcher until the session ends.</summary>
    public OverlayOutcome? Show()
    {
        if (NativeMethods.GetCursorPos(out var cp))
        {
            _cursor = new VPoint(cp.X, cp.Y);
            _cursorValid = true;
        }
        _hover = _cursorValid ? _windowEnum.HitTest(_cursor.X, _cursor.Y, includeChildren: true) : null;

        var monitors = _monitors.GetMonitors();
        if (monitors.Count == 0) return null;
        var active = new bool[monitors.Count];
        bool anyCursorMonitor = false;
        for (int i = 0; i < monitors.Count; i++)
        {
            active[i] = _cursorValid && monitors[i].Bounds.Contains(_cursor.X, _cursor.Y);
            anyCursorMonitor |= active[i];
        }
        if (!anyCursorMonitor) active[0] = true; // keyboard focus must land somewhere

        for (int i = 0; i < monitors.Count; i++)
            _windows.Add(new OverlayWindow(this, monitors[i], FrozenSource, active[i]));

        // The finished-callback must exist before the first window is shown: a session-ending event
        // (e.g. an external WM_CLOSE) can fire between Show() and PushFrame, and would otherwise
        // leave the nested pump below running forever.
        var frame = new DispatcherFrame();
        _finished = () => frame.Continue = false;
        try
        {
            foreach (var w in _windows) w.Show();
        }
        catch (Exception ex)
        {
            Log.Error("Showing the overlay windows failed", ex);
            EndSession(null);
            return null;
        }
        Dispatcher.PushFrame(frame);
        return _outcome;
    }

    // ---- input entry points (called by OverlayWindow) ----

    internal void OnMove(OverlayWindow w, VPoint p)
    {
        _cursor = p;
        _cursorValid = true;
        switch (_state)
        {
            case OverlayState.Idle:
                _hover = _windowEnum.HitTest(p.X, p.Y, includeChildren: true);
                break;
            case OverlayState.Dragging:
                UpdateDrag(p);
                break;
            case OverlayState.Selected when _drawPreview is not null:
                UpdateDraw(p);
                break;
            case OverlayState.Selected when _drag is DragAction.Move or DragAction.Resize:
                // Handle/body presses from OnLeftDown set _drag while staying in Selected.
                UpdateDrag(p);
                break;
        }
        InvalidateAll();
    }

    internal void OnLeftDown(OverlayWindow w, VPoint p)
    {
        _cursor = p;
        _cursorValid = true;
        // An active annotation tool swallows presses inside the selection; presses outside still
        // fall through to the selection logic below so the region can be redrawn/adjusted.
        if (_state == OverlayState.Selected && Tool is { } tool
            && Mode == CaptureMode.Region && _selection.Contains(p.X, p.Y))
        {
            BeginDraw(p);
            InvalidateAll();
            return;
        }
        if (_state == OverlayState.Idle)
        {
            if (Mode == CaptureMode.Region) StartNewRegion(p);
        }
        else if (_state == OverlayState.Selected)
        {
            var h = ComputeHandle(_selection, p);
            switch (h)
            {
                case HandleDir.N or HandleDir.S or HandleDir.E or HandleDir.W
                     or HandleDir.NW or HandleDir.NE or HandleDir.SW or HandleDir.SE:
                    _drag = DragAction.Resize;
                    _handle = h;
                    _dragAnchor = _selection;
                    _pressPoint = p;
                    break;
                case HandleDir.Body:
                    _drag = DragAction.Move;
                    _dragAnchor = _selection;
                    _pressPoint = p;
                    _grabOffset = new VPoint(p.X - _selection.X, p.Y - _selection.Y);
                    break;
                default:
                    if (Mode == CaptureMode.Region) StartNewRegion(p);
                    break;
            }
        }
        InvalidateAll();
    }

    internal void OnLeftUp(OverlayWindow w, VPoint p)
    {
        _cursor = p;
        _cursorValid = true;
        // Releasing an annotation stroke never confirms/cancels the session.
        if (_drawPreview is not null)
        {
            CommitDraw();
            InvalidateAll();
            return;
        }
        var drag = _drag;
        _drag = DragAction.None;

        if (_state == OverlayState.Dragging && drag == DragAction.NewRegion)
        {
            _selection = RectBetween(_pressPoint, p);
            bool isClick = _selection.Width < ClickThreshold || _selection.Height < ClickThreshold;
            if (isClick)
            {
                _selection = default;
                if (Mode == CaptureMode.Region) SelectWindowUnderCursor();
                _state = _selection.IsEmpty ? OverlayState.Idle : OverlayState.Selected;
                if (_autoConfirm && !_selection.IsEmpty)
                {
                    Confirm(OverlayIntent.Edit);
                    return;
                }
            }
            else
            {
                _selection = ClampToVirtual(_selection);
                _state = OverlayState.Selected;
                if (_autoConfirm)
                {
                    Confirm(OverlayIntent.Edit);
                    return;
                }
            }
        }
        else if (_state == OverlayState.Idle && Mode == CaptureMode.WindowPick)
        {
            ConfirmWindowPick();
        }
        InvalidateAll();
    }

    internal void OnDoubleClick(OverlayWindow w, VPoint p)
    {
        if (Mode == CaptureMode.Region && _state == OverlayState.Selected && !_selection.IsEmpty)
            Confirm(OverlayIntent.Edit);
    }

    /// <summary>Handles a key press; returns true when the key was consumed.</summary>
    internal bool OnKey(Key key, bool shift)
    {
        switch (key)
        {
            case Key.Escape:
                Cancel();
                return true;
            case Key.Enter:
                if (_state == OverlayState.Selected && !_selection.IsEmpty && Mode == CaptureMode.Region)
                    Confirm(OverlayIntent.Edit);
                return true;
            case Key.C when MagnifierEnabled && _cursorValid && Keyboard.Modifiers == ModifierKeys.None:
                CopyCursorColor();
                InvalidateAll();
                return true;
            case Key.V or Key.R or Key.E or Key.L or Key.A or Key.P or Key.H or Key.T or Key.N or Key.M
                when CanAnnotate && Keyboard.Modifiers == ModifierKeys.None:
                Tool = KeyToTool(key);
                return true;
            case Key.Left when _state == OverlayState.Selected:
                Nudge(shift ? -10 : -1, 0);
                return true;
            case Key.Right when _state == OverlayState.Selected:
                Nudge(shift ? 10 : 1, 0);
                return true;
            case Key.Up when _state == OverlayState.Selected:
                Nudge(0, shift ? -10 : -1);
                return true;
            case Key.Down when _state == OverlayState.Selected:
                Nudge(0, shift ? 10 : 1);
                return true;
            default:
                return false;
        }
    }

    internal void Confirm(OverlayIntent intent)
    {
        if (_selection.IsEmpty) return;
        Doc.ImageWidth = _selection.Width;
        Doc.ImageHeight = _selection.Height;
        EndSession(new OverlayOutcome
        {
            Confirmed = true,
            Region = _selection,
            Intent = intent,
            AnnotationDocument = Doc.Items.Count > 0 ? Doc : null,
        });
    }

    internal void ConfirmWindowPick()
    {
        var wi = _hover ?? (_cursorValid ? _windowEnum.HitTest(_cursor.X, _cursor.Y, includeChildren: true) : null);
        if (wi is null) return;
        _hover = wi;
        EndSession(new OverlayOutcome
        {
            Confirmed = true,
            Region = wi.Bounds,
            WindowHandle = wi.Handle,
            PickedWindow = wi,
            Intent = OverlayIntent.Edit,
        });
    }

    internal void Cancel() => EndSession(null);

    internal void OnWindowClosed(OverlayWindow w)
    {
        // Alt+F4 or any external close means the user is done with the overlay.
        if (!_ended) Cancel();
    }

    internal HandleDir CurrentHandle =>
        _state == OverlayState.Selected && !_selection.IsEmpty && _cursorValid
            ? ComputeHandle(_selection, _cursor)
            : HandleDir.None;

    internal static HandleDir ComputeHandle(PixelRect sel, VPoint p)
    {
        if (sel.IsEmpty) return HandleDir.None;
        bool nearL = Math.Abs(p.X - sel.X) <= HandleGrab;
        bool nearR = Math.Abs(p.X - sel.Right) <= HandleGrab;
        bool nearT = Math.Abs(p.Y - sel.Y) <= HandleGrab;
        bool nearB = Math.Abs(p.Y - sel.Bottom) <= HandleGrab;
        bool inX = p.X >= sel.X - HandleGrab && p.X <= sel.Right + HandleGrab;
        bool inY = p.Y >= sel.Y - HandleGrab && p.Y <= sel.Bottom + HandleGrab;
        if (nearL && nearT) return HandleDir.NW;
        if (nearR && nearT) return HandleDir.NE;
        if (nearL && nearB) return HandleDir.SW;
        if (nearR && nearB) return HandleDir.SE;
        if (nearT && inX) return HandleDir.N;
        if (nearB && inX) return HandleDir.S;
        if (nearL && inY) return HandleDir.W;
        if (nearR && inY) return HandleDir.E;
        if (sel.Contains(p.X, p.Y)) return HandleDir.Body;
        return HandleDir.None;
    }

    internal PixelColor? SampleColor(VPoint p)
    {
        int x = p.X - Virtual.X, y = p.Y - Virtual.Y;
        if (x < 0 || y < 0 || x >= Frozen.Width || y >= Frozen.Height) return null;
        return Frozen.GetPixel(x, y);
    }

    internal void InvalidateAll()
    {
        foreach (var w in _windows) w.OnSessionChanged();
    }

    private void StartNewRegion(VPoint p)
    {
        if (Doc.Items.Count > 0) ClearAnnotations(); // redrawing the region restarts annotation too (undoable)
        _state = OverlayState.Dragging;
        _drag = DragAction.NewRegion;
        _pressPoint = p;
        _selection = default;
    }

    private void UpdateDrag(VPoint p)
    {
        switch (_drag)
        {
            case DragAction.NewRegion:
                _selection = RectBetween(_pressPoint, p);
                break;
            case DragAction.Move:
            {
                int nx = Math.Clamp(p.X - _grabOffset.X, Virtual.X, Math.Max(Virtual.X, Virtual.Right - _dragAnchor.Width));
                int ny = Math.Clamp(p.Y - _grabOffset.Y, Virtual.Y, Math.Max(Virtual.Y, Virtual.Bottom - _dragAnchor.Height));
                MoveSelectionTo(nx, ny);
                break;
            }
            case DragAction.Resize:
                var resized = Resize(_dragAnchor, _handle, p.X - _pressPoint.X, p.Y - _pressPoint.Y);
                MoveSelectionTo(resized.X, resized.Y, resized.Width, resized.Height);
                break;
        }
    }

    /// <summary>Assigns the selection and keeps inline annotations glued to the screen pixels they
    /// mark by shifting them with the selection's top-left corner.</summary>
    private void MoveSelectionTo(int x, int y, int? width = null, int? height = null)
    {
        int dx = x - _selection.X, dy = y - _selection.Y;
        _selection = width is { } w && height is { } h ? new PixelRect(x, y, w, h) : new PixelRect(x, y, _selection.Width, _selection.Height);
        if ((dx != 0 || dy != 0) && Doc.Items.Count > 0)
        {
            foreach (var item in Doc.Items) item.Move(-dx, -dy);
            if (_mosaicSource is not null || Doc.Items.OfType<MosaicItem>().Any()) RecomputeMosaic();
        }
    }

    private void SelectWindowUnderCursor()
    {
        var wi = _windowEnum.HitTest(_cursor.X, _cursor.Y, includeChildren: true);
        _hover = wi;
        if (wi is null) return;
        _selection = ClampToVirtual(wi.Bounds);
    }

    // ---- inline annotation (Snipaste-style drawing on the frozen frame) ----

    private static EditorTool? KeyToTool(Key key) => key switch
    {
        Key.V => EditorTool.Select,
        Key.R => EditorTool.Rect,
        Key.E => EditorTool.Ellipse,
        Key.L => EditorTool.Line,
        Key.A => EditorTool.Arrow,
        Key.P => EditorTool.Pen,
        Key.H => EditorTool.Highlighter,
        Key.T => EditorTool.Text,
        Key.N => EditorTool.Step,
        Key.M => EditorTool.Mosaic,
        _ => null,
    };

    private PointD ToImage(VPoint p) => new(p.X - _selection.X, p.Y - _selection.Y);

    private static double Dist(PointD a, PointD b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private void BeginDraw(VPoint p)
    {
        var img = ToImage(p);
        _drawStartVirtual = p;
        _drawStartImage = img;
        switch (Tool)
        {
            case EditorTool.Text:
                TextEditRequested?.Invoke(this, new TextEditRequestEventArgs(null, img));
                return;
            case EditorTool.Step:
            {
                _pendingSnapshot ??= UndoStack.Serialize(Doc);
                var step = new StepItem { Center = img, Radius = 16, StrokeColor = _editor.StrokeColor, Number = Doc.NextStepNumber() };
                Doc.Items.Add(step);
                Doc.RenumberSteps();
                FinishSnapshot();
                return;
            }
        }
        _pendingSnapshot ??= UndoStack.Serialize(Doc);
        _drawPreview = Tool switch
        {
            EditorTool.Rect => new RectItem { Rect = new RectD(img.X, img.Y, 0, 0) },
            EditorTool.Ellipse => new EllipseItem { Rect = new RectD(img.X, img.Y, 0, 0) },
            EditorTool.Line => new LineItem { Start = img, End = img },
            EditorTool.Arrow => new ArrowItem { Start = img, End = img },
            EditorTool.Pen => new PenItem { Points = [img] },
            EditorTool.Highlighter => new HighlighterItem { Points = [img] },
            EditorTool.Mosaic => new MosaicItem { Mode = MosaicMode.Pixelate, Strength = _editor.MosaicCellSize, Rect = new RectD(img.X, img.Y, 0, 0) },
            _ => null,
        };
        if (_drawPreview is not null) ApplyCommonProps(_drawPreview);
    }

    private void UpdateDraw(VPoint p)
    {
        var img = ToImage(p);
        switch (_drawPreview)
        {
            case RectItem b:
                b.Rect = RectD.FromPoints(_drawStartImage, img);
                break;
            case EllipseItem e:
                e.Rect = RectD.FromPoints(_drawStartImage, img);
                break;
            case MosaicItem m:
                m.Rect = RectD.FromPoints(_drawStartImage, img);
                RecomputeMosaicThrottled();
                break;
            case ArrowItem a: // ArrowItem : LineItem — must precede LineItem
                a.End = img;
                break;
            case LineItem l:
                l.End = img;
                break;
            case HighlighterItem h: // HighlighterItem : PenItem — must precede PenItem
            {
                var last = h.Points[^1];
                if (Dist(img, last) >= 1) h.Points.Add(img);
                break;
            }
            case PenItem pen:
            {
                var last = pen.Points[^1];
                if (Dist(img, last) >= 1) pen.Points.Add(img);
                break;
            }
        }
    }

    private void CommitDraw()
    {
        var item = _drawPreview;
        _drawPreview = null;
        if (item is null) return;
        bool valid = item switch
        {
            HighlighterItem h => h.Points.Count >= 2, // derived types first: HighlighterItem : PenItem
            PenItem pen => pen.Points.Count >= 2,
            ArrowItem a => Dist(a.Start, a.End) >= 2, // ArrowItem : LineItem
            LineItem l => Dist(l.Start, l.End) >= 2,
            BoxItem b => b.Rect.Width >= 2 && b.Rect.Height >= 2,
            _ => false,
        };
        if (valid)
        {
            Doc.Items.Add(item);
            if (item is MosaicItem) RecomputeMosaic();
            FinishSnapshot();
        }
        else if (item is MosaicItem)
        {
            RecomputeMosaic(); // drop the preview's mosaic contribution
            _pendingSnapshot = null;
        }
        else
        {
            _pendingSnapshot = null; // discarded dot/zero-size stroke: no undo entry
        }
    }

    /// <summary>Commits text typed in the overlay's text box (image-space position from the Text tool).</summary>
    internal void CommitText(PointD position, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        _pendingSnapshot ??= UndoStack.Serialize(Doc);
        var item = new TextItem
        {
            Position = position,
            Text = text,
            FontSize = _editor.FontSize,
            Bold = false,
            StrokeColor = _editor.StrokeColor,
        };
        AnnotationCanvas.MeasureTextItem(item);
        Doc.Items.Add(item);
        FinishSnapshot();
    }

    internal void DoUndo()
    {
        if (_drawPreview is not null) return;
        var d = Undo.Undo(Doc);
        if (d is not null) ReplaceDoc(d);
    }

    internal void DoRedo()
    {
        if (_drawPreview is not null) return;
        var d = Undo.Redo(Doc);
        if (d is not null) ReplaceDoc(d);
    }

    private void ReplaceDoc(AnnotationDocument d)
    {
        Doc.Items = d.Items;
        Doc.Crop = d.Crop;
        RecomputeMosaic();
        AnnotationChanged?.Invoke(this, EventArgs.Empty);
        InvalidateAll();
    }

    private void ClearAnnotations()
    {
        _pendingSnapshot ??= UndoStack.Serialize(Doc);
        Doc.Items.Clear();
        RecomputeMosaic();
        FinishSnapshot();
    }

    private void FinishSnapshot()
    {
        if (_pendingSnapshot is null) return;
        Undo.PushSerialized(_pendingSnapshot);
        _pendingSnapshot = null;
        AnnotationChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyCommonProps(AnnotationItem item)
    {
        item.StrokeColor = _editor.StrokeColor;
        if (item is HighlighterItem) item.Opacity = 0.45;
        else if (item is not MosaicItem and not TextItem and not StepItem)
            item.StrokeThickness = _editor.StrokeThickness;
    }

    /// <summary>Pixels the committed (and in-flight) mosaic rects into a cached copy of the selection crop.</summary>
    private void RecomputeMosaic()
    {
        var items = Doc.Items.OfType<MosaicItem>().ToList();
        if (_drawPreview is MosaicItem preview) items.Add(preview);
        if (items.Count == 0 || _selection.IsEmpty)
        {
            _mosaicSource = null;
            _mosaicScratch = null;
            _cropPristine = null;
            _cropCacheRect = default;
            return;
        }
        if (_cropPristine is null || _cropCacheRect != _selection)
        {
            _cropPristine = Frozen.Crop(new PixelRect(
                _selection.X - Virtual.X, _selection.Y - Virtual.Y, _selection.Width, _selection.Height));
            _cropCacheRect = _selection;
            _mosaicScratch = null;
        }
        _mosaicScratch ??= _cropPristine.Clone();
        Array.Copy(_cropPristine.Data, _mosaicScratch.Data, _cropPristine.Data.Length);
        var full = new PixelRect(0, 0, _selection.Width, _selection.Height);
        foreach (var m in items)
        {
            var r = m.Rect.ToPixelRect().Intersect(full);
            if (r.IsEmpty) continue;
            if (m.Mode == MosaicMode.Pixelate) Mosaic.Pixelate(_mosaicScratch, r, m.Strength);
            else Mosaic.Blur(_mosaicScratch, r, m.Strength);
        }
        _mosaicSource = _mosaicScratch.ToBitmapSource();
    }

    /// <summary>Mosaic preview during a drag: the recompute copies and re-processes the whole crop, so throttle it.</summary>
    private void RecomputeMosaicThrottled()
    {
        if ((DateTime.UtcNow - _lastMosaicRecompute).TotalMilliseconds < 40) return;
        _lastMosaicRecompute = DateTime.UtcNow;
        RecomputeMosaic();
    }

    private void CopyCursorColor()
    {
        var c = SampleColor(_cursor);
        if (c is null) return;
        string hex = c.Value.ToHex(includeAlpha: false);
        try
        {
            Clipboard.SetText(hex);
            _colorFlashUntil = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
            Log.Info($"Copied color {hex}");
        }
        catch (Exception ex)
        {
            Log.Error("Copying the sampled color failed", ex);
        }
    }

    private void Nudge(int dx, int dy)
    {
        if (_selection.IsEmpty) return;
        var moved = ClampToVirtual(_selection.Offset(dx, dy));
        if (!moved.IsEmpty) MoveSelectionTo(moved.X, moved.Y);
        InvalidateAll();
    }

    private static PixelRect RectBetween(VPoint a, VPoint b) =>
        PixelRect.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    private static PixelRect Resize(PixelRect a, HandleDir h, int dx, int dy)
    {
        int l = a.X, t = a.Y, r = a.Right, b = a.Bottom;
        if (h is HandleDir.W or HandleDir.NW or HandleDir.SW) l = Math.Min(a.X + dx, r - MinSelection);
        if (h is HandleDir.E or HandleDir.NE or HandleDir.SE) r = Math.Max(a.Right + dx, l + MinSelection);
        if (h is HandleDir.N or HandleDir.NW or HandleDir.NE) t = Math.Min(a.Y + dy, b - MinSelection);
        if (h is HandleDir.S or HandleDir.SW or HandleDir.SE) b = Math.Max(a.Bottom + dy, t + MinSelection);
        return PixelRect.FromLTRB(l, t, r, b);
    }

    private PixelRect ClampToVirtual(PixelRect r)
    {
        int l = Math.Max(r.X, Virtual.X), t = Math.Max(r.Y, Virtual.Y);
        int rt = Math.Min(r.Right, Virtual.Right), b = Math.Min(r.Bottom, Virtual.Bottom);
        return rt > l && b > t ? PixelRect.FromLTRB(l, t, rt, b) : default;
    }

    private void EndSession(OverlayOutcome? outcome)
    {
        if (_ended) return;
        _ended = true;
        _outcome = outcome;
        foreach (var w in _windows.ToArray()) w.Close();
        _windows.Clear();
        Log.Info($"Overlay session ended: {(outcome is null ? "cancelled" : $"{outcome.Intent} {outcome.Region} window=0x{outcome.WindowHandle:X}")}");
        _finished?.Invoke();
    }
}
