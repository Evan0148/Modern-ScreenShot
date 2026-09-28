using System.Globalization;
using System.Windows;
using ModernScreenShot.App.Services;

namespace ModernScreenShot.App.Localization;

/// <summary>
/// Swaps the string ResourceDictionary merged into Application.Resources so DynamicResource bindings update live.
/// </summary>
public sealed class LocalizationService
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";
    public static readonly string[] SupportedLanguages = [Chinese, English];

    private static LocalizationService? _instance;
    private ResourceDictionary? _current;

    public LocalizationService() => _instance = this;

    /// <summary>The effective language ("zh-CN" or "en-US").</summary>
    public string CurrentLanguage { get; private set; } = English;

    /// <summary>The requested language ("" = follow system).</summary>
    public string RequestedLanguage { get; private set; } = "";

    public event EventHandler? LanguageChanged;

    // Captured before Apply() overwrites the thread UI culture, so "" keeps meaning the real OS language.
    private static readonly CultureInfo SystemUICulture = CultureInfo.CurrentUICulture;

    public static string ResolveSystemLanguage()
    {
        var ui = SystemUICulture;
        return ui.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Chinese : English;
    }

    public static string Normalize(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return ResolveSystemLanguage();
        if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return Chinese;
        return English;
    }

    public static ResourceDictionary LoadDictionary(string lang) =>
        new() { Source = new Uri($"pack://application:,,,/ModernScreenShot.App;component/Localization/Strings.{Normalize(lang)}.xaml", UriKind.Absolute) };

    /// <summary>Applies a language. "" follows the system UI culture. Must be called on the UI thread.</summary>
    public void Apply(string lang)
    {
        var previousRequested = RequestedLanguage;
        RequestedLanguage = lang ?? "";
        var effective = Normalize(lang);
        var dict = LoadDictionary(effective);
        var app = Application.Current;
        if (app is not null)
        {
            var merged = app.Resources.MergedDictionaries;
            if (_current is not null) merged.Remove(_current);
            merged.Add(dict);
        }
        _current = dict;
        bool changed = effective != CurrentLanguage;
        CurrentLanguage = effective;
        var culture = CultureInfo.GetCultureInfo(effective);
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        Thread.CurrentThread.CurrentUICulture = culture;
        Log.Info($"Language applied: {effective} (requested '{RequestedLanguage}')");
        // Also raise when only the requested language changed: with system=en-US, switching
        // "follow system" ↔ explicit en-US leaves the effective language unchanged but the
        // tray checkmark and settings combo must still refresh.
        if (changed || previousRequested != RequestedLanguage) LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Looks up a localized string (formatted with args). Returns the key itself when missing.</summary>
    public static string Get(string key, params object[] args)
    {
        object? value = null;
        if (_instance?._current is { } dict && dict.Contains(key)) value = dict[key];
        else if (Application.Current?.TryFindResource(key) is { } found) value = found;

        if (value is not string s)
        {
            Log.Warn($"Missing localization key '{key}'");
            return key;
        }
        if (args is null || args.Length == 0) return s;
        try
        {
            return string.Format(CultureInfo.CurrentCulture, s, args);
        }
        catch (FormatException ex)
        {
            Log.Error($"Bad format string for key '{key}'", ex);
            return s;
        }
    }

    /// <summary>Returns keys present in one dictionary but not the other (used by --smoke).</summary>
    public static IReadOnlyList<string> FindMissingKeys()
    {
        var zh = LoadDictionary(Chinese).Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet();
        var en = LoadDictionary(English).Keys.Cast<object>().Select(k => k.ToString()!).ToHashSet();
        var missing = new List<string>();
        missing.AddRange(en.Except(zh).Select(k => $"zh-CN missing '{k}'"));
        missing.AddRange(zh.Except(en).Select(k => $"en-US missing '{k}'"));
        return missing;
    }
}
