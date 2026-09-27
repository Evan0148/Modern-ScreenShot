using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Editor;
using ModernScreenShot.App.Output;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.History;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Output;
using ModernScreenShot.Core.Settings;

namespace ModernScreenShot.App;

/// <summary>
/// Feature wiring. Later tasks register their services and start-up logic here so App.xaml.cs stays stable.
/// </summary>
public partial class App
{
    /// <summary>True once the tray shell (T8) is running; then the app stays resident.</summary>
    private bool TrayStarted { get; set; }

    private readonly List<Window> EditorWindows = [];

    partial void RegisterFeatureServices(IServiceCollection services)
    {
        services.AddSingleton<CaptureService>();
        services.AddSingleton<ClipboardService>();
        services.AddSingleton<ImageExporter>();
        services.AddSingleton<HistoryRecorder>();
    }

    partial void OnStartupCompleted(string[] args)
    {
        if (ParseCaptureArg(args) is { } mode)
        {
            RunCapture(mode);
        }
        else
        {
            Log.Info("No capture request; shell features (tray/hotkeys) arrive with T8.");
        }
        if (!TrayStarted && EditorWindows.Count == 0) Shutdown(0);
    }

    partial void OnSmokeTest(IServiceProvider services)
    {
        _ = services.GetRequiredService<CaptureService>();
        _ = services.GetRequiredService<ClipboardService>();
        _ = services.GetRequiredService<ImageExporter>();
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
        Log.Info("smoke: CaptureService, EditorWindow and HistoryWindow instantiated (overlay not shown).");
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

    /// <summary>Opens the history window (tray menu / hotkey land here in T8).</summary>
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
