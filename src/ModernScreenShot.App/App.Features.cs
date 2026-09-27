using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Editor;
using ModernScreenShot.App.Output;
using ModernScreenShot.App.Services;
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
        Log.Info("smoke: CaptureService and EditorWindow resolved/instantiated (overlay is intentionally not shown).");
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
    /// Post-capture routing. The overlay toolbar supplies an explicit action for region/window picks;
    /// other modes follow the settings. Pin (T7) falls back to the editor until implemented.
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
        switch (action)
        {
            case AfterCaptureAction.SaveOnly:
                QuickSave(result, store);
                break;
            case AfterCaptureAction.OpenEditor:
            case AfterCaptureAction.Pin:
                if (action == AfterCaptureAction.Pin) Log.Info("Pin window is not implemented yet (T7); opening the editor instead.");
                OpenEditor(result);
                break;
            default: // CopyOnly; ShowToolbar is handled by the overlay itself, which always supplies an intent
                CopyToClipboard(result);
                break;
        }
    }

    private void OpenEditor(CaptureResult result)
    {
        var editor = new EditorWindow(result, Services.GetRequiredService<SettingsStore>(),
            Services.GetRequiredService<ClipboardService>(), Services.GetRequiredService<ImageExporter>());
        editor.Closed += OnEditorClosed;
        EditorWindows.Add(editor);
        editor.Show();
        editor.Activate();
        Log.Info($"Editor opened for {result.Mode} capture ({result.Image.Width}x{result.Image.Height}).");
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
