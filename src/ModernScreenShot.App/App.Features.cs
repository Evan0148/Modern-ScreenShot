using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Editor;
using ModernScreenShot.App.Localization;
using ModernScreenShot.App.Output;
using ModernScreenShot.App.Overlay;
using ModernScreenShot.App.Services;
using ModernScreenShot.App.Settings;
using ModernScreenShot.App.Shell;
using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.History;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Output;
using ModernScreenShot.Core.Settings;
using Backdrop = Wpf.Ui.Controls.WindowBackdropType;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App;

/// <summary>
/// Feature wiring. Later tasks register their services and start-up logic here so App.xaml.cs stays stable.
/// </summary>
public partial class App
{
    /// <summary>True once the tray shell is running; then the app stays resident.</summary>
    private bool TrayStarted { get; set; }

    private readonly List<Window> EditorWindows = [];

    private SingleInstance? _singleInstance;
    private HotkeyService? _hotkeys;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;

    partial void RegisterFeatureServices(IServiceCollection services)
    {
        services.AddSingleton<ScrollingCaptureService>();
        services.AddSingleton<CaptureService>();
        services.AddSingleton<ClipboardService>();
        services.AddSingleton<ImageExporter>();
        services.AddSingleton<HistoryRecorder>();
        // Shell services: constructors are side-effect free; Start() is called explicitly below.
        services.AddSingleton<SingleInstance>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TrayService>();
    }

    partial void OnStartupCompleted(string[] args)
    {
        // Single instance: when another instance already owns the mutex, hand it the command line
        // and exit without touching anything else.
        var single = Services.GetRequiredService<SingleInstance>();
        if (!single.TryStart(OnForwardedArguments))
        {
            if (!SingleInstance.TryForward(args))
                Log.Warn("Another instance is running but the arguments could not be forwarded.");
            Shutdown(0);
            return;
        }

        try
        {
            StartShell(single);
        }
        catch (Exception ex)
        {
            Log.Error("Tray shell startup failed", ex);
            if (ParseCaptureArg(args) is null)
            {
                Shutdown(1);
                return;
            }
            // With an explicit capture request the capture itself may still work; fall through.
        }

        if (ParseCaptureArg(args) is { } mode) RunCapture(mode);
        if (!TrayStarted && EditorWindows.Count == 0) Shutdown(0);
    }

    partial void OnSmokeTest(IServiceProvider services)
    {
        _ = services.GetRequiredService<ScrollingCaptureService>();
        _ = services.GetRequiredService<CaptureService>();
        _ = services.GetRequiredService<ClipboardService>();
        _ = services.GetRequiredService<ImageExporter>();
        // Shell services resolve without side effects (tray icon / hotkeys / mutex start explicitly).
        _ = services.GetRequiredService<SingleInstance>();
        _ = services.GetRequiredService<HotkeyService>();
        _ = services.GetRequiredService<TrayService>();
        // Instantiate the editor off-screen to catch XAML/ctor regressions in both languages.
        var testResult = new CaptureResult
        {
            Image = new PixelBuffer(64, 48),
            Mode = CaptureMode.Region,
            SourceRect = new PixelRect(0, 0, 64, 48),
        };
        var editor = new EditorWindow(testResult, services.GetRequiredService<SettingsStore>(),
            services.GetRequiredService<ClipboardService>(), services.GetRequiredService<ImageExporter>());
        editor.Close();
        var history = new HistoryWindow(services.GetRequiredService<HistoryStore>(),
            services.GetRequiredService<SettingsStore>(), services.GetRequiredService<ClipboardService>(),
            (_, _) => { });
        history.Close();
        var settings = new SettingsWindow(services.GetRequiredService<SettingsStore>(),
            services.GetRequiredService<LocalizationService>(),
            services.GetRequiredService<HotkeyService>(), _ => { });
        settings.Close();
        var countdown = new CountdownWindow();
        countdown.Close();
        Log.Info("smoke: shell services, SettingsWindow and CountdownWindow instantiated (nothing shown).");
    }

    // ---- shell ----

    private void StartShell(SingleInstance single)
    {
        var hotkeys = Services.GetRequiredService<HotkeyService>();
        hotkeys.HotkeyPressed += OnHotkeyAction;
        hotkeys.Start();

        var tray = Services.GetRequiredService<TrayService>();
        tray.Start(new TrayService.Actions
        {
            RunCapture = RunCapture,
            RunDelayedCapture = RunDelayedCapture,
            OpenHistory = OpenHistory,
            OpenSettings = OpenSettings,
            Exit = ExitApp,
        });

        _singleInstance = single;
        _hotkeys = hotkeys;
        _tray = tray;
        TrayStarted = true;

        ApplyTheme(Services.GetRequiredService<SettingsStore>().Current.Theme);
        ShowFirstRunNotice();
        Application.Current.Exit += OnAppExitCleanup;
        Log.Info("Tray shell started (tray icon, hotkeys, single-instance listener).");
    }

    /// <summary>Applies the stored theme ("System"/"Light"/"Dark") via WPF-UI.</summary>
    internal static void ApplyTheme(string theme)
    {
        try
        {
            var appTheme = theme switch
            {
                "Light" => Wpf.Ui.Appearance.ApplicationTheme.Light,
                "Dark" => Wpf.Ui.Appearance.ApplicationTheme.Dark,
                _ => Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme() == Wpf.Ui.Appearance.SystemTheme.Dark
                    ? Wpf.Ui.Appearance.ApplicationTheme.Dark
                    : Wpf.Ui.Appearance.ApplicationTheme.Light,
            };
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(appTheme, Backdrop.None, false);
            Log.Info($"Theme applied: '{theme}' -> {appTheme}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Theme '{theme}' could not be applied: {ex.Message}");
        }
    }

    private void ShowFirstRunNotice()
    {
        var store = Services.GetRequiredService<SettingsStore>();
        if (store.Current.FirstRunShown) return;
        _tray?.ShowNotification(L.Get("FirstRun.Title"), L.Get("FirstRun.Body"));
        store.Current.FirstRunShown = true;
        try
        {
            store.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Persisting FirstRunShown failed", ex);
        }
        Log.Info("First run notice shown.");
    }

    private void OnForwardedArguments(string[] args)
    {
        if (ParseCaptureArg(args) is not { } mode)
        {
            Log.Info("Forwarded arguments contained no capture mode; ignored.");
            return;
        }
        RunCapture(mode);
    }

    private void OnHotkeyAction(string action)
    {
        if (action == HotkeyActions.History)
        {
            OpenHistory();
            return;
        }
        CaptureMode? mode = action switch
        {
            HotkeyActions.Region => CaptureMode.Region,
            HotkeyActions.Fullscreen => CaptureMode.Fullscreen,
            HotkeyActions.AllMonitors => CaptureMode.AllMonitors,
            HotkeyActions.ActiveWindow => CaptureMode.ActiveWindow,
            HotkeyActions.WindowPick => CaptureMode.WindowPick,
            HotkeyActions.DelayRegion => CaptureMode.DelayRegion,
            HotkeyActions.LastRegion => CaptureMode.LastRegion,
            HotkeyActions.Scrolling => CaptureMode.Scrolling,
            _ => null,
        };
        if (mode is { } m) RunCapture(m);
        else Log.Warn($"Unknown hotkey action '{action}'.");
    }

    /// <summary>Tray delay submenu: countdown first (3/5/10 s), then a region capture.</summary>
    private void RunDelayedCapture(int seconds)
    {
        if (CountdownWindow.Run(seconds, Services.GetRequiredService<MonitorService>()))
            RunCapture(CaptureMode.Region);
        else
            Log.Info($"Delayed capture ({seconds}s) cancelled.");
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(Services.GetRequiredService<SettingsStore>(),
            Services.GetRequiredService<LocalizationService>(), _hotkeys,
            message => _tray?.ShowNotification(L.Get("Settings.Title"), message));
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
        Log.Info("Settings window opened.");
    }

    private void ExitApp()
    {
        Log.Info("Exit requested from the tray menu.");
        _tray?.Dispose(); // remove the tray icon before shutdown so it never lingers
        Application.Current.Shutdown();
    }

    private void OnAppExitCleanup(object sender, ExitEventArgs e)
    {
        // Idempotent: the service provider also disposes these singletons afterwards.
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _singleInstance?.Dispose();
    }

    private void RunCapture(CaptureMode mode)
    {
        try
        {
            var capture = Services.GetRequiredService<CaptureService>();
            var result = capture.Capture(mode);
            if (result is null)
            {
                Log.Info($"Capture '{mode}' was cancelled or produced no image.");
                return;
            }
            DispatchCaptureResult(result);
        }
        catch (Exception ex)
        {
            Log.Error($"Capture '{mode}' failed", ex);
        }
    }

    /// <summary>
    /// Post-capture routing. Every confirmed capture is recorded in history (original + document +
    /// thumbnail); then the action from the overlay toolbar or the settings is executed.
    /// </summary>
    private void DispatchCaptureResult(CaptureResult result)
    {
        var store = Services.GetRequiredService<SettingsStore>();
        var output = store.Current.Output;
        var action = result.RequestedAction
            ?? (result.Mode is CaptureMode.Region or CaptureMode.WindowPick
                ? output.AfterRegionCapture
                : output.AfterOtherCapture);
        Log.Info($"Dispatching {result.Mode} capture {result.Image.Width}x{result.Image.Height} via {action}.");

        var doc = new AnnotationDocument
        {
            ImageWidth = result.Image.Width,
            ImageHeight = result.Image.Height,
            Effects = store.Current.Effects.Clone(),
            WindowTitle = result.WindowTitle,
            CaptureMode = result.Mode.ToString(),
        };
        Services.GetRequiredService<HistoryRecorder>().Record(result.Image, doc);

        switch (action)
        {
            case AfterCaptureAction.SaveOnly:
                QuickSave(result, store);
                break;
            case AfterCaptureAction.OpenEditor:
                OpenEditor(result, doc);
                break;
            case AfterCaptureAction.Pin:
                PinCapture(result);
                break;
            default: // CopyOnly; ShowToolbar is handled by the overlay itself, which always supplies an intent
                CopyToClipboard(result);
                break;
        }
    }

    private void OpenEditor(CaptureResult result, AnnotationDocument? document = null)
    {
        var editor = new EditorWindow(result, Services.GetRequiredService<SettingsStore>(),
            Services.GetRequiredService<ClipboardService>(), Services.GetRequiredService<ImageExporter>(),
            document,
            image => new PinWindow(image, Services.GetRequiredService<ImageExporter>(),
                Services.GetRequiredService<ClipboardService>(), OpenEditorForImage, OpenDefaultSaveFolder));
        editor.Closed += OnEditorClosed;
        EditorWindows.Add(editor);
        editor.Show();
        editor.Activate();
        Log.Info($"Editor opened for {result.Mode} capture ({result.Image.Width}x{result.Image.Height}).");
    }

    private void OpenEditorForImage(PixelBuffer image)
    {
        var result = new CaptureResult
        {
            Image = image,
            Mode = CaptureMode.Region,
            SourceRect = new PixelRect(0, 0, image.Width, image.Height),
        };
        OpenEditor(result);
    }

    private void PinCapture(CaptureResult result)
    {
        var pin = new PinWindow(result.Image, Services.GetRequiredService<ImageExporter>(),
            Services.GetRequiredService<ClipboardService>(), OpenEditorForImage, OpenDefaultSaveFolder);
        pin.Closed += OnEditorClosed;
        EditorWindows.Add(pin); // same lifetime rule: keep the app alive while any floating window exists
        pin.Show();
        pin.Activate();
        Log.Info($"Pinned {result.Image.Width}x{result.Image.Height} to screen.");
    }

    private static string OpenDefaultSaveFolder()
    {
        try
        {
            var store = Services.GetRequiredService<SettingsStore>();
            string dir = string.IsNullOrWhiteSpace(store.Current.Output.SaveDirectory)
                ? AppPaths.DefaultSaveDir
                : store.Current.Output.SaveDirectory;
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            return dir;
        }
        catch (Exception ex)
        {
            Log.Error("Opening the save folder failed", ex);
            return AppPaths.DefaultSaveDir;
        }
    }

    /// <summary>Opens the history window (tray menu / hotkey).</summary>
    private void OpenHistory()
    {
        var win = new HistoryWindow(Services.GetRequiredService<HistoryStore>(),
            Services.GetRequiredService<SettingsStore>(), Services.GetRequiredService<ClipboardService>(),
            (result, doc) => OpenEditor(result, doc));
        win.Closed += OnEditorClosed;
        EditorWindows.Add(win);
        win.Show();
        win.Activate();
        Log.Info("History window opened.");
    }

    private void OnEditorClosed(object? sender, EventArgs e)
    {
        if (sender is Window w) EditorWindows.Remove(w);
        if (EditorWindows.Count == 0 && !TrayStarted) Shutdown(0);
    }

    private static void CopyToClipboard(CaptureResult result)
    {
        if (Services.GetRequiredService<ClipboardService>().TryPutImage(result.Image))
            Log.Info("Capture copied to clipboard.");
    }

    private void QuickSave(CaptureResult result, SettingsStore store)
    {
        try
        {
            var path = Services.GetRequiredService<ImageExporter>().QuickSave(result.Image, result.WindowTitle, result.Mode.ToString());
            Log.Info($"Capture saved to {path}");
        }
        catch (Exception ex)
        {
            Log.Error("Quick save failed", ex);
        }
    }

    private static CaptureMode? ParseCaptureArg(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--capture", StringComparison.OrdinalIgnoreCase)) continue;
            return args[i + 1] switch
            {
                "region" => CaptureMode.Region,
                "fullscreen" => CaptureMode.Fullscreen,
                "all" => CaptureMode.AllMonitors,
                "active" => CaptureMode.ActiveWindow,
                "window" => CaptureMode.WindowPick,
                "last" => CaptureMode.LastRegion,
                "scroll" => CaptureMode.Scrolling,
                "delay" => CaptureMode.DelayRegion,
                _ => null,
            };
        }
        return null;
    }
}
