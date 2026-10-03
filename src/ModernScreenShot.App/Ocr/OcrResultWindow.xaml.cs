using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ModernScreenShot.App.Controls;
using ModernScreenShot.App.Output;
using ModernScreenShot.Core.Ocr;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Ocr;

/// <summary>
/// Result surface for one recognition pass: the text (read-only, selectable), a stats line
/// (lines / duration / model tier) and Copy. The clipboard write is what most users came for, so
/// the caller can pass <c>autoCopy</c> and the text lands on the clipboard as the window opens.
/// </summary>
public partial class OcrResultWindow : Window
{
    private readonly ClipboardService _clipboard;
    private readonly string _stats;
    private readonly DispatcherTimer _copyFlash = new() { Interval = TimeSpan.FromMilliseconds(1600) };

    public OcrResultWindow(OcrTextResult result, ClipboardService clipboard, bool autoCopy)
    {
        InitializeComponent();
        AppTitleBar.Attach(this);
        _clipboard = clipboard;

        bool empty = result.Lines.Count == 0;
        TextHost.Text = empty ? L.Get("Ocr.Empty") : result.Text;
        CopyButton.IsEnabled = !empty;
        _stats = FormatStats(result);
        StatusText.Text = _stats;
        _copyFlash.Tick += (_, _) => { _copyFlash.Stop(); StatusText.Text = _stats; };
        // The stats line is built imperatively (it carries formatted numbers), so unlike the
        // DynamicResource chrome it cannot refresh itself; re-derive it whenever the window is
        // re-activated — the tray's language switch is the only way it could have gone stale.
        Activated += (_, _) => StatusText.Text = _stats;
        Loaded += (_, _) =>
        {
            if (autoCopy && !empty) CopyText();
            TextHost.Focus();
        };
    }

    private static string FormatStats(OcrTextResult result)
    {
        string model = result is { AccurateRequested: true, FallbackToFast: false }
            ? L.Get("Ocr.ModelAccurate")
            : L.Get("Ocr.ModelFast");
        string stats = L.Get("Ocr.Status", result.Lines.Count, result.ElapsedMs / 1000.0, model);
        return result.FallbackToFast ? stats + " · " + L.Get("Ocr.FallbackFast") : stats;
    }

    private void CopyText()
    {
        if (string.IsNullOrEmpty(TextHost.Text) || !CopyButton.IsEnabled) return;
        if (!_clipboard.TryPutText(TextHost.Text)) return;
        StatusText.Text = L.Get("Toast.Copied");
        _copyFlash.Stop();
        _copyFlash.Start();
    }

    private void OnCopyClick(object sender, RoutedEventArgs e) => CopyText();

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
