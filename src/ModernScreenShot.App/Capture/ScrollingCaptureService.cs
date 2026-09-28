using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Settings;
using CaptureMode = ModernScreenShot.Core.Settings.CaptureMode;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Capture;

/// <summary>
/// Long screenshot: after a region has been selected, captures one frame per interval, scrolls the
/// page down with a synthetic wheel event at the region center and stitches the frames incrementally
/// with <see cref="ScrollStitcher"/>. Everything runs on the UI thread; the loop is pumped with a
/// nested dispatcher frame (same pattern as the overlay session).
/// </summary>
public sealed class ScrollingCaptureService
{
    private const int WheelNotches = 3;      // wheel notches per interval (mouseData = -3 * WHEEL_DELTA)
    private const int BottomFrames = 2;      // consecutive identical frames that mean "scrolled to the end"
    private const int ManualRounds = 3;      // consecutive no-progress rounds before falling back to manual scrolling
    private const int MaxCaptureFailures = 3;
    private const int MinIntervalMs = 50;
    private const int MinMaxHeight = 200;

    private static bool _active; // one scrolling capture at a time (whole flow is UI-thread bound)

    private readonly ScreenCapturer _screen;
    private readonly SettingsStore _settings;

    // Low-level keyboard hook state (UI thread only, single active capture enforced by _active).
    private LowLevelKeyboardProc? _hookProc;
    private Action? _onEscape;
    private IntPtr _hook;

    public ScrollingCaptureService(ScreenCapturer screen, SettingsStore settings)
    {
        _screen = screen;
        _settings = settings;
    }

    /// <summary>Runs a scrolling capture for a physical-pixel virtual-screen region.
    /// Returns null when the user cancels or the capture fails.</summary>
    public CaptureResult? Run(PixelRect region)
    {
        if (region.IsEmpty) return null;
        if (_active)
        {
            Log.Warn("A scrolling capture is already running; ignoring the new request.");
            return null;
        }
        _active = true;
        try
        {
            return RunCore(region);
        }
        finally
        {
            _active = false;
        }
    }

    private CaptureResult? RunCore(PixelRect region)
    {
        ScrollingStatusWindow? status = null;
        DispatcherTimer? timer = null;
        DispatcherFrame? frame = null;
        bool hasCursorPos = NativeMethods.GetCursorPos(out var cursorBefore);
        try
        {
            var captureSettings = _settings.Current.Capture;
            int interval = Math.Clamp(captureSettings.ScrollIntervalMs, MinIntervalMs, 5000);
            int maxHeight = Math.Max(MinMaxHeight, captureSettings.ScrollMaxHeight);
            var stitcher = new ScrollStitcher(maxHeight);
            int centerX = region.X + region.Width / 2;
            int centerY = region.Y + region.Height / 2;

            bool manual = false;   // auto-scroll abandoned; the user scrolls by hand
            int idleRounds = 0;    // consecutive rounds without new content
            int scrollRounds = 0;  // rounds where scrolling produced new content
            int failedRounds = 0;  // consecutive frame capture failures
            int failedStitches = 0; // consecutive stitch failures (e.g. OOM)
            bool finished = false;

            void Finish(string reason)
            {
                if (finished) return;
                finished = true;
                Log.Info($"Scrolling capture finished ({reason}): {stitcher.FrameCount} frames, {stitcher.Height}px stitched.");
                if (frame is not null) frame.Continue = false;
            }

            status = new ScrollingStatusWindow();
            status.StopRequested += (_, _) => Finish("stopped by user");
            status.ShowFor(region);

            _onEscape = () => Finish("stopped by Esc");
            _hookProc = OnKeyboardHook;
            _hook = NativeMethods.SetWindowsHookEx(NativeMethods.WH_KEYBOARD_LL, _hookProc,
                NativeMethods.GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero)
                Log.Warn($"Scrolling capture: keyboard hook failed (error {Marshal.GetLastWin32Error()}); use the Stop button.");

            // Frame 0 is the unscrolled view; one wheel event right away so the first tick already
            // sees scrolled content (idle rounds below therefore always follow a real scroll attempt).
            stitcher.AddFrame(_screen.Capture(region, includeCursor: false));
            status.UpdateProgress(stitcher.Height);
            SendWheel(centerX, centerY);

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(interval) };
            timer.Tick += (_, _) =>
            {
                PixelBuffer captured;
                try
                {
                    captured = _screen.Capture(region, includeCursor: false);
                    failedRounds = 0;
                }
                catch (Exception ex)
                {
                    failedRounds++;
                    Log.Error($"Scrolling capture: frame capture failed ({failedRounds}/{MaxCaptureFailures})", ex);
                    if (failedRounds >= MaxCaptureFailures) Finish("frame capture failed");
                    return;
                }

                bool added;
                try
                {
                    added = stitcher.AddFrame(captured);
                }
                catch (ArgumentException ex)
                {
                    Log.Error("Scrolling capture: frame size changed mid-capture", ex);
                    Finish("frame size mismatch");
                    return;
                }
                catch (Exception ex)
                {
                    // e.g. OutOfMemoryException: the stitcher state must be considered corrupt.
                    failedStitches++;
                    Log.Error($"Scrolling capture: frame stitching failed ({failedStitches}/{MaxCaptureFailures})", ex);
                    if (failedStitches >= MaxCaptureFailures) Finish("frame stitching failed");
                    return;
                }
                status.UpdateProgress(stitcher.Height);

                if (stitcher.Height >= maxHeight)
                {
                    Finish("max height reached");
                    return;
                }

                if (added)
                {
                    idleRounds = 0;
                    scrollRounds++;
                }
                else
                {
                    idleRounds++;
                }

                if (manual) return; // keep capturing; the user scrolls by hand and stops when done

                if (scrollRounds >= 1 && idleRounds >= BottomFrames)
                {
                    Finish("scrolled to the end");
                    return;
                }
                if (scrollRounds == 0 && idleRounds >= ManualRounds)
                {
                    manual = true;
                    status.ShowManualHint();
                    Log.Warn("Scrolling capture: auto-scroll has no effect here; switched to manual mode.");
                    return; // no further synthetic wheel events
                }
                SendWheel(centerX, centerY);
            };
            timer.Start();

            frame = new DispatcherFrame();
            Dispatcher.PushFrame(frame); // nested message loop, ends via Finish()
            timer.Stop();

            var result = stitcher.Result;
            if (result is null || stitcher.FrameCount == 0)
            {
                Log.Warn("Scrolling capture produced no usable image.");
                return null;
            }
            return new CaptureResult { Image = result, Mode = CaptureMode.Scrolling, SourceRect = region };
        }
        catch (Exception ex)
        {
            Log.Error("Scrolling capture failed", ex);
            return null;
        }
        finally
        {
            if (_hook != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hook);
                _hook = IntPtr.Zero;
            }
            if (timer is not null) timer.Stop();
            status?.Close();
            if (frame is not null) frame.Continue = false; // safety net if Finish never ran
            if (hasCursorPos) NativeMethods.SetCursorPos(cursorBefore.X, cursorBefore.Y); // SendWheel moved it into the region
        }
    }

    /// <summary>Moves the cursor to the region center and sends one 3-notch wheel-down event.</summary>
    private static void SendWheel(int centerX, int centerY)
    {
        if (!NativeMethods.SetCursorPos(centerX, centerY))
            Log.Warn("Scrolling capture: SetCursorPos failed; the wheel event may reach the wrong window.");
        var input = new INPUT { type = NativeMethods.INPUT_MOUSE };
        input.U.mi.mouseData = -WheelNotches * NativeMethods.WHEEL_DELTA;
        input.U.mi.dwFlags = NativeMethods.MOUSEEVENTF_WHEEL;
        if (NativeMethods.SendInput(1, [input], INPUT.Size) != 1)
            Log.Warn($"Scrolling capture: SendInput(wheel) failed (error {Marshal.GetLastWin32Error()}).");
    }

    private IntPtr OnKeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0
            && (wParam == (IntPtr)NativeMethods.WM_KEYDOWN || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN)
            && Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam).vkCode == NativeMethods.VK_ESCAPE)
        {
            _onEscape?.Invoke();
            return new IntPtr(1); // swallow Esc so the page under capture does not react to it
        }
        return NativeMethods.CallNextHookEx(_hook, nCode, wParam, lParam);
    }
}

/// <summary>
/// Small borderless topmost HUD shown while a scrolling capture runs: live progress, a hint line and
/// a Stop button. It never takes focus (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE), so keyboard input —
/// including Esc — stays with the captured app; Esc is detected through the low-level hook in
/// <see cref="ScrollingCaptureService"/>.
/// </summary>
internal sealed class ScrollingStatusWindow : Window
{
    private const double WidthDips = 276;
    private const int MarginPx = 12;

    private readonly TextBlock _progress;
    private readonly TextBlock _hint;

    /// <summary>Raised when the user clicks Stop, presses Esc (focus fallback) or the window closes.</summary>
    public event EventHandler? StopRequested;

    public ScrollingStatusWindow()
    {
        Title = "ModernScreenShot Scrolling";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;

        _progress = new TextBlock
        {
            Text = L.Get("Scroll.Scrolling", 0),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _hint = new TextBlock
        {
            Text = L.Get("Scroll.Hint"),
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            LineHeight = 15,
            MinHeight = 31, // room for two lines so switching to the manual hint never reflows the window
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)),
            Opacity = 0.75,
            Margin = new Thickness(0, 4, 0, 10),
        };
        var stack = new StackPanel();
        stack.Children.Add(_progress);
        stack.Children.Add(_hint);
        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = BuildStopButton(),
        });
        var root = new Border
        {
            Width = WidthDips,
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x26)),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16, 12, 16, 12),
            Child = stack,
        };
        Content = root;

        // Measure now so the physical-pixel placement knows the final window size.
        root.Measure(new Size(WidthDips, double.PositiveInfinity));
        Width = WidthDips;
        Height = Math.Ceiling(root.DesiredSize.Height);

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            StopRequested?.Invoke(this, EventArgs.Empty);
        };
        Closed += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Shows the window at the bottom-right of the cursor monitor's work area, shifted to
    /// another corner or monitor when that spot would overlap the capture region.</summary>
    public void ShowFor(PixelRect region)
    {
        SourceInitialized += (_, _) => Place(region);
        Show();
    }

    public void UpdateProgress(int heightPx) => _progress.Text = L.Get("Scroll.Scrolling", heightPx);

    public void ShowManualHint() => _hint.Text = L.Get("Scroll.Manual");

    private Button BuildStopButton()
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = "\uE71A",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = L.Get("Scroll.Stop"),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var button = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            Padding = new Thickness(14, 6, 14, 6),
            Cursor = Cursors.Hand,
            Focusable = false,
        };
        var hover = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.Click += (_, _) => StopRequested?.Invoke(this, EventArgs.Empty);
        return button;
    }

    private void Place(PixelRect region)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var monitors = new MonitorService();
        NativeMethods.GetCursorPos(out var cursor);
        var monitor = monitors.FromPoint(cursor.X, cursor.Y);
        double scale = monitor.DpiX / 96.0;
        int w = (int)Math.Ceiling(Width * scale);
        int h = (int)Math.Ceiling(Height * scale);
        var work = monitor.WorkArea;

        // Preferred: bottom-right of the cursor monitor's work area. The HUD is a real window, so it
        // would be stitched into every frame when overlapping the region — try other corners, then
        // other monitors, before falling back to the default spot.
        var candidates = new List<(int X, int Y)>
        {
            (work.Right - w - MarginPx, work.Bottom - h - MarginPx),
            (work.X + MarginPx, work.Bottom - h - MarginPx),
            (work.Right - w - MarginPx, work.Y + MarginPx),
            (work.X + MarginPx, work.Y + MarginPx),
        };
        foreach (var other in monitors.GetMonitors())
        {
            if (other.Handle == monitor.Handle) continue;
            var ow = other.WorkArea;
            candidates.Add((ow.Right - w - MarginPx, ow.Bottom - h - MarginPx));
        }

        int x = work.Right - w - MarginPx, y = work.Bottom - h - MarginPx;
        foreach (var (cx, cy) in candidates)
        {
            if (PixelRect.FromLTRB(cx, cy, cx + w, cy + h).Intersect(region).IsEmpty)
            {
                x = cx;
                y = cy;
                break;
            }
        }

        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        long ex = NativeMethods.GetExStyle(hwnd);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            (IntPtr)(ex | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE));
    }
}
