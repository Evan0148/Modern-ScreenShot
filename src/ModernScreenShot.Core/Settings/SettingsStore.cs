using System.Text.Json;

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
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
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
        foreach (var (k, v) in HotkeySettings.CreateDefaults())
            s.Hotkeys.Bindings.TryAdd(k, v);
        s.Output ??= new OutputSettings();
        s.Capture ??= new CaptureSettings();
        s.Editor ??= new EditorSettings();
        s.Effects ??= BuiltInPresets.Clean().Settings;
        s.Effects.Shadow ??= new();
        s.Effects.Reflection ??= new();
        s.Effects.Frame ??= new();
        s.UserPresets ??= [];
        s.Output.JpgQuality = Math.Clamp(s.Output.JpgQuality, 1, 100);
        s.Output.WebPQuality = Math.Clamp(s.Output.WebPQuality, 1, 100);
        s.Capture.DelaySeconds = Math.Clamp(s.Capture.DelaySeconds, 1, 60);
        s.HistoryMaxCount = Math.Clamp(s.HistoryMaxCount, 0, 5000);
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
