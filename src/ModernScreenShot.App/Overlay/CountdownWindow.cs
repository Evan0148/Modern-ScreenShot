using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Overlay;

/// <summary>
/// Small topmost bubble near the cursor showing the remaining seconds of a delayed capture.
/// Esc cancels. <see cref="Run"/> blocks the caller with a nested dispatcher frame, exactly like
/// the overlay session does, and returns false when the countdown was cancelled.
/// </summary>
public sealed class CountdownWindow : Window
{
    private const int WidthDips = 168;
    private const int HeightDips = 176;

    private readonly TextBlock _secondsText;
    private int _remaining;
    private bool _confirmed;
    private bool _finished;
    private DispatcherFrame? _frame;

    public CountdownWindow()
    {
        Title = "ModernScreenShot Countdown";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        UseLayoutRounding = true;
        Width = WidthDips;
        Height = HeightDips;

        var title = new TextBlock
        {
            Text = L.Get("Countdown.Title"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)),
            Opacity = 0.85,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _secondsText = new TextBlock
        {
            FontSize = 56,
            FontWeight = FontWeights.Bold,
            Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 6),
        };
        var hint = new TextBlock
        {
            Text = L.Get("Countdown.Cancel"),
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEC)),
            Opacity = 0.65,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(title);
        stack.Children.Add(_secondsText);
        stack.Children.Add(hint);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x20, 0x20, 0x26)),
            CornerRadius = new CornerRadius(16),
            Child = stack,
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Finish(false);
        };
        // Alt+F4 or any external close counts as a cancellation.
        Closed += (_, _) => Finish(_confirmed);
    }

    /// <summary>
    /// Shows the countdown for <paramref name="seconds"/> on the monitor under the cursor and
    /// pumps the dispatcher until it finishes. Returns false when the user cancelled (Esc).
    /// </summary>
    public static bool Run(int seconds, MonitorService monitors)
    {
        if (seconds < 1) seconds = 1;
        var window = new CountdownWindow { _remaining = seconds };
        window.UpdateText();
        window.SourceInitialized += (_, _) => window.PlaceNearCursor(monitors);
        window._frame = new DispatcherFrame();
        window.Show();
        Log.Info($"Countdown started: {seconds}s");

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            window._remaining--;
            if (window._remaining <= 0)
            {
                timer.Stop();
                window.Finish(true);
            }
            else
            {
                window.UpdateText();
            }
        };
        timer.Start();

        Dispatcher.PushFrame(window._frame); // nested message loop, ends via Finish()
        timer.Stop();
        window.Close();
        Log.Info($"Countdown ended: {(window._confirmed ? "confirmed" : "cancelled")}.");
        return window._confirmed;
    }

    private void UpdateText() => _secondsText.Text = L.Get("Countdown.Seconds", Math.Max(_remaining, 0));

    private void Finish(bool confirmed)
    {
        if (_finished) return;
        _finished = true;
        _confirmed = confirmed;
        _frame?.Continue = false;
    }

    /// <summary>Physical-pixel placement near the cursor, clamped to the monitor's work area.</summary>
    private void PlaceNearCursor(MonitorService monitors)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        NativeMethods.GetCursorPos(out var cursor);
        var monitor = monitors.FromPoint(cursor.X, cursor.Y);
        double scale = monitor.DpiX / 96.0;
        int width = (int)Math.Round(WidthDips * scale);
        int height = (int)Math.Round(HeightDips * scale);
        int x = cursor.X - width / 2;
        int y = cursor.Y + 32; // just below the cursor
        var work = monitor.WorkArea;
        x = Math.Clamp(x, work.X, Math.Max(work.X, work.Right - width));
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - height));
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        long exStyle = NativeMethods.GetExStyle(hwnd);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            (IntPtr)(exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
    }
}
