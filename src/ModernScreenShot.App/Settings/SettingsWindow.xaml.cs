using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ModernScreenShot.App.Localization;
using ModernScreenShot.App.Services;
using ModernScreenShot.App.Shell;
using ModernScreenShot.Core.Settings;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Settings;

/// <summary>
/// Tabbed settings window. Control values are written back to the settings store when the window
/// closes (single save point); language / theme / autostart take effect immediately on change.
/// All labels are DynamicResource strings so a language switch updates the open window live;
/// code-built parts (hotkey rows, about texts) refresh via <see cref="LocalizationService.LanguageChanged"/>.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly LocalizationService _localization;
    private readonly HotkeyService? _hotkeys;
    private readonly Action<string>? _notifyConflict;
    private readonly Dictionary<string, HotkeyBinding> _editedBindings;
    private readonly Dictionary<string, TextBlock> _conflictMarks = [];
    private readonly Dictionary<string, TextBlock> _actionLabels = [];
    private readonly Dictionary<string, HotkeyRecorder> _recorders = [];
    private bool _loading = true;
    private bool _wasShown;
    private bool _syncingLanguage;
    private bool _revertingAutostart;

    public SettingsWindow(SettingsStore settings, LocalizationService localization,
        HotkeyService? hotkeys, Action<string>? notifyConflict)
    {
        InitializeComponent();
        _settings = settings;
        _localization = localization;
        _hotkeys = hotkeys;
        _notifyConflict = notifyConflict;
        _editedBindings = settings.Current.Hotkeys.Bindings.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());

        LoadGeneralTab();
        LoadHotkeyTab();
        LoadCaptureTab();
        LoadOutputTab();
        LoadEffectsTab();
        RefreshAboutTexts();
        _loading = false;

        _localization.LanguageChanged += OnLanguageChanged;
        Loaded += (_, _) => _wasShown = true;
        Closed += OnClosed;
    }

    // ---- tab loading ----

    private void LoadGeneralTab()
    {
        LanguageBox.SelectedIndex = _settings.Current.Language switch
        {
            LocalizationService.Chinese => 1,
            LocalizationService.English => 2,
            _ => 0,
        };
        ThemeBox.SelectedIndex = _settings.Current.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
        StartWithWindowsBox.IsChecked = _settings.Current.StartWithWindows;
    }

    private void LoadHotkeyTab()
    {
        foreach (var action in HotkeyActions.All)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = L.Get($"Mode.{action}"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 16, 0),
            };
            var recorder = new HotkeyRecorder();
            recorder.Binding = _editedBindings.GetValueOrDefault(action, new HotkeyBinding());
            var mark = new TextBlock
            {
                Text = L.Get("Settings.Conflict"),
                Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
                Margin = new Thickness(12, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            Grid.SetColumn(label, 0);
            Grid.SetColumn(recorder, 1);
            Grid.SetColumn(mark, 2);
            row.Children.Add(label);
            row.Children.Add(recorder);
            row.Children.Add(mark);
            HotkeyPanel.Children.Add(row);

            _actionLabels[action] = label;
            _recorders[action] = recorder;
            _conflictMarks[action] = mark;
            recorder.BindingChanged += (_, _) =>
            {
                _editedBindings[action] = recorder.Binding;
                RefreshConflicts();
            };
        }
        RefreshConflicts();
    }

    private void LoadCaptureTab()
    {
        var capture = _settings.Current.Capture;
        DelayBox.Text = capture.DelaySeconds.ToString(CultureInfo.InvariantCulture);
        ShowMagnifierBox.IsChecked = capture.ShowMagnifier;
        CaptureCursorBox.IsChecked = capture.CaptureCursor;
        TransparentCornersBox.IsChecked = capture.WindowTransparentCorners;
        HistoryMaxBox.Text = _settings.Current.HistoryMaxCount.ToString(CultureInfo.InvariantCulture);
    }

    private void LoadOutputTab()
    {
        var output = _settings.Current.Output;
        SaveDirBox.Text = output.SaveDirectory;
        FormatBox.SelectedIndex = (int)output.Format;
        JpgQualitySlider.Value = output.JpgQuality;
        WebPQualitySlider.Value = output.WebPQuality;
        JpgQualityValue.Text = output.JpgQuality.ToString(CultureInfo.InvariantCulture);
        WebPQualityValue.Text = output.WebPQuality.ToString(CultureInfo.InvariantCulture);
        TemplateBox.Text = output.FileNameTemplate;
        AutoSaveBox.IsChecked = output.AutoSave;
        AutoCopyBox.IsChecked = output.AutoCopy;
        AfterRegionBox.SelectedIndex = (int)output.AfterRegionCapture;
        AfterOtherBox.SelectedIndex = (int)output.AfterOtherCapture;
    }

    private void LoadEffectsTab() => ApplyEffectsBox.IsChecked = _settings.Current.Output.ApplyEffectsOnExport;

    private void RefreshAboutTexts()
    {
        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = L.Get("Settings.Version", version is null ? "?" : version.ToString(3));
        SettingsPathText.Text = L.Get("Settings.SettingsPath", _settings.FilePath);
    }

    // ---- conflict marking ----

    private void RefreshConflicts()
    {
        // Internal duplicates: the same modifier+key assigned to more than one action.
        var counts = new Dictionary<(int Modifiers, int VirtualKey), int>();
        foreach (var binding in _editedBindings.Values)
        {
            if (binding.IsEmpty) continue;
            var key = (binding.Modifiers, binding.VirtualKey);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        var external = _hotkeys?.FailedActions.ToHashSet() ?? [];
        foreach (var action in HotkeyActions.All)
        {
            var binding = _editedBindings.GetValueOrDefault(action, new HotkeyBinding());
            bool duplicated = !binding.IsEmpty && counts.GetValueOrDefault((binding.Modifiers, binding.VirtualKey)) > 1;
            bool occupied = external.Contains(action);
            _conflictMarks[action].Visibility = duplicated || occupied ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---- event handlers ----

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _syncingLanguage) return;
        string language = LanguageBox.SelectedIndex switch
        {
            1 => LocalizationService.Chinese,
            2 => LocalizationService.English,
            _ => "",
        };
        _settings.Current.Language = language;
        try
        {
            _localization.Apply(language); // raises LanguageChanged, which refreshes this window
        }
        catch (Exception ex)
        {
            Log.Error("Applying the selected language failed", ex);
        }
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string theme = ThemeBox.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };
        _settings.Current.Theme = theme;
        App.ApplyTheme(theme);
    }

    private void OnStartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _revertingAutostart) return;
        bool enable = StartWithWindowsBox.IsChecked == true;
        try
        {
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string valueName = "Modern-ScreenShot";
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            if (enable)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) throw new InvalidOperationException("Process path unavailable.");
                key.SetValue(valueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
            _settings.Current.StartWithWindows = enable;
            Log.Info($"Start with Windows {(enable ? "enabled" : "disabled")}.");
        }
        catch (Exception ex)
        {
            Log.Error("Toggling start-with-Windows failed", ex);
            _revertingAutostart = true;
            StartWithWindowsBox.IsChecked = _settings.Current.StartWithWindows;
            _revertingAutostart = false;
        }
    }

    private void OnQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (JpgQualityValue is null || WebPQualityValue is null) return; // still building the tree
        JpgQualityValue.Text = ((int)JpgQualitySlider.Value).ToString(CultureInfo.InvariantCulture);
        WebPQualityValue.Text = ((int)WebPQualitySlider.Value).ToString(CultureInfo.InvariantCulture);
    }

    private void OnBrowseSaveDir(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L.Get("Settings.SaveDir") };
            string current = SaveDirBox.Text.Trim();
            if (Directory.Exists(current)) dialog.FolderName = current;
            if (dialog.ShowDialog(this) == true) SaveDirBox.Text = dialog.FolderName;
        }
        catch (Exception ex)
        {
            Log.Error("Browsing for the save folder failed", ex);
        }
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Opening the log folder failed", ex);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => Close();

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var action in HotkeyActions.All)
        {
            if (_actionLabels.TryGetValue(action, out var label)) label.Text = L.Get($"Mode.{action}");
            if (_recorders.TryGetValue(action, out var recorder)) recorder.Refresh();
            if (_conflictMarks.TryGetValue(action, out var mark)) mark.Text = L.Get("Settings.Conflict");
        }
        RefreshAboutTexts();
        // The language may have been changed elsewhere (e.g. tray menu) while this window is open;
        // without this sync, OK would write the stale combo value back and revert that choice.
        _syncingLanguage = true;
        LanguageBox.SelectedIndex = _settings.Current.Language switch
        {
            LocalizationService.Chinese => 1,
            LocalizationService.English => 2,
            _ => 0,
        };
        _syncingLanguage = false;
    }

    // ---- persistence ----

    private void OnClosed(object? sender, EventArgs e)
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        if (!_wasShown) return; // never shown (e.g. --smoke): persist nothing

        WriteBack();
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }

        var failed = _hotkeys?.ReRegister() ?? [];
        foreach (var action in failed)
        {
            var binding = _editedBindings.GetValueOrDefault(action, new HotkeyBinding());
            _notifyConflict?.Invoke(L.Get("Toast.HotkeyConflict", HotkeyService.Format(binding)));
        }
    }

    private void WriteBack()
    {
        var s = _settings.Current;
        s.Language = LanguageBox.SelectedIndex switch
        {
            1 => LocalizationService.Chinese,
            2 => LocalizationService.English,
            _ => "",
        };
        s.Theme = ThemeBox.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };
        s.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        s.Hotkeys.Bindings = _editedBindings;
        s.Capture.DelaySeconds = ParseInt(DelayBox.Text, s.Capture.DelaySeconds, 1, 60);
        s.Capture.ShowMagnifier = ShowMagnifierBox.IsChecked == true;
        s.Capture.CaptureCursor = CaptureCursorBox.IsChecked == true;
        s.Capture.WindowTransparentCorners = TransparentCornersBox.IsChecked == true;
        s.HistoryMaxCount = ParseInt(HistoryMaxBox.Text, s.HistoryMaxCount, 0, 5000);
        s.Output.SaveDirectory = SaveDirBox.Text.Trim();
        s.Output.Format = Enum.IsDefined((ImageFormat)FormatBox.SelectedIndex)
            ? (ImageFormat)FormatBox.SelectedIndex : s.Output.Format;
        s.Output.JpgQuality = (int)JpgQualitySlider.Value;
        s.Output.WebPQuality = (int)WebPQualitySlider.Value;
        s.Output.FileNameTemplate = TemplateBox.Text;
        s.Output.AutoSave = AutoSaveBox.IsChecked == true;
        s.Output.AutoCopy = AutoCopyBox.IsChecked == true;
        s.Output.AfterRegionCapture = Enum.IsDefined((AfterCaptureAction)AfterRegionBox.SelectedIndex)
            ? (AfterCaptureAction)AfterRegionBox.SelectedIndex : s.Output.AfterRegionCapture;
        s.Output.AfterOtherCapture = Enum.IsDefined((AfterCaptureAction)AfterOtherBox.SelectedIndex)
            ? (AfterCaptureAction)AfterOtherBox.SelectedIndex : s.Output.AfterOtherCapture;
        s.Output.ApplyEffectsOnExport = ApplyEffectsBox.IsChecked == true;
    }

    private static int ParseInt(string? text, int fallback, int min, int max)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.CurrentCulture, out var value)) return fallback;
        return Math.Clamp(value, min, max);
    }
}
