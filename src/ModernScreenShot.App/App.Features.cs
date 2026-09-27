using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Output;
using ModernScreenShot.Core.Settings;

namespace ModernScreenShot.App;

/// <summary>
/// Feature wiring. Later tasks register their services and start-up logic here so App.xaml.cs stays stable.
/// </summary>
public partial class App
{
    /// <summary>Set by the shell module (T8). While false, the app exits after a one-shot --capture run.</summary>
    private bool ShellStarted { get; set; }

    partial void RegisterFeatureServices(IServiceCollection services)
    {
        services.AddSingleton<CaptureService>();
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
        if (!ShellStarted) Shutdown(0);
    }

    partial void OnSmokeTest(IServiceProvider services)
    {
        _ = services.GetRequiredService<CaptureService>();
        Log.Info("smoke: CaptureService resolved (overlay windows are intentionally not instantiated).");
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
    /// other modes follow the settings. Editor (T5) and pin (T7) fall back to copying until implemented.
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
                Log.Info("Editor is not implemented yet (T5); copying to clipboard instead.");
                CopyToClipboard(result);
                break;
            case AfterCaptureAction.Pin:
                Log.Info("Pin window is not implemented yet (T7); copying to clipboard instead.");
                CopyToClipboard(result);
                break;
            default: // CopyOnly; ShowToolbar is handled by the overlay itself, which always supplies an intent
                CopyToClipboard(result);
                break;
        }
    }

    private static void CopyToClipboard(CaptureResult result)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Clipboard.SetImage(result.Image.ToBitmapSource());
                Log.Info("Capture copied to clipboard.");
                return;
            }
            catch (ExternalException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
            catch (Exception ex)
            {
                Log.Error("Clipboard copy failed", ex);
                return;
            }
        }
    }

    private void QuickSave(CaptureResult result, SettingsStore store)
    {
        try
        {
            var output = store.Current.Output;
            string dir = string.IsNullOrWhiteSpace(output.SaveDirectory) ? AppPaths.DefaultSaveDir : output.SaveDirectory;
            string baseName = FileNameTemplate.Format(output.FileNameTemplate, result.Time, output.Counter,
                result.WindowTitle, result.Mode.ToString());
            string path = FileNameTemplate.GetUniquePath(dir, baseName, ".png");
            BitmapInterop.SavePng(result.Image, path);
            output.Counter++;
            store.Save();
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
