using System.Globalization;
using System.Windows;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Settings;
using ModernScreenShot.Core.Translation;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Translation;

/// <summary>
/// The steps shared by every translation entry point (capture overlay, editor, swapped re-run):
/// pick the target language, make sure the model is on disk, and hand the text to the engine.
/// Lives here rather than in <see cref="EditorWindow"/> or the App partial so the two surfaces
/// cannot drift apart.
/// </summary>
internal static class TranslationFlow
{
    /// <summary>
    /// The target language for this text. Normally the configured one; with auto-detect on, a text
    /// that is already in the target language is translated into its Chinese/English counterpart
    /// instead — so a Chinese screenshot still produces English even when the target is Chinese.
    /// </summary>
    public static string ResolveTarget(string text, TranslationSettings settings)
    {
        string target = settings.ToCode;
        if (!settings.AutoDetectSource) return target;
        if (TranslationLanguages.ByCode(target) is null) return "zh";

        string detected = LanguageDetector.Detect(text);
        if (!string.Equals(detected, target, StringComparison.OrdinalIgnoreCase)) return target;

        // The text is already in the target language; flip only between the two the detector knows.
        string counterpart = string.Equals(detected, "zh", StringComparison.OrdinalIgnoreCase) ? "en" : "zh";
        return TranslationLanguages.ByCode(counterpart) is not null ? counterpart : target;
    }

    /// <summary>Persists the target actually used once the user swaps direction.</summary>
    public static void RememberTarget(SettingsStore store, string toCode)
    {
        if (string.Equals(store.Current.Translation.ToCode, toCode, StringComparison.OrdinalIgnoreCase)) return;
        store.Current.Translation.ToCode = toCode;
        store.Save();
    }

    /// <summary>Localized label for a language code, following the current UI language.</summary>
    public static string LanguageName(string code)
    {
        // LocalizationService.Apply keeps CurrentUICulture in step with the chosen language, so the
        // language names do not need a second channel for "which language is the UI in".
        bool chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("zh", StringComparison.OrdinalIgnoreCase);
        return TranslationLanguages.DisplayNameOf(code, chinese);
    }

    /// <summary>
    /// Translates recognized screen text. The text is reflowed into paragraphs first: the engine
    /// translates a prompt as a whole, but screen text arrives hard-wrapped, and keeping the line
    /// breaks would leak broken phrasing through (see <see cref="TextReflow"/>). The returned
    /// outcome carries the reflowed text as its source, which is what the result window shows — it
    /// is the text that was actually translated.
    /// </summary>
    public static Task<TranslationOutcome> TranslateAsync(
        TranslationService service, string text, string toCode, CancellationToken ct = default)
    {
        string prepared = TextReflow.ToParagraphs(text);
        return service.TranslateAsync(prepared, toCode, ct);
    }

    /// <summary>
    /// Makes sure the model is on disk, offering to download it (about 1.1 GB) when it is not.
    /// Returns false when the user declined or the download failed — after reporting why through
    /// <paramref name="notify"/>.
    /// </summary>
    public static async Task<bool> EnsureModelAsync(
        TranslationService service, Action<string, string>? notify, Window? owner)
    {
        if (!TranslationEngine.IsRuntimeInstalled)
        {
            Log.Error($"The llama.cpp runtime is missing under {TranslationEngine.RuntimeDir}");
            notify?.Invoke(L.Get("Translate.Title"), L.Get("Translate.EngineMissing"));
            return false;
        }
        if (TranslationModelDownloader.IsModelInstalled()) return true;

        string question = L.Get("Translate.NeedModelBody");
        string caption = L.Get("Translate.NeedModelTitle");
        var answer = owner is null
            ? MessageBox.Show(question, caption, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes)
            : MessageBox.Show(owner, question, caption, MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes);
        if (answer != MessageBoxResult.Yes) return false;

        try
        {
            Log.Info($"Translate: downloading {TranslationModelDownloader.ModelFileName} "
                     + $"({TranslationModelDownloader.ModelBytes / (1024 * 1024)} MB)");
            double lastReported = -1;
            var progress = new Progress<double>(fraction =>
            {
                // The download is long; report in 5% steps so the balloon is not re-posted constantly.
                if (fraction - lastReported < 0.05 && fraction < 1.0) return;
                lastReported = fraction;
                notify?.Invoke(L.Get("Translate.Title"), L.Get("Translate.Downloading", fraction * 100));
            });
            await TranslationModelDownloader.DownloadAsync(progress);

            // The engine had no model to load before; make it pick the new file up.
            await service.RestartAsync();
            Log.Info("Translate: model downloaded.");
            notify?.Invoke(L.Get("Translate.Title"), L.Get("Translate.DownloadDone"));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Downloading the translation model failed", ex);
            notify?.Invoke(L.Get("Translate.Title"), L.Get("Translate.DownloadFailed", ex.Message));
            return false;
        }
    }
}
