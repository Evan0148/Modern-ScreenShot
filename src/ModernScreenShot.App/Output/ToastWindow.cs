using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ModernScreenShot.App.Capture;

namespace ModernScreenShot.App.Output;

/// <summary>
/// Shared persistent feedback toast for long async operations (offline translation / OCR).
/// Visual identity matches PinWindow.ShowToast (PinWindow.cs:428-489): borderless topmost window,
/// dark 0xE0 background, white text, 14x8 padding, UiMotion.FadeSlideIn entrance, UiMotion.FadeOut
/// exit, bottom-right work-area anchoring — the user perceives "the same app's hint".
///
/// Differences from the pin toast:
///  - Persistent: no 2s auto-close while work is in progress; the caller closes it. Terminal states
///    (<see cref="Complete"/>) auto-close via FadeOut after ~3s (DispatcherTimer).
///  - <see cref="UpdateText"/> replaces the text in place (replace-in-place semantics).
///  - Optional cancel affordance: a plain text button whose label the caller supplies (localization
///    is resolved at call sites, never here); shown only when a callback is provided.
///  - Static <see cref="ShowOrUpdate"/> entry point with singleton replace-in-place semantics: a
///    living instance is updated (text + cancel callback swapped), otherwise a new one is created.
///    A new operation may supersede the old toast — that is the intended semantics.
///  - Modal coexistence: the returned instance can be used as a MessageBox owner, or
///    <see cref="DropTopmost"/>/<see cref="RestoreTopmost"/> temporarily yield while a modal is up.
/// </summary>
public sealed class ToastWindow : Window
{
    private static ToastWindow? _current;

    private readonly Border _root;   // visual root: motion targets this, never the window layer
    private readonly TextBlock _message;
    private readonly TextBlock _cancel;
    private Action? _onCancel;
    private bool _cancelInvoked;
    private DispatcherTimer? _autoCloseTimer;
    private bool _closing;
    private Rect _workArea;

    private ToastWindow(string text, Rect workArea, string? cancelLabel, Action? onCancel)
    {
        _workArea = workArea;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ShowInTaskbar = false;
        ShowActivated = false; // progress feedback must never steal focus
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        _message = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
        };
        _cancel = new TextBlock
        {
            Foreground = Brushes.White,
            TextDecorations = TextDecorations.Underline,
            Cursor = Cursors.Hand,
            Margin = new Thickness(12, 0, 0, 0),
            Opacity = 0.8,
        };
        _cancel.MouseLeftButtonUp += (_, _) => InvokeCancel();
        _cancel.MouseEnter += (_, _) => _cancel.Opacity = _cancelInvoked ? 0.5 : 1.0;
        _cancel.MouseLeave += (_, _) => _cancel.Opacity = _cancelInvoked ? 0.5 : 0.8;

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(_message);
        row.Children.Add(_cancel);

        _root = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E)),
            Padding = new Thickness(14, 8, 14, 8),
            Child = row,
        };
        Content = _root;
        SetCancel(cancelLabel, onCancel);

        Loaded += (_, _) => Place();
        // SizeToContent re-sizes anchor top-left: longer replacement text would grow right/bottom
        // off the corner — re-anchor on every content-driven size change (same as the pin toast).
        SizeChanged += (_, _) => Place();

        // Entrance: slide+fade the root before Show() so the first painted frame already has the
        // start pose (same recipe as the pin toast).
        UiMotion.FadeSlideIn(_root, 0, 8, 150);
        Show();
    }

    /// <summary>
    /// Singleton show-or-update: if a living toast exists it is updated in place (text, work area
    /// and cancel callback swapped, any pending terminal auto-close cancelled — the new operation
    /// supersedes the old); otherwise a new toast is created. A toast already mid-fade-out is left
    /// to die and a fresh one is created alongside it (same rule as the pin toast). Marshals to the
    /// UI dispatcher when called from a worker thread.
    /// </summary>
    /// <param name="text">Progress message, already localized by the caller.</param>
    /// <param name="workArea">Target monitor work area in DIPs (see <see cref="WorkAreaFor"/>);
    /// null → primary work area.</param>
    /// <param name="cancelLabel">Label for the optional cancel text button; null/empty hides it.</param>
    /// <param name="onCancel">Invoked when the cancel button is clicked; null hides the button.</param>
    public static ToastWindow ShowOrUpdate(string text, Rect? workArea = null,
        string? cancelLabel = null, Action? onCancel = null)
    {
        var app = Application.Current;
        if (app is not null && !app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => ShowOrUpdate(text, workArea, cancelLabel, onCancel));

        var area = workArea ?? SystemParameters.WorkArea;
        if (_current is { IsLoaded: true } existing && !existing._closing)
        {
            existing.StopAutoClose();
            existing._workArea = area;
            existing.UpdateText(text);
            existing.SetCancel(cancelLabel, onCancel);
            existing.Place();
            return existing;
        }
        var toast = new ToastWindow(text, area, cancelLabel, onCancel);
        _current = toast;
        return toast;
    }

    /// <summary>
    /// Converts a monitor's physical-pixel work area into the DIP rect this window positions
    /// against (per-monitor DPI aware — same computation PinWindow uses for its toast).
    /// </summary>
    public static Rect WorkAreaFor(MonitorInfo monitor)
    {
        double s = monitor.DpiX > 0 ? monitor.DpiX / 96.0 : 1.0;
        return new Rect(monitor.WorkArea.X / s, monitor.WorkArea.Y / s,
            monitor.WorkArea.Width / s, monitor.WorkArea.Height / s);
    }

    /// <summary>Replaces the message text in place; the toast stays put and does not replay its entrance.</summary>
    public void UpdateText(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => UpdateText(text)); return; }
        _message.Text = text;
    }

    /// <summary>Swaps the cancel affordance: label + callback. Null label or callback hides the button.</summary>
    public void SetCancel(string? label, Action? onCancel)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => SetCancel(label, onCancel)); return; }
        _onCancel = onCancel;
        _cancelInvoked = false;
        if (string.IsNullOrEmpty(label) || onCancel is null)
        {
            _cancel.Visibility = Visibility.Collapsed;
        }
        else
        {
            _cancel.Text = label;
            _cancel.Opacity = 0.8;
            _cancel.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Terminal state (failure / cancelled / done-with-note): optionally swaps in a final message,
    /// drops the cancel button and auto-closes via FadeOut after ~3s. Safe to call more than once.
    /// </summary>
    public void Complete(string? finalText = null)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Complete(finalText)); return; }
        if (_closing) return;
        if (finalText is not null) _message.Text = finalText;
        SetCancel(null, null);
        StopAutoClose();
        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _autoCloseTimer.Tick += (_, _) =>
        {
            StopAutoClose();
            BeginFadeOut();
        };
        _autoCloseTimer.Start();
    }

    /// <summary>Closes immediately without a fade (success paths that hand over to a result window).</summary>
    public void CloseInstant()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(CloseInstant); return; }
        if (_closing && !IsLoaded) return;
        _closing = true;
        StopAutoClose();
        Close();
    }

    /// <summary>Temporarily drops Topmost while a modal (MessageBox/dialog) is pending.</summary>
    public void DropTopmost()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(DropTopmost); return; }
        Topmost = false;
    }

    /// <summary>Restores Topmost after a modal has closed (no-op once the toast is on its way out).</summary>
    public void RestoreTopmost()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(RestoreTopmost); return; }
        if (!_closing) Topmost = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        StopAutoClose();
        if (ReferenceEquals(_current, this)) _current = null;
        base.OnClosed(e);
    }

    private void BeginFadeOut()
    {
        if (_closing) return;
        _closing = true;
        UiMotion.FadeOut(_root, 120, onCompleted: CloseInstant);
    }

    private void InvokeCancel()
    {
        if (_cancelInvoked || _onCancel is null) return;
        _cancelInvoked = true; // single-shot: a cancel click must never fire the callback twice
        _cancel.Opacity = 0.5;
        _onCancel();
    }

    private void StopAutoClose()
    {
        if (_autoCloseTimer is null) return;
        _autoCloseTimer.Stop();
        _autoCloseTimer = null;
    }

    private void Place()
    {
        var area = _workArea;
        Left = Math.Max(area.Left + 8, area.Right - ActualWidth - 24);
        Top = Math.Max(area.Top + 8, area.Bottom - ActualHeight - 24);
    }
}
