using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using ModernScreenShot.App.Localization;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Settings;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Shell;

/// <summary>
/// System tray shell: icon with left-click region capture, a capture submenu, a delayed-capture
/// submenu (3/5/10 s), history, settings, language switch and exit. The menu is rebuilt on demand
/// so that a language switch applies immediately. The constructor is side-effect free; call
/// <see cref="Start"/> to create the icon.
/// </summary>
public sealed class TrayService : IDisposable
{
    /// <summary>Actions provided by the app (which owns capture / settings / shutdown lifetimes).</summary>
    public sealed class Actions
    {
        public required Action<CaptureMode> RunCapture { get; init; }
        public required Action<int> RunDelayedCapture { get; init; }
        public required Action OpenHistory { get; init; }
        public required Action OpenSettings { get; init; }
        public required Action Exit { get; init; }
    }

    private readonly SettingsStore _settings;
    private readonly LocalizationService _localization;
    private Actions _actions = null!;
    private TaskbarIcon? _icon;
    private bool _disposed;

    public TrayService(SettingsStore settings, LocalizationService localization)
    {
        _settings = settings;
        _localization = localization;
    }

    /// <summary>Creates the tray icon and menu. Call once from the UI thread.</summary>
    public void Start(Actions actions)
    {
        if (_disposed) return;
        if (_icon is not null) return;
        _actions = actions;

        _icon = new TaskbarIcon
        {
            ToolTipText = L.Get("App.Tooltip"),
            IconSource = LoadIconSource(),
            NoLeftClickDelay = true, // single click acts immediately
        };
        // false = skip the efficiency-mode call: it throws COMException 0x80070001 on some systems.
        _icon.ForceCreate(enablesEfficiencyMode: false);
        _icon.TrayLeftMouseUp += (_, _) => _actions.RunCapture(CaptureMode.Region);
        RebuildMenu();
        _localization.LanguageChanged += OnLanguageChanged;
        Log.Info("Tray icon created.");
    }

    /// <summary>Rebuilds the context menu (also refreshes the tooltip); re-entrant by design.</summary>
    public void RebuildMenu()
    {
        if (_icon is null) return;
        _icon.ToolTipText = L.Get("App.Tooltip");

        var menu = new ContextMenu();

        var capture = new MenuItem { Header = L.Get("Tray.Capture") };
        capture.Items.Add(Item("Tray.Region", () => _actions.RunCapture(CaptureMode.Region)));
        capture.Items.Add(Item("Tray.Fullscreen", () => _actions.RunCapture(CaptureMode.Fullscreen)));
        capture.Items.Add(Item("Tray.AllMonitors", () => _actions.RunCapture(CaptureMode.AllMonitors)));
        capture.Items.Add(Item("Tray.ActiveWindow", () => _actions.RunCapture(CaptureMode.ActiveWindow)));
        capture.Items.Add(Item("Tray.WindowPick", () => _actions.RunCapture(CaptureMode.WindowPick)));
        capture.Items.Add(Item("Tray.LastRegion", () => _actions.RunCapture(CaptureMode.LastRegion)));
        capture.Items.Add(Item("Tray.Scrolling", () => _actions.RunCapture(CaptureMode.Scrolling)));
        menu.Items.Add(capture);

        var delay = new MenuItem { Header = L.Get("Tray.Delay") };
        delay.Items.Add(Item("Tray.Delay3", () => _actions.RunDelayedCapture(3)));
        delay.Items.Add(Item("Tray.Delay5", () => _actions.RunDelayedCapture(5)));
        delay.Items.Add(Item("Tray.Delay10", () => _actions.RunDelayedCapture(10)));
        menu.Items.Add(delay);

        menu.Items.Add(Item("Tray.History", _actions.OpenHistory));
        menu.Items.Add(Item("Tray.Settings", _actions.OpenSettings));

        var language = new MenuItem { Header = L.Get("Tray.Language") };
        AddLanguageItem(language, "Tray.LangSystem", "", _localization.RequestedLanguage);
        AddLanguageItem(language, "Tray.LangChinese", LocalizationService.Chinese, _localization.RequestedLanguage);
        AddLanguageItem(language, "Tray.LangEnglish", LocalizationService.English, _localization.RequestedLanguage);
        menu.Items.Add(language);

        menu.Items.Add(Item("Tray.Exit", _actions.Exit));
        _icon.ContextMenu = menu;
    }

    /// <summary>Shows a tray balloon (used for the first-run hint and hotkey conflicts).</summary>
    public void ShowNotification(string title, string body)
    {
        try
        {
            if (_icon is { IsCreated: true }) _icon.ShowNotification(title, body);
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray notification failed: {ex.Message}");
        }
    }

    private MenuItem Item(string key, Action onClick)
    {
        var item = new MenuItem { Header = L.Get(key) };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void AddLanguageItem(MenuItem parent, string labelKey, string language, string requested)
    {
        var item = new MenuItem
        {
            Header = L.Get(labelKey),
            IsCheckable = true,
            IsChecked = string.Equals(requested, language, StringComparison.Ordinal),
        };
        item.Click += (_, _) => ApplyLanguage(language);
        parent.Items.Add(item);
    }

    private void ApplyLanguage(string language)
    {
        try
        {
            _settings.Current.Language = language;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Persisting the tray language switch failed", ex);
        }
        // LanguageChanged rebuilds the menu; Apply persists nothing by itself.
        _localization.Apply(language);
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        // Always on the UI thread; BeginInvoke keeps the menu rebuild out of the Apply call stack.
        Application.Current?.Dispatcher.BeginInvoke(RebuildMenu);
    }

    private static BitmapFrame? LoadIconSource()
    {
        try
        {
            return BitmapFrame.Create(new Uri("pack://application:,,,/ModernScreenShot.App;component/Assets/app.ico", UriKind.Absolute));
        }
        catch (Exception ex)
        {
            Log.Warn($"Tray icon resource could not be loaded: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _localization.LanguageChanged -= OnLanguageChanged;
        _icon?.Dispose(); // removes the tray icon; without this it lingers until hover
        _icon = null;
        Log.Info("Tray icon disposed.");
    }
}
