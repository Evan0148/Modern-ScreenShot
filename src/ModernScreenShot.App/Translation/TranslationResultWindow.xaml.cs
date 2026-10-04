using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ModernScreenShot.App.Controls;
using ModernScreenShot.App.Output;
using ModernScreenShot.Core.Translation;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Translation;

/// <summary>
/// Result surface for one translation pass: the recognized source text, the translation, a stats
/// line (target language / duration) and Copy. Deliberately the same shape as
/// <see cref="Ocr.OcrResultWindow"/> — same chrome, same read-only boxes, same copy affordance —
/// because it is the same kind of "here is what we read off the screen" surface.
/// </summary>
public partial class TranslationResultWindow : Window
{
    private readonly ClipboardService _clipboard;
    private string _stats;
    private string _targetName;
    private readonly DispatcherTimer _copyFlash = new() { Interval = TimeSpan.FromMilliseconds(1600) };
    private bool _swapBusy;
    private bool _closed;

    /// <summary>Raised when the user asks to translate the same source text the other way round.</summary>
    public event Action? SwapRequested;

    public TranslationResultWindow(
        TranslationOutcome outcome, ClipboardService clipboard, bool autoCopy,
        string targetName, bool canSwap = true)
    {
        InitializeComponent();
        AppTitleBar.Attach(this);
        _clipboard = clipboard;
        _targetName = targetName;

        bool empty = string.IsNullOrEmpty(outcome.Text);
        SourceHost.Text = outcome.SourceText;
        ResultHost.Text = empty ? L.Get("Translate.Empty") : outcome.Text;
        CopyButton.IsEnabled = !empty;
        SwapButton.Visibility = canSwap ? Visibility.Visible : Visibility.Collapsed;
        _stats = FormatStats(outcome);
        StatusText.Text = _stats;
        _copyFlash.Tick += (_, _) => { _copyFlash.Stop(); RestoreStatus(); };
        // The stats line carries formatted values, so it cannot refresh through DynamicResource;
        // re-derive it on activation in case the tray switched language while it was open.
        Activated += (_, _) => RestoreStatus();
        Closed += (_, _) => _closed = true;
        Loaded += (_, _) =>
        {
            if (autoCopy && !empty) CopyText();
            ResultHost.Focus();
        };
    }

    /// <summary>Rebuilds the header/status after a swap so the window reflects the new direction.
    /// Also the success path out of the swap-busy state: re-enables the swap button.</summary>
    public void ShowRetranslation(TranslationOutcome outcome, string targetName)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(() => ShowRetranslation(outcome, targetName)));
            return;
        }
        if (_closed) return;
        _targetName = targetName;
        bool empty = string.IsNullOrEmpty(outcome.Text);
        SourceHost.Text = outcome.SourceText;
        ResultHost.Text = empty ? L.Get("Translate.Empty") : outcome.Text;
        CopyButton.IsEnabled = !empty;
        _stats = FormatStats(outcome);
        EndSwapBusy();
    }

    /// <summary>Restores the swap button and stats line after a retranslation ended without a new result (failure/cancel). No-op when the window is already closed.</summary>
    public void RestoreSwapIdle()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RestoreSwapIdle));
            return;
        }
        if (_closed || !_swapBusy) return;
        EndSwapBusy();
    }

    /// <summary>Leaves the swap-busy state: button back on, status back to the stats line. UI thread only.</summary>
    private void EndSwapBusy()
    {
        _swapBusy = false;
        SwapButton.IsEnabled = true;
        _copyFlash.Stop();
        StatusText.Text = _stats;
    }

    /// <summary>Current non-flash status: the busy text while a retranslation is running, otherwise the stats line.</summary>
    private void RestoreStatus() =>
        StatusText.Text = _swapBusy ? L.Get("Translate.Toast.Translating") : _stats;

    private string FormatStats(TranslationOutcome outcome) =>
        L.Get("Translate.Status", _targetName, outcome.ElapsedMs / 1000.0);

    private void CopyText()
    {
        if (string.IsNullOrEmpty(ResultHost.Text) || !CopyButton.IsEnabled) return;
        if (!_clipboard.TryPutText(ResultHost.Text)) return;
        StatusText.Text = L.Get("Toast.Copied");
        _copyFlash.Stop();
        _copyFlash.Start();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyText();

    private void OnSwapClick(object sender, RoutedEventArgs e)
    {
        if (_swapBusy) return; // belt-and-braces: the disabled button already blocks a second click
        _swapBusy = true;
        SwapButton.IsEnabled = false;
        _copyFlash.Stop();
        StatusText.Text = L.Get("Translate.Toast.Translating");
        SwapRequested?.Invoke();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
