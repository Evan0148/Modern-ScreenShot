using System.Windows;
using System.Windows.Interop;

namespace ModernScreenShot.App.Interop;

/// <summary>
/// Windows 11 Snap Layouts bridge for a custom (WindowChrome) title bar. The OS only offers the snap
/// flyout when <c>WM_NCHITTEST</c> answers <c>HTMAXBUTTON</c> over the maximize button; because the
/// caption band is non-client surface under WindowChrome, the WPF <c>CaptionButton</c> never sees
/// those pointer messages. This hook closes that gap for the maximize button only:
///
/// <list type="bullet">
/// <item><c>WM_NCHITTEST</c> returns <c>HTMAXBUTTON(9)</c> + <c>handled=true</c> when the screen point
/// is inside the max button's live rect AND the window is foreground AND carries
/// <c>WS_MAXIMIZEBOX</c>. <c>handled=true</c> short-circuits WindowChrome's own hit test, so the OS
/// treats the rect as the native maximize button and shows the snap flyout on hover.</item>
/// <item><c>WM_NCLBUTTONDOWN/UP</c> with <c>HTMAXBUTTON</c> drive the pressed visual and, on release,
/// the maximize/restore toggle (native button semantics — same convention as the cited
/// PSAppDeployToolkit <c>FluenceWindow</c>). <c>handled=true</c> on both suppresses the default NC
/// handling so the toggle happens exactly once.</item>
/// <item><c>WM_NCMOUSEMOVE</c> / <c>WM_NCMOUSELEAVE</c> / <c>WM_NCACTIVATE(0)</c> maintain the hover /
/// pressed state exposed to the caller (the app's <c>CaptionButton</c> renders it; this class never
/// touches visuals).</item>
/// </list>
///
/// The hook is installed on the <see cref="HwndSource"/> after WindowChrome is set, so it runs first
/// (HwndSource invokes hooks in reverse registration order) — see <c>.omo/evidence/hook-order-proof.md</c>.
/// On Windows 10 (build &lt; 22000) snap layouts do not exist, so <see cref="Install"/> is a no-op and
/// maximize keeps its normal behavior.
/// </summary>
public sealed class TitleBarSnapHook
{
    // ---- messages ----
    private const int WmNcActivate = 0x0086;
    private const int WmNcMouseMove = 0x00A0;
    private const int WmNcLButtonDown = 0x00A1;
    private const int WmNcLButtonUp = 0x00A2;
    private const int WmNcMouseLeave = 0x02A3;
    private const int WmNcHitTest = 0x0084;

    /// <summary>Hit-test code the OS maps to the native maximize/restore button (and snap flyout).</summary>
    private const int HtMaxButton = 9;

    /// <summary>WS_MAXIMIZEBOX: without it there is no maximize affordance to snap from.</summary>
    private const long WsMaximizeBox = 0x00010000L;

    /// <summary>Build at which snap layouts became available (Windows 11).</summary>
    private const int Windows11Build = 22000;

    private readonly Window _window;
    private readonly FrameworkElement _maxButton;
    private readonly IntPtr _hwnd;

    private bool _hover;
    private bool _pressed;
    private int _lastMoveX = int.MinValue;
    private int _lastMoveY = int.MinValue;

    /// <summary>True while the pointer hovers the maximize button in the non-client region.</summary>
    public bool IsOverMaxButton => _hover;

    /// <summary>True while the maximize button is held down in the non-client region.</summary>
    public bool IsMaxButtonPressed => _pressed;

    /// <summary>Raised on the UI thread when <see cref="IsOverMaxButton"/> or
    /// <see cref="IsMaxButtonPressed"/> changes, so callers can re-render the button.</summary>
    public event EventHandler? StateChanged;

    private TitleBarSnapHook(Window window, FrameworkElement maxButton, IntPtr hwnd)
    {
        _window = window;
        _maxButton = maxButton;
        _hwnd = hwnd;
    }

    /// <summary>
    /// Installs the snap bridge on <paramref name="window"/> for its <paramref name="maxButton"/>.
    /// Returns the hook for state subscription, or <c>null</c> when the OS predates Windows 11 or the
    /// window handle/source is not available yet (nothing is installed in that case).
    /// </summary>
    public static TitleBarSnapHook? Install(Window window, FrameworkElement maxButton)
    {
        if (Environment.OSVersion.Version.Build < Windows11Build) return null;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return null;
        if (HwndSource.FromHwnd(hwnd) is not { } source) return null;

        var hook = new TitleBarSnapHook(window, maxButton, hwnd);
        source.AddHook(hook.WndProc);
        window.Closed += (_, _) => source.RemoveHook(hook.WndProc);
        return hook;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WmNcHitTest:
                if (IsPointOverMaxButton(lParam) && CanSnap())
                {
                    handled = true;
                    return new IntPtr(HtMaxButton);
                }
                break;

            case WmNcLButtonDown:
                // Swallow the native default so the toggle below is the only one; the button shows
                // its pressed fill through IsMaxButtonPressed.
                if (wParam.ToInt64() == HtMaxButton && CanSnap())
                {
                    SetPressed(true);
                    handled = true;
                }
                break;

            case WmNcLButtonUp:
                if (wParam.ToInt64() == HtMaxButton && CanSnap())
                {
                    SetPressed(false);
                    ToggleMaximize();
                    handled = true;
                }
                else if (_pressed)
                {
                    SetPressed(false);
                }
                break;

            case WmNcMouseMove:
            {
                // WM_NCMOUSEMOVE carries the cursor position; track the rect rather than trusting
                // wParam so leaving the button (while still over the caption) clears the hover.
                bool over = IsPointOverMaxButton(lParam) && CanSnap();
                GetScreenPoint(lParam, out int mx, out int my);
                bool moved = mx != _lastMoveX || my != _lastMoveY;
                _lastMoveX = mx;
                _lastMoveY = my;
                // A genuine move onto the button re-arms hover; the OS also replays NCMOUSEMOVE at
                // the same pixel after a leave (and while the snap flyout polls), which must not
                // resurrect a hover that a leave/deactivate just cleared.
                if (!over) SetHover(false);
                else if (moved) SetHover(true);
                if (!over) SetPressed(false);
                break;
            }

            case WmNcMouseLeave:
                SetHover(false);
                SetPressed(false);
                break;

            case WmNcActivate:
                // wParam == 0: the window is being deactivated — the OS no longer routes NC hover
                // here, so drop any highlight to avoid a stuck state.
                if (wParam == IntPtr.Zero)
                {
                    SetHover(false);
                    SetPressed(false);
                }
                break;
        }

        return IntPtr.Zero;
    }

    private bool CanSnap() =>
        _hwnd == NativeMethods.GetForegroundWindow() &&
        (NativeMethods.GetStyle(_hwnd) & WsMaximizeBox) != 0;

    private bool IsPointOverMaxButton(IntPtr lParam)
    {
        if (!_maxButton.IsVisible || _maxButton.ActualWidth <= 0 || _maxButton.ActualHeight <= 0)
            return false;
        try
        {
            var topLeft = _maxButton.PointToScreen(new Point(0, 0));
            var bottomRight = _maxButton.PointToScreen(new Point(_maxButton.ActualWidth, _maxButton.ActualHeight));
            GetScreenPoint(lParam, out int x, out int y);
            return x >= topLeft.X && x < bottomRight.X && y >= topLeft.Y && y < bottomRight.Y;
        }
        catch (InvalidOperationException)
        {
            // Not connected to a presentation source yet: not over the button.
            return false;
        }
    }

    private static void GetScreenPoint(IntPtr lParam, out int x, out int y)
    {
        long value = lParam.ToInt64();
        x = unchecked((short)(value & 0xFFFF));
        y = unchecked((short)((value >> 16) & 0xFFFF));
    }

    private void ToggleMaximize() =>
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void SetHover(bool value)
    {
        if (_hover == value) return;
        _hover = value;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetPressed(bool value)
    {
        if (_pressed == value) return;
        _pressed = value;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }
}
