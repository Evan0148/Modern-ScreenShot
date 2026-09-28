using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
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
    private Action? _finished;

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

    public OverlaySession(CaptureMode mode, PixelBuffer frozen, PixelRect virtualScreen, BitmapSource frozenSource,
        MonitorService monitors, WindowEnumerator windowEnum, bool magnifierEnabled, bool autoConfirmOnSelect = false)
    {
        Mode = mode is CaptureMode.Region or CaptureMode.WindowPick ? mode : CaptureMode.Region;
        Frozen = frozen;
        Virtual = virtualScreen;
        FrozenSource = frozenSource;
        _monitors = monitors;
        _windowEnum = windowEnum;
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
        EndSession(new OverlayOutcome { Confirmed = true, Region = _selection, Intent = intent });
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
                _selection = new PixelRect(nx, ny, _dragAnchor.Width, _dragAnchor.Height);
                break;
            }
            case DragAction.Resize:
                _selection = Resize(_dragAnchor, _handle, p.X - _pressPoint.X, p.Y - _pressPoint.Y);
                break;
        }
    }

    private void SelectWindowUnderCursor()
    {
        var wi = _windowEnum.HitTest(_cursor.X, _cursor.Y, includeChildren: true);
        _hover = wi;
        if (wi is null) return;
        _selection = ClampToVirtual(wi.Bounds);
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
        if (!moved.IsEmpty) _selection = moved;
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
