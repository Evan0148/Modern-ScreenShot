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
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Overlay;

/// <summary>How a confirmed capture should be dispatched. <see cref="Default"/> marks a confirm
/// gesture (Enter / double-click / click-snap / window pick) that carried no explicit toolbar
/// choice — dispatch then applies the user's configured "action after capture" setting.
/// The toolbar buttons carry their own explicit intents.</summary>
public enum OverlayIntent { Default, Edit, Copy, Save, Pin, Ocr }

public sealed class OverlayOutcome
{
    public required bool Confirmed { get; init; }
    /// <summary>Selected region (or picked window bounds) in virtual-screen physical pixels.</summary>
    public PixelRect Region { get; init; }
    /// <summary>Set when a window was confirmed in WindowPick mode (captured via PrintWindow).</summary>
    public IntPtr WindowHandle { get; init; }
    public WindowInfo? PickedWindow { get; init; }
    /// <summary>Set in Region mode when the confirmed selection coincides exactly with a snapshot
    /// window's bounds (click-snapped or hand-drawn); lets the capture bake the macOS-style shadow.</summary>
    public WindowInfo? SnappedWindow { get; init; }
    public OverlayIntent Intent { get; init; } = OverlayIntent.Default;
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
    private readonly bool _autoDetect;              // Snipaste-style element snapping (settings toggle)
    private bool _detectElements;                   // snapping master switch (settings toggle; WindowPick forces on)
    private bool _detectChildren = true;            // Tab's other half: include child UI elements while snapping
    private readonly Action? _persistEditorOptions; // saves settings.json when options changed (invoked on confirm)
    private bool _erasing;                          // eraser stroke in progress (one drag = one undo record)
    private bool _erasedAny;                        // eraser stroke deleted at least one item
    private DateTime _colorFlashUntil = DateTime.MinValue;
    // Precise operations: re-capture the frozen frame on demand (F5 / ` / !) and re-apply the last
    // confirmed region (Shift+R). Both are optional — diagnostics/tests construct sessions without them.
    /// <summary>Re-captures the frozen frame. The bool asks to FLIP the current cursor-capture
    /// setting for this one refresh (F5 passes false = honor the setting, ` / ! pass true).</summary>
    private readonly Func<bool, PixelBuffer>? _refreshFrozen;
    private readonly PixelRect? _lastRegion;        // CaptureSettings.LastRegion (virtual-screen physical px)
    // Transient status label (e.g. the detection mode after Tab): timestamp-gated like ColorFlash,
    // no animation, so diagnostic render paths stay deterministic.
    private string? _transientHint;
    private DateTime _transientHintUntil = DateTime.MinValue;

    internal PixelBuffer Frozen { get; private set; }
    internal PixelRect Virtual { get; }
    internal BitmapSource FrozenSource { get; private set; }
    internal CaptureMode Mode { get; }
    internal bool MagnifierEnabled { get; }
    /// <summary>Force the overlay to the very top of the z-order (elevated capture priority) so it can
    /// sit above always-on-top windows. Only set when the app is elevated (see CaptureService).</summary>
    internal bool CapturePriority { get; }
    /// <summary>When true a finished selection immediately confirms as Edit (scrolling capture); the toolbar is hidden.</summary>
    internal bool AutoConfirmOnSelect => _autoConfirm;

    internal OverlayState State => _state;
    internal PixelRect? Selection => _selection.IsEmpty ? null : _selection;
    internal WindowInfo? Hover => _hover;
    /// <summary>Bare-desktop snap target (Snipaste semantics): snapping is on, no window sits under
    /// the cursor, so the whole monitor under the cursor is the target — highlighted on hover and
    /// selected whole on click. Null while a real window is hovered or snapping is off.</summary>
    internal PixelRect? HoverMonitorBounds { get; private set; }
    internal bool AutoDetect => _detectElements;
    internal VPoint Cursor => _cursor;
    internal bool CursorValid => _cursorValid;
    internal bool ColorFlashActive => DateTime.UtcNow < _colorFlashUntil;
    internal bool CanRefreshFrozen => _refreshFrozen is not null;
    internal bool HasLastRegion => _lastRegion is { } r && !r.IsEmpty;
    /// <summary>Transient status text shown near the top of every overlay window while active.</summary>
    internal string? TransientHint => TransientHintActive ? _transientHint : null;
    internal bool TransientHintActive => DateTime.UtcNow < _transientHintUntil;

    /// <summary>Active inline-annotation tool; null means the selection itself is being adjusted.</summary>
    internal EditorTool? Tool
    {
        get => _tool;
        set
        {
            if (_autoConfirm || value == _tool || _drawPreview is not null || _erasing) return;
            _tool = value is EditorTool.Select ? null : value;
            AnnotationChanged?.Invoke(this, EventArgs.Empty);
            InvalidateAll();
        }
    }
    internal BitmapSource? MosaicSource => _mosaicSource;
    internal AnnotationItem? PreviewItem => _drawPreview;
    internal double EditorFontSize => _editor.FontSize;
    internal string EditorFontFamily => _editor.FontFamily;
    internal bool EditorFontBold => _editor.FontBold;
    internal string EditorStrokeColor => _editor.StrokeColor;
    /// <summary>Shared last-used annotation options; the overlay options bar writes into this instance.</summary>
    internal EditorSettings Editor => _editor;
    /// <summary>True once the user touched any option in the overlay options bar (persisted on confirm).</summary>
    private bool _optionsDirty;
    internal void MarkOptionsChanged() => _optionsDirty = true;
    /// <summary>Inline annotation is available: region mode, a selection exists, toolbar visible.</summary>
    internal bool CanAnnotate => !_autoConfirm && Mode == CaptureMode.Region
        && _state == OverlayState.Selected && !_selection.IsEmpty;

    public OverlaySession(CaptureMode mode, PixelBuffer frozen, PixelRect virtualScreen, BitmapSource frozenSource,
        MonitorService monitors, WindowEnumerator windowEnum, bool magnifierEnabled, EditorSettings editor,
        bool autoConfirmOnSelect = false, Action? persistEditorOptions = null, bool autoElementDetection = true,
        bool capturePriority = false, Func<bool, PixelBuffer>? refreshFrozen = null, PixelRect? lastRegion = null)
    {
        Mode = mode is CaptureMode.Region or CaptureMode.WindowPick ? mode : CaptureMode.Region;
        Frozen = frozen;
        Virtual = virtualScreen;
        FrozenSource = frozenSource;
        _monitors = monitors;
        _windowEnum = windowEnum;
        _editor = editor;
        MagnifierEnabled = magnifierEnabled;
        CapturePriority = capturePriority;
        _autoConfirm = autoConfirmOnSelect;
        _persistEditorOptions = persistEditorOptions;
        _refreshFrozen = refreshFrozen;
        _lastRegion = lastRegion is { IsEmpty: false } region ? region : null;
        // WindowPick always needs detection (that's the whole mode); Region honors the toggle.
        _autoDetect = autoElementDetection || Mode == CaptureMode.WindowPick;
        _detectElements = _autoDetect;
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
        HoverMonitorBounds = _hover is null && _cursorValid ? MonitorBoundsAt(_cursor) : null;

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

    internal void OnMove(OverlayWindow? w, VPoint p)
    {
        _cursor = p;
        _cursorValid = true;
        switch (_state)
        {
            case OverlayState.Idle:
                RefreshIdleHover();
                break;
            case OverlayState.Dragging:
                UpdateDrag(p);
                break;
            case OverlayState.Selected when _drawPreview is not null:
                UpdateDraw(p);
                break;
            case OverlayState.Selected when _erasing:
                EraseAt(ToImage(p));
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
        if (_erasing)
        {
            FinishErase();
            InvalidateAll();
            return;
        }
        if (_drawPreview is not null)
        {
            CommitDraw();
            InvalidateAll();
            return;
        }
        var drag = _drag;
        _drag = DragAction.None;
        if (drag is DragAction.Move or DragAction.Resize && Doc.Items.OfType<MosaicItem>().Any())
        {
            // MoveSelectionTo recomputes mosaic throttled during the drag; the drag end gets the
            // one exact pass so the pixels are never left at the last throttled frame.
            _lastMosaicRecompute = DateTime.MinValue;
            RecomputeMosaic();
        }

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
                    Confirm(OverlayIntent.Default);
                    return;
                }
            }
            else
            {
                _selection = ClampToVirtual(_selection);
                _state = OverlayState.Selected;
                if (_autoConfirm)
                {
                    Confirm(OverlayIntent.Default);
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
        if (Mode != CaptureMode.Region || _state != OverlayState.Selected || _selection.IsEmpty) return;
        // With an annotation tool active, a rapid double-click inside the selection is two presses
        // of that tool (e.g. two Step markers), not "confirm to editor" — only the Select tool
        // (Tool == null) confirms. The second press starts a stroke whose zero-size commit is
        // rejected, except for the Step tool where each tap places a marker.
        if (Tool is { } && _selection.Contains(p.X, p.Y))
        {
            BeginDraw(p);
            InvalidateAll();
            return;
        }
        Confirm(OverlayIntent.Default);
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
                    Confirm(OverlayIntent.Default);
                return true;
            case Key.C when MagnifierEnabled && _cursorValid && Keyboard.Modifiers == ModifierKeys.None:
                CopyCursorColor();
                InvalidateAll();
                return true;
            // ---- precise operations (Snipaste-style) ----
            case Key.W when Keyboard.Modifiers == ModifierKeys.None:
                MoveCursorBy(0, -1);
                return true;
            case Key.S when Keyboard.Modifiers == ModifierKeys.None:
                MoveCursorBy(0, 1);
                return true;
            case Key.D when Keyboard.Modifiers == ModifierKeys.None:
                MoveCursorBy(1, 0);
                return true;
            case Key.A when Keyboard.Modifiers == ModifierKeys.None
                && (!CanAnnotate || _drag is DragAction.Move or DragAction.Resize):
                // Inside a finished selection (no drag in progress) A stays the Arrow tool
                // (see KeyToTool); before a region exists — and while any drag is live — it moves
                // the pointer exactly like W/S/D.
                MoveCursorBy(-1, 0);
                return true;
            case Key.Tab when Mode == CaptureMode.Region && Keyboard.Modifiers == ModifierKeys.None:
                ToggleElementDetection();
                return true;
            case Key.D1 or Key.D2 when Keyboard.Modifiers == ModifierKeys.None:
                SelectElementLevel(up: key == Key.D1);
                return true;
            case Key.R when Mode == CaptureMode.Region && Keyboard.Modifiers == ModifierKeys.Shift:
                ApplyLastRegion(); // plain R is the Rect tool, so only Shift+R is bound
                return true;
            case Key.F5 when Keyboard.Modifiers == ModifierKeys.None && _refreshFrozen is not null:
                RefreshFrozenFrame(toggleCursor: false);
                return true;
            case Key.Oem3 when (Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) && _refreshFrozen is not null:
                RefreshFrozenFrame(toggleCursor: true); // ` (and ~)
                return true;
            case Key.D1 when Keyboard.Modifiers == ModifierKeys.Shift && _refreshFrozen is not null:
                RefreshFrozenFrame(toggleCursor: true); // "!" on US layouts
                return true;
            case Key.V or Key.R or Key.E or Key.L or Key.A or Key.P or Key.H or Key.T or Key.N or Key.M or Key.X
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
        // Remember the options the user changed in the options bar ("last used" behavior). A failed
        // save must never abort the capture the user just confirmed.
        if (_optionsDirty && _persistEditorOptions is { } persist)
        {
            _optionsDirty = false;
            try
            {
                persist();
            }
            catch (Exception ex)
            {
                Log.Error("Persisting overlay editor options failed", ex);
            }
        }
        Doc.ImageWidth = _selection.Width;
        Doc.ImageHeight = _selection.Height;
        EndSession(new OverlayOutcome
        {
            Confirmed = true,
            Region = _selection,
            SnappedWindow = Mode == CaptureMode.Region ? _windowEnum.FindByBounds(_selection) : null,
            Intent = intent,
            AnnotationDocument = Doc.Items.Count > 0 ? Doc : null,
        });
    }

    internal void ConfirmWindowPick()
    {
        var wi = _hover ?? (_cursorValid ? _windowEnum.HitTest(_cursor.X, _cursor.Y, includeChildren: true) : null);
        if (wi is null) return;
        _hover = wi;
        // Clamp like every other region path: a window extending past the virtual screen (stale
        // restored position) would otherwise yield a crop larger than the frozen frame.
        var region = ClampToVirtual(wi.Bounds);
        EndSession(new OverlayOutcome
        {
            Confirmed = true,
            Region = region,
            WindowHandle = wi.Handle,
            PickedWindow = wi,
            Intent = OverlayIntent.Default,
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
            // Throttled: this runs per mouse move during selection drags and each pass copies +
            // re-processes the whole selection crop (plus a BitmapSource.Create on top).
            if (_mosaicSource is not null || Doc.Items.OfType<MosaicItem>().Any()) RecomputeMosaicThrottled();
        }
    }

    private void SelectWindowUnderCursor()
    {
        if (!_detectElements) return; // a click snaps only when element detection is enabled
        var wi = _windowEnum.HitTest(_cursor.X, _cursor.Y, includeChildren: _detectChildren);
        _hover = wi;
        if (wi is not null)
        {
            _selection = ClampToVirtual(wi.Bounds);
            return;
        }
        // Snipaste semantics: the bare desktop is a snap target — a click selects the whole monitor
        // under the cursor (resize handles included; Enter confirms, dragging elsewhere redraws).
        if (MonitorBoundsAt(_cursor) is { } monitor) SelectRegion(monitor);
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
        Key.X => EditorTool.Eraser,
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
                var step = new StepItem { Center = img, Radius = _editor.StepRadius, StrokeColor = _editor.StrokeColor, Number = Doc.NextStepNumber() };
                Doc.Items.Add(step);
                Doc.RenumberSteps();
                FinishSnapshot();
                return;
            }
            case EditorTool.Eraser:
                StartErase(img);
                return;
        }
        _pendingSnapshot ??= UndoStack.Serialize(Doc);
        _drawPreview = Tool switch
        {
            EditorTool.Rect => new RectItem { Rect = new RectD(img.X, img.Y, 0, 0), Filled = _editor.FillShape, FillColor = AnnotationCanvas.FillColorFor(_editor.StrokeColor) },
            EditorTool.Ellipse => new EllipseItem { Rect = new RectD(img.X, img.Y, 0, 0), Filled = _editor.FillShape, FillColor = AnnotationCanvas.FillColorFor(_editor.StrokeColor) },
            EditorTool.Line => new LineItem { Start = img, End = img, Dashed = _editor.DashedLine },
            EditorTool.Arrow => new ArrowItem { Start = img, End = img, Dashed = _editor.DashedLine },
            EditorTool.Pen => new PenItem { Points = [img] },
            EditorTool.Highlighter => new HighlighterItem { Points = [img] },
            EditorTool.Mosaic => new MosaicItem
            {
                Mode = _editor.MosaicPixelate ? MosaicMode.Pixelate : MosaicMode.Blur,
                Strength = _editor.MosaicCellSize,
                Rect = new RectD(img.X, img.Y, 0, 0),
            },
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
            Bold = _editor.FontBold,
            StrokeColor = _editor.StrokeColor,
        };
        AnnotationCanvas.MeasureTextItem(item);
        Doc.Items.Add(item);
        FinishSnapshot();
    }

    // ---- eraser (drag over committed items to delete them; one drag = one undo record) ----

    private void StartErase(PointD img)
    {
        _erasing = true;
        _erasedAny = false;
        _pendingSnapshot ??= UndoStack.Serialize(Doc);
        EraseAt(img);
    }

    private void EraseAt(PointD img)
    {
        for (int i = Doc.Items.Count - 1; i >= 0; i--) // topmost first
        {
            var item = Doc.Items[i];
            if (!AnnotationRenderer.HitsForErase(item, img, Math.Max(6, item.StrokeThickness / 2 + 4))) continue;
            Doc.Items.RemoveAt(i);
            if (item is StepItem) Doc.RenumberSteps();
            if (item is MosaicItem) RecomputeMosaic();
            _erasedAny = true;
            break;
        }
    }

    private void FinishErase()
    {
        _erasing = false;
        if (_erasedAny) FinishSnapshot();
        else _pendingSnapshot = null; // nothing was hit: no undo entry
    }

    internal void DoUndo()
    {
        if (_drawPreview is not null || _erasing) return;
        var d = Undo.Undo(Doc);
        if (d is not null) ReplaceDoc(d);
    }

    internal void DoRedo()
    {
        if (_drawPreview is not null || _erasing) return;
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
        if (item is HighlighterItem)
        {
            item.Opacity = 0.45;
            item.StrokeThickness = _editor.StrokeThickness; // the thickness dots cover the highlighter too
        }
        else if (item is not MosaicItem and not TextItem and not StepItem)
        {
            item.StrokeThickness = _editor.StrokeThickness;
        }
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

    // ---- precise operations (Snipaste-style) ----

    /// <summary>Moves the real cursor by (dx, dy) via SetCursorPos (clamped to the virtual screen)
    /// and feeds the new position through the same OnMove path a physical mouse move takes, so
    /// hover detection, an in-flight handle/body drag, an in-progress annotation stroke and the
    /// magnifier all follow exactly as they would under the hand.</summary>
    private void MoveCursorBy(int dx, int dy)
    {
        if (!_cursorValid) return;
        int nx = Math.Clamp(_cursor.X + dx, Virtual.X, Math.Max(Virtual.X, Virtual.Right - 1));
        int ny = Math.Clamp(_cursor.Y + dy, Virtual.Y, Math.Max(Virtual.Y, Virtual.Bottom - 1));
        if (nx == _cursor.X && ny == _cursor.Y) return;
        if (!NativeMethods.SetCursorPos(nx, ny)) return;
        OnMove(null, new VPoint(nx, ny));
    }

    /// <summary>Tab: flips this session's detection granularity (windows ↔ include child UI
    /// elements) and refreshes the hover highlight under the cursor for the new mode. Inert when
    /// the settings have snapping off — Tab only splits the granularity of an enabled snapper.</summary>
    private void ToggleElementDetection()
    {
        if (!_detectElements) return;
        _detectChildren = !_detectChildren;
        ShowTransientHint(L.Get(_detectChildren ? "Overlay.ElementDetectionElements" : "Overlay.ElementDetectionWindows"));
        RefreshIdleHover();
        InvalidateAll();
    }

    /// <summary>Bounds of the monitor containing <paramref name="p"/>, or null when the point is
    /// outside every monitor (a stale cursor after a display topology change).</summary>
    private PixelRect? MonitorBoundsAt(VPoint p)
    {
        foreach (var m in _monitors.GetMonitors())
            if (m.Bounds.Contains(p.X, p.Y)) return m.Bounds;
        return null;
    }

    /// <summary>Idle-state hover resolution shared by mouse moves, Tab and session start: the
    /// top-most window/element under the cursor, or — Snipaste semantics — the whole monitor when
    /// the cursor sits on the bare desktop (highlighted on hover, selected whole on click).</summary>
    private void RefreshIdleHover()
    {
        if (!_detectElements || !_cursorValid)
        {
            _hover = null;
            HoverMonitorBounds = null;
            return;
        }
        _hover = _windowEnum.HitTest(_cursor.X, _cursor.Y, includeChildren: _detectChildren);
        HoverMonitorBounds = _hover is null ? MonitorBoundsAt(_cursor) : null;
    }

    /// <summary>1 / 2: from the currently hovered or selected window/element, walk UP to its parent
    /// window or DOWN to the deepest child window under the cursor. Both reuse the enumerator's
    /// DWM-corrected window plumbing, so the result snaps exactly like a click-snap. Does nothing
    /// when there is no current element and no parent/child to walk to.</summary>
    private void SelectElementLevel(bool up)
    {
        var current = CurrentElement();
        if (current is null) return;
        if (up)
        {
            if (_windowEnum.DescribeParent(current.Handle) is not { } parent) return;
            ApplyElement(parent);
        }
        else
        {
            if (!_cursorValid) return;
            if (_windowEnum.DeepestChildUnder(current.Handle, _cursor.X, _cursor.Y) is not { } child) return;
            ApplyElement(child);
        }
    }

    private WindowInfo? CurrentElement()
    {
        if (_hover is { } hovered) return hovered;
        if (!_selection.IsEmpty) return _windowEnum.FindByBounds(_selection);
        return null;
    }

    /// <summary>Snaps the current element: in Idle only the hover highlight moves (the user still
    /// clicks to confirm, exactly like mouse-driven snapping); in Selected the selection itself is
    /// re-snapped to the new element's DWM bounds (annotations stay glued to their screen pixels).</summary>
    private void ApplyElement(WindowInfo wi)
    {
        _hover = wi;
        HoverMonitorBounds = null;
        if (_state == OverlayState.Selected) SelectRegion(wi.Bounds);
        else InvalidateAll();
    }

    /// <summary>Ctrl+A: sets the selection to the whole monitor under the CURSOR — keyboard focus
    /// stays on the window activated at session start and never follows the mouse across monitors,
    /// so the receiving window's monitor is only the fallback for an invalid cursor. Enters the
    /// selected state; never auto-confirms.</summary>
    internal void SelectFullMonitor(PixelRect fallbackMonitor)
        => SelectRegion((_cursorValid ? MonitorBoundsAt(_cursor) : null) ?? fallbackMonitor);

    /// <summary>Ctrl+A / Shift+R / element navigation: replaces the selection, keeping annotations
    /// glued to their screen pixels (MoveSelectionTo semantics), and enters the selected state with
    /// resize handles. Never auto-confirms.</summary>
    internal void SelectRegion(PixelRect region)
    {
        var clipped = ClampToVirtual(region);
        if (clipped.IsEmpty) return;
        if (_selection.IsEmpty) _selection = clipped;
        else MoveSelectionTo(clipped.X, clipped.Y, clipped.Width, clipped.Height);
        _state = OverlayState.Selected;
        InvalidateAll();
    }

    /// <summary>Shift+R: applies the persisted last region (CaptureSettings.LastRegion), intersected
    /// with the monitor the cursor currently sits on — a region stored on another monitor must not
    /// produce a crop outside this session's frame.</summary>
    private void ApplyLastRegion()
    {
        if (_lastRegion is not { } last || last.IsEmpty) return;
        var monitor = (_cursorValid ? MonitorBoundsAt(_cursor) : null) ?? Virtual;
        var clipped = last.Intersect(monitor);
        if (clipped.IsEmpty)
        {
            ShowTransientHint(L.Get("Overlay.LastRegionOutside"));
            return;
        }
        SelectRegion(clipped);
    }

    /// <summary>F5 / ` / !: re-captures the frozen frame through the caller-supplied delegate.
    /// Every overlay window is hidden around the capture so the composed desktop — and therefore
    /// the fresh frame — can never contain the overlay UI itself. The frame size must stay
    /// identical (same monitor topology), which keeps all selection coordinates valid.</summary>
    private void RefreshFrozenFrame(bool toggleCursor)
    {
        if (_refreshFrozen is null || _ended) return;
        foreach (var w in _windows) w.Hide();
        PixelBuffer? fresh = null;
        try
        {
            System.Threading.Thread.Sleep(140); // let DWM recompose without the overlay windows
            fresh = _refreshFrozen(toggleCursor);
        }
        catch (Exception ex)
        {
            Log.Error("Refreshing the frozen frame failed", ex);
        }
        finally
        {
            foreach (var w in _windows) w.Show();
        }
        if (fresh is null) return;
        if (fresh.Width != Frozen.Width || fresh.Height != Frozen.Height)
        {
            Log.Warn($"Refreshed frame is {fresh.Width}x{fresh.Height}, expected {Frozen.Width}x{Frozen.Height}; keeping the previous frame.");
            return;
        }
        Frozen = fresh;
        FrozenSource = fresh.ToBitmapSource();
        // Caches derived from the old frame must not survive the swap.
        _cropPristine = null;
        _cropCacheRect = default;
        if (Doc.Items.OfType<MosaicItem>().Any()) RecomputeMosaic();
        foreach (var w in _windows) w.OnFrozenFrameSwapped();
    }

    /// <summary>Shows a short status label near the top of every overlay window (1.5 s,
    /// timestamp-gated — the same deterministic pattern as ColorFlash, no animation).</summary>
    private void ShowTransientHint(string text)
    {
        _transientHint = text;
        _transientHintUntil = DateTime.UtcNow + TimeSpan.FromSeconds(1.5);
        // One delayed repaint so the label actually disappears without further input.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1650) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!TransientHintActive) InvalidateAll();
        };
        timer.Start();
    }

    /// <summary>Cross-window support for the F1 help card: a click on any monitor's overlay window
    /// closes the card wherever it is shown.</summary>
    internal void HideHelpPanels()
    {
        foreach (var w in _windows) w.HideHelpPanel();
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
