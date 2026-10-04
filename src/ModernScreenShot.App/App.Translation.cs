using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Output;
using ModernScreenShot.App.Services;
using ModernScreenShot.App.Translation;
using ModernScreenShot.Core.Ocr;
using ModernScreenShot.Core.Settings;
using ModernScreenShot.Core.Translation;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App;

/// <summary>
/// Translation feature wiring: reading the text off a capture (OCR) and running it through the
/// bundled Hy-MT2 model. Registration lives in App.Features.cs, the shared steps in
/// <see cref="TranslationFlow"/>, the engine client in
/// ModernScreenShot.Core.Translation.TranslationService.
/// </summary>
public partial class App
{
    /// <summary>Posts a tray balloon; the tray is optional (diagnostic runs have none).</summary>
    private void Notify(string title, string body) => _tray?.ShowNotification(title, body);

    /// <summary>
    /// Post-capture translation (overlay toolbar button or the "截图后翻译" action). The capture is
    /// recognized first — translating a screenshot starts with reading it — then the text goes
    /// through the model and the result window appears.
    /// </summary>
    private async void RunTranslateCapture(CaptureResult result)
    {
        var ct = BeginOperation();
        // Warm OCR takes 60-400ms; the 300ms gate keeps that hot path from flashing a toast.
        // No cancel button during OCR — a sub-second phase is not worth aborting.
        OperationToastDelayed(L.Get("Ocr.Running"), ct);
        try
        {
            var ocr = Services.GetRequiredService<OcrService>();
            if (!ocr.HasBundledModels)
            {
                Log.Error($"Translation needs OCR, but the models are missing under {ocr.BundleModelsDir}");
                OperationToastComplete(L.Get("Ocr.Unavailable"));
                Notify(L.Get("Translate.Title"), L.Get("Ocr.Unavailable"));
                return;
            }

            string text;
            try
            {
                // Same pixels the other direct outputs get: inline annotations flattened in.
                var image = ImageWithAnnotations(result);
                var recognized = await ocr.RecognizeAsync(
                    image, Services.GetRequiredService<SettingsStore>().Current.Ocr.Accuracy, ct);
                text = recognized.Text;
                Log.Info($"Translate: OCR read {recognized.Lines.Count} line(s) from the {result.Mode} capture.");
            }
            catch (OperationCanceledException)
            {
                // The OCR-phase toast has no cancel button, so this OCE can only come from a
                // superseding operation or app exit — the superseding operation owns the toast
                // now, so close silently instead of stomping it with "Cancelled".
                Log.Info("Translate: OCR cancelled before translation.");
                OperationToastClose();
                return;
            }
            catch (Exception ex)
            {
                Log.Error("OCR of the capture failed before translation", ex);
                OperationToastComplete(L.Get("Ocr.Failed"));
                Notify(L.Get("Translate.Title"), L.Get("Ocr.Failed"));
                return;
            }

            if (string.IsNullOrWhiteSpace(text))
            {
                OperationToastComplete(L.Get("Ocr.Empty"));
                Notify(L.Get("Translate.Title"), L.Get("Ocr.Empty"));
                return;
            }

            await TranslateAndShowAsync(text, ct);
        }
        finally
        {
            // TranslateAndShowAsync ends the operation in its own finally too; EndOperation is
            // idempotent, so the double call on the hand-off path is harmless.
            EndOperation();
        }
    }

    /// <summary>
    /// Translates <paramref name="text"/> into the resolved target language and opens the result
    /// window. A missing model is offered for download rather than dead-ending.
    /// </summary>
    private async Task TranslateAndShowAsync(string text, CancellationToken ct = default)
    {
        try
        {
            var store = Services.GetRequiredService<SettingsStore>();
            string target = TranslationFlow.ResolveTarget(text, store.Current.Translation);

            var engine = Services.GetRequiredService<TranslationService>();
            // Download progress goes to the toast (single in-progress channel), not the tray.
            // The toast is the MessageBox owner: it is topmost, so an owner-less consent box
            // would hide behind it.
            var toast = OperationToast(L.Get("Translate.Toast.LoadingEngine"), cancellable: true);
            var ensured = await TranslationFlow.EnsureModelAsync(engine,
                (_, body) => OperationToast(body, cancellable: true), toast, ct);
            if (ensured != EnsureModelResult.Ready)
            {
                // Failed already reported why through the toast above (null = terminal, keeping
                // that text); Declined (user said no to the download) and Cancelled both read as cancelled.
                OperationToastComplete(ensured == EnsureModelResult.Failed ? null : L.Get("Translate.Cancelled"));
                return;
            }

            try
            {
                OperationToast(L.Get("Translate.Toast.Translating"), cancellable: true);
                var outcome = await TranslationFlow.TranslateAsync(engine, text, target, ct);
                Log.Info($"Translate: -> {target} in {outcome.ElapsedMs} ms ({text.Length} chars).");
                OperationToastClose(); // the result window is the done signal
                ShowTranslationResult(outcome, text);
            }
            catch (OperationCanceledException)
            {
                // Cancellation is not a failure; it must never reach the Translate.Failed branch.
                Log.Info("Translate: cancelled by the user.");
                OperationToastComplete(L.Get("Translate.Cancelled"));
            }
            catch (Exception ex)
            {
                Log.Error("Translation failed", ex);
                OperationToastComplete(L.Get("Translate.Failed", ex.Message));
                Notify(L.Get("Translate.Title"), L.Get("Translate.Failed", ex.Message));
            }
        }
        finally
        {
            EndOperation();
        }
    }

    /// <summary>Opens the translation result window; tracked like the other floating windows.</summary>
    private void ShowTranslationResult(TranslationOutcome outcome, string sourceText, Window? owner = null)
    {
        var window = new TranslationResultWindow(outcome, Services.GetRequiredService<ClipboardService>(),
            Services.GetRequiredService<SettingsStore>().Current.Translation.CopyAfterTranslate,
            TranslationFlow.LanguageName(outcome.ToCode))
        {
            Owner = owner,
        };
        window.SwapRequested += async () => await SwapTranslationAsync(window, sourceText);
        if (owner is null)
        {
            // Only top-level floating windows participate in the "keep the app alive" tracking.
            window.Closed += OnEditorClosed;
            EditorWindows.Add(window);
        }
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Re-runs the same source text with the direction reversed (the result window's ⇄ button):
    /// the language the text was just translated into becomes the new target.
    /// </summary>
    private async Task SwapTranslationAsync(TranslationResultWindow window, string sourceText)
    {
        var ct = BeginOperation();
        try
        {
            var store = Services.GetRequiredService<SettingsStore>();
            var detected = LanguageDetector.Detect(sourceText);
            // Swap to the counterpart of the target that was just used.
            string from = store.Current.Translation.ToCode;
            string target = string.Equals(from, "zh", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";

            var engine = Services.GetRequiredService<TranslationService>();
            // Same channel unification as the capture path: download progress goes to the toast,
            // and the topmost toast owns the consent MessageBox so it cannot hide behind it.
            var toast = OperationToast(L.Get("Translate.Toast.LoadingEngine"), cancellable: true);
            var ensured = await TranslationFlow.EnsureModelAsync(engine,
                (_, body) => OperationToast(body, cancellable: true), toast, ct);
            if (ensured != EnsureModelResult.Ready)
            {
                OperationToastComplete(ensured == EnsureModelResult.Failed ? null : L.Get("Translate.Cancelled"));
                // The re-run died before producing a result: hand the swap button back to idle.
                if (window.IsLoaded) window.RestoreSwapIdle();
                return;
            }

            try
            {
                OperationToast(L.Get("Translate.Toast.Translating"), cancellable: true);
                var outcome = await TranslationFlow.TranslateAsync(engine, sourceText, target, ct);
                TranslationFlow.RememberTarget(store, target);
                OperationToastClose(); // the updated result window is the done signal
                window.ShowRetranslation(outcome, TranslationFlow.LanguageName(target));
                Log.Info($"Translate: swapped to {target} in {outcome.ElapsedMs} ms (text looked like '{detected}').");
            }
            catch (OperationCanceledException)
            {
                Log.Info("Translate: swapped re-run cancelled by the user.");
                OperationToastComplete(L.Get("Translate.Cancelled"));
                if (window.IsLoaded) window.RestoreSwapIdle();
            }
            catch (Exception ex)
            {
                Log.Error("Swapped translation failed", ex);
                OperationToastComplete(L.Get("Translate.Failed", ex.Message));
                Notify(L.Get("Translate.Title"), L.Get("Translate.Failed", ex.Message));
                if (window.IsLoaded) window.RestoreSwapIdle();
            }
        }
        finally
        {
            EndOperation();
        }
    }

    // ---------------------------------------------------------------- diagnostics

    private string? translateTestText;
    private string? translateTestTarget;

    /// <summary>--translate-test=&lt;text&gt; overrides the self-test input;
    /// --translate-to=&lt;code&gt; overrides its target language.</summary>
    private void CaptureTranslateTestArgument(string[] args)
    {
        var text = args.FirstOrDefault(a => a.StartsWith("--translate-test=", StringComparison.OrdinalIgnoreCase));
        if (text is not null) translateTestText = text["--translate-test=".Length..];

        var target = args.FirstOrDefault(a => a.StartsWith("--translate-to=", StringComparison.OrdinalIgnoreCase));
        if (target is not null) translateTestTarget = target["--translate-to=".Length..];
    }

    /// <summary>
    /// Runs a translation diagnostic on the dispatcher (it awaits engine I/O) and then exits with its
    /// verdict, matching the --smoke contract of "exit code 0 means it works".
    /// </summary>
    private void RunTranslateDiagnosticAndShutdown(bool install)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            int code = install ? await RunTranslateInstallAsync() : await RunTranslateSelfTestAsync();
            Shutdown(code);
        });
    }

    /// <summary>
    /// --translate-install: performs the real model download with no UI. Exists so the download path
    /// — the one with a ~1.1 GB payload behind it — can be verified on its own.
    /// </summary>
    private async Task<int> RunTranslateInstallAsync()
    {
        try
        {
            if (!TranslationEngine.IsRuntimeInstalled)
            {
                Log.Error($"translate-install: the llama.cpp runtime is missing under {TranslationEngine.RuntimeDir}.");
                return 1;
            }
            if (TranslationModelDownloader.IsModelInstalled())
            {
                Log.Info("translate-install: the model is already installed.");
                return 0;
            }

            Log.Info($"translate-install: downloading {TranslationModelDownloader.ModelFileName}");
            double lastReported = -1;
            var progress = new Progress<double>(fraction =>
            {
                if (fraction * 100 - lastReported < 10) return;
                lastReported = fraction * 100;
                Log.Info($"translate-install: {fraction * 100:0}%");
            });
            await TranslationModelDownloader.DownloadAsync(progress);

            bool ok = TranslationModelDownloader.IsModelInstalled();
            Log.Info(ok
                ? $"translate-install: model installed at {TranslationModelDownloader.ModelPath}."
                : "translate-install: the model did not land where expected.");
            return ok ? 0 : 1;
        }
        catch (Exception ex)
        {
            Log.Error("translate-install failed", ex);
            return 1;
        }
    }

    /// <summary>
    /// --translate-test: end-to-end self check over the real engine (no UI). Exit code 0 means the
    /// whole chain works; the translated text is written next to the log for inspection.
    /// </summary>
    private async Task<int> RunTranslateSelfTestAsync()
    {
        try
        {
            if (!TranslationEngine.IsRuntimeInstalled)
            {
                Log.Error($"translate-test: the llama.cpp runtime is missing under {TranslationEngine.RuntimeDir}; "
                          + "run tools/build-llama-runtime.ps1.");
                return 1;
            }
            if (!TranslationModelDownloader.IsModelInstalled())
            {
                Log.Error($"translate-test: the model is not installed. Run --translate-install first "
                          + $"(expected at {TranslationModelDownloader.ModelPath}).");
                return 1;
            }

            var store = Services.GetRequiredService<SettingsStore>();
            string text = string.IsNullOrWhiteSpace(translateTestText)
                ? "Hello, world! This is an offline translation self test."
                : translateTestText;
            string target = string.IsNullOrWhiteSpace(translateTestTarget)
                ? TranslationFlow.ResolveTarget(text, store.Current.Translation)
                : translateTestTarget;

            Log.Info($"translate-test: target {target}, input {text.Length} chars");
            var engine = Services.GetRequiredService<TranslationService>();
            var outcome = await TranslationFlow.TranslateAsync(engine, text, target);
            Log.Info($"translate-test: '{outcome.SourceText}' -> '{outcome.Text}' in {outcome.ElapsedMs} ms");

            string outPath = Path.Combine(AppPaths.LocalDir, "logs", "translate-test.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
            await File.WriteAllTextAsync(outPath,
                $"to={target}{Environment.NewLine}"
                + $"source={outcome.SourceText}{Environment.NewLine}result={outcome.Text}{Environment.NewLine}"
                + $"elapsedMs={outcome.ElapsedMs}{Environment.NewLine}");
            Log.Info($"translate-test: wrote {outPath}");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("translate-test failed", ex);
            return 1;
        }
    }

    /// <summary>
    /// --render-translate probe: builds a representative outcome and opens the result window so the
    /// diagnostic renderer can snapshot it. Uses fixed sample text on purpose — the probe verifies
    /// layout, and must not depend on the model being downloaded.
    /// </summary>
    private void ShowDiagnosticTranslate()
    {
        var sample = new TranslationOutcome(
            "The screenshot tool supports regions, windows and scrolling captures.",
            "截图工具支持区域截图、窗口截图和滚动长截图。",
            "zh", 214);
        ShowTranslationResult(sample, sample.SourceText);
    }
}
