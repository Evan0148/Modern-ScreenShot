using System.Text.Json;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Ocr;

namespace ModernScreenShot.Core.Settings;

public sealed class SettingsStore
{
    private readonly object _gate = new();
    public string FilePath { get; }
    public AppSettings Current { get; private set; } = new();
    public event EventHandler? Saved;

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(AppPaths.RoamingDir, "settings.json");
    }

    /// <summary>Loads settings; on any failure backs up the corrupt file and falls back to defaults.</summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                LoadError = null;
                if (File.Exists(FilePath))
                {
                    var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonDefaults.Options);
                    Current = Normalize(loaded ?? new AppSettings());
                }
                else Current = new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                LoadError = ex.Message;
                TryBackupCorrupt();
                Current = new AppSettings();
            }
            return Current;
        }
    }

    public string? LoadError { get; private set; }

    public void Save()
    {
        lock (_gate)
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonDefaults.Options));
            File.Move(tmp, FilePath, overwrite: true);
        }
        Saved?.Invoke(this, EventArgs.Empty);
    }

    public void Replace(AppSettings settings)
    {
        lock (_gate) Current = Normalize(settings);
    }

    private static AppSettings Normalize(AppSettings s)
    {
        s.Hotkeys ??= new HotkeySettings();
        s.Hotkeys.Bindings ??= HotkeySettings.CreateDefaults();
        // Explicit JSON nulls deserialize over the initializer and would NRE the first consumer
        // (HotkeyService.ReRegister at startup); replace them instead of trusting the values.
        var keyDefaults = HotkeySettings.CreateDefaults();
        foreach (var k in s.Hotkeys.Bindings.Where(kv => kv.Value is null).Select(kv => kv.Key).ToList())
            s.Hotkeys.Bindings[k] = keyDefaults[k];
        foreach (var (k, v) in keyDefaults)
            s.Hotkeys.Bindings.TryAdd(k, v);
        s.Output ??= new OutputSettings();
        s.Capture ??= new CaptureSettings();
        s.Editor ??= new EditorSettings();
        s.Ocr ??= new OcrSettings();
        if (!Enum.IsDefined(s.Ocr.Accuracy)) s.Ocr.Accuracy = OcrAccuracy.Fast;
        s.Translation ??= new TranslationSettings();
        if (string.IsNullOrWhiteSpace(s.Translation.ToCode)) s.Translation.ToCode = "zh";
        s.Editor.Palette ??= [.. new EditorSettings().Palette];
        s.Effects ??= BuiltInPresets.Clean().Settings;
        s.Effects.Shadow ??= new();
        s.Effects.Reflection ??= new();
        s.Effects.Frame ??= new();
        s.UserPresets ??= [];
        foreach (var p in s.UserPresets)
        {
            p.Name ??= "";
            p.Settings ??= new EffectSettings();
            p.Settings.Shadow ??= new();
            p.Settings.Reflection ??= new();
            p.Settings.Frame ??= new();
        }
        s.Output.JpgQuality = Math.Clamp(s.Output.JpgQuality, 1, 100);
        s.Output.WebPQuality = Math.Clamp(s.Output.WebPQuality, 1, 100);
        s.Capture.DelaySeconds = Math.Clamp(s.Capture.DelaySeconds, 1, 60);
        s.Capture.ScrollIntervalMs = Math.Clamp(s.Capture.ScrollIntervalMs, 50, 5000);
        s.Capture.ScrollMaxHeight = Math.Clamp(s.Capture.ScrollMaxHeight, 200, 100000);
        s.HistoryMaxCount = Math.Clamp(s.HistoryMaxCount, 0, 5000);
        s.Theme = s.Theme switch { "Light" or "Dark" => s.Theme, _ => "System" };
        s.Editor.MosaicCellSize = Math.Clamp(s.Editor.MosaicCellSize, 2, 100);
        s.Editor.BlurRadius = Math.Clamp(s.Editor.BlurRadius, 1, 100);
        s.Editor.StrokeThickness = Math.Clamp(s.Editor.StrokeThickness, 1, 60);
        s.Editor.FontSize = Math.Clamp(s.Editor.FontSize, 8, 144);
        s.Editor.StepRadius = Math.Clamp(s.Editor.StepRadius, 4, 100);
        // Effect values are persisted and feed unchecked dimension math downstream; clamps are
        // deliberately wider than the UI sliders so UI-chosen values are never altered.
        var shadow = s.Effects.Shadow;
        shadow.BlurRadius = Math.Clamp(shadow.BlurRadius, 0, 200);
        shadow.Spread = Math.Clamp(shadow.Spread, 0, 100);
        shadow.Distance = Math.Clamp(shadow.Distance, 0, 200);
        shadow.Opacity = Math.Clamp(shadow.Opacity, 0, 1);
        shadow.Angle = ((shadow.Angle % 360) + 360) % 360;
        var reflection = s.Effects.Reflection;
        reflection.Height = Math.Clamp(reflection.Height, 0.05, 1);
        reflection.StartOpacity = Math.Clamp(reflection.StartOpacity, 0, 1);
        reflection.EndOpacity = Math.Clamp(reflection.EndOpacity, 0, 1);
        reflection.Gap = Math.Clamp(reflection.Gap, 0, 200);
        reflection.Blur = Math.Clamp(reflection.Blur, 0, 100);
        var frame = s.Effects.Frame;
        frame.CornerRadius = Math.Clamp(frame.CornerRadius, 0, 500);
        frame.Padding = Math.Clamp(frame.Padding, 0, 500);
        frame.BorderThickness = Math.Clamp(frame.BorderThickness, 0, 100);
        frame.GradientAngle = ((frame.GradientAngle % 360) + 360) % 360;
        s.Version = AppSettings.CurrentVersion;
        return s;
    }

    private void TryBackupCorrupt()
    {
        try
        {
            if (File.Exists(FilePath)) File.Copy(FilePath, FilePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", true);
        }
        catch (IOException) { /* backup is best effort; defaults are still loaded */ }
        catch (UnauthorizedAccessException) { /* same as above */ }
    }
}

public static class AppPaths
{
    public const string AppName = "Modern-ScreenShot";
    public static string RoamingDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppName);
    public static string LocalDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppName);
    public static string HistoryDir => Path.Combine(LocalDir, "History");
    public static string LogDir => Path.Combine(LocalDir, "logs");
    public static string DefaultSaveDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), AppName);
}
