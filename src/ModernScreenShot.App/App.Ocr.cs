using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Extensions.DependencyInjection;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Ocr;
using ModernScreenShot.App.Output;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Ocr;
using ModernScreenShot.Core.Settings;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App;

/// <summary>
/// OCR feature wiring: recognition of confirmed captures and the result window. Registration lives
/// in App.Features.cs (RegisterFeatureServices / DispatchCaptureResult); the engine itself is
/// ModernScreenShot.Core.Ocr.OcrService, the editor has its own trigger on the same service.
/// </summary>
public partial class App
{
    /// <summary>
    /// Post-capture OCR (overlay toolbar button or "文字识别" as the configured after-capture
    /// action). Recognition runs on the thread pool — a 4K capture takes a few hundred ms — then the
    /// result window appears (and copies the text, when the setting asks for it).
    /// </summary>
    private async void RunOcrCapture(CaptureResult result)
    {
        var ocr = Services.GetRequiredService<OcrService>();
        if (!ocr.HasBundledModels)
        {
            Log.Error($"OCR models are missing under {ocr.BundleModelsDir}");
            _tray?.ShowNotification(L.Get("Ocr.Title"), L.Get("Ocr.Unavailable"));
            return;
        }
        try
        {
            // Same pixels the other direct outputs get: with inline annotations flattened in.
            var image = ImageWithAnnotations(result);
            var outcome = await ocr.RecognizeAsync(image, Services.GetRequiredService<SettingsStore>().Current.Ocr.Accuracy);
            Log.Info($"OCR finished for {result.Mode} capture: {outcome.Lines.Count} line(s), {outcome.ElapsedMs} ms"
                     + (outcome.FallbackToFast ? " (accurate models missing, used fast)." : "."));
            ShowOcrResult(outcome);
        }
        catch (Exception ex)
        {
            Log.Error("OCR of the capture failed", ex);
            _tray?.ShowNotification(L.Get("Ocr.Title"), L.Get("Ocr.Failed"));
        }
    }

    /// <summary>Opens the shared OCR result window; it is tracked like the other floating windows so
    /// the app stays alive while it is visible.</summary>
    private void ShowOcrResult(OcrTextResult outcome)
    {
        var window = new OcrResultWindow(outcome, Services.GetRequiredService<ClipboardService>(),
            Services.GetRequiredService<SettingsStore>().Current.Ocr.CopyAfterRecognize);
        window.Closed += OnEditorClosed;
        EditorWindows.Add(window);
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// --render-ocr probe: runs a real recognition over a synthetic text image (so the snapshot
    /// shows genuine content) and opens the result window for the diagnostic renderer.
    /// </summary>
    private void ShowDiagnosticOcr()
    {
        var image = RenderSampleText("文字识别诊断：你好，世界！Hello, World! 2026");
        var outcome = Services.GetRequiredService<OcrService>().Recognize(image, OcrAccuracy.Fast);
        ShowOcrResult(outcome);
    }

    /// <summary>Renders one line of text onto a white canvas — the deterministic "screenshot" the
    /// --render-ocr probe recognizes.</summary>
    private static PixelBuffer RenderSampleText(string text)
    {
        var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Microsoft YaHei UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            34, Brushes.Black, 96);
        int w = (int)Math.Ceiling(formatted.Width) + 60;
        int h = (int)Math.Ceiling(formatted.Height) + 60;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, w, h));
            dc.DrawText(formatted, new Point(30, 30));
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        int stride = w * 4;
        var data = new byte[stride * h];
        rtb.CopyPixels(data, stride, 0);
        return new PixelBuffer(w, h, data);
    }
}
