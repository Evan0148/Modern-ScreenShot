using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Overlay;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Settings;

namespace ModernScreenShot.App.Capture;

/// <summary>Orchestrates capture modes on the UI thread and produces <see cref="CaptureResult"/>s.</summary>
public sealed class CaptureService
{
    private readonly MonitorService _monitors;
    private readonly ScreenCapturer _screen;
    private readonly WindowCapturer _windowCapturer;
    private readonly WindowEnumerator _windows;
    private readonly SettingsStore _settings;

    public CaptureService(MonitorService monitors, ScreenCapturer screen, WindowCapturer windowCapturer,
        WindowEnumerator windows, SettingsStore settings)
    {
        _monitors = monitors;
        _screen = screen;
        _windowCapturer = windowCapturer;
        _windows = windows;
        _settings = settings;
    }

    /// <summary>
    /// Runs a capture on the calling (UI) thread. Returns null when the user cancels or when the
    /// mode is not available yet (scrolling capture arrives with T9). DelayRegion shows the
    /// countdown bubble first and continues as a region capture when it is not cancelled.
    /// </summary>
    public CaptureResult? Capture(CaptureMode mode)
    {
        try
        {
            if (mode == CaptureMode.DelayRegion)
            {
                if (!CountdownWindow.Run(_settings.Current.Capture.DelaySeconds, _monitors))
                {
                    Log.Info("Delayed capture cancelled during the countdown.");
                    return null;
                }
                mode = CaptureMode.Region;
            }
            return mode switch
            {
                CaptureMode.Region or CaptureMode.WindowPick => CaptureViaOverlay(mode),
                CaptureMode.Fullscreen => CaptureScreen(_monitors.GetCursorMonitor().Bounds, CaptureMode.Fullscreen),
                CaptureMode.AllMonitors => CaptureScreen(_monitors.GetVirtualScreen(), CaptureMode.AllMonitors),
                CaptureMode.ActiveWindow => CaptureActiveWindow(),
                CaptureMode.LastRegion => CaptureLastRegion(),
                CaptureMode.Scrolling => null,
                _ => null,
            };
        }
        catch (Exception ex)
        {
            Log.Error($"Capture mode {mode} failed", ex);
            return null;
        }
    }

    private CaptureResult? CaptureViaOverlay(CaptureMode mode)
    {
        var vs = _monitors.GetVirtualScreen();
        if (vs.IsEmpty) return null;
        // Freeze BEFORE any overlay is visible so the shot can never contain the overlay itself.
        // (Cursor is intentionally excluded here; cursor capture only applies to direct modes.)
        var frozen = _screen.Capture(vs, includeCursor: false);
        _windows.Refresh(); // snapshot before overlay windows exist
        var session = new OverlaySession(mode, frozen, vs, frozen.ToBitmapSource(), _monitors, _windows,
            _settings.Current.Capture.ShowMagnifier);
        var outcome = session.Show();
        if (outcome is null || !outcome.Confirmed)
        {
            Log.Info($"Overlay session '{mode}' cancelled.");
            return null;
        }

        if (mode == CaptureMode.Region && !outcome.Region.IsEmpty)
        {
            _settings.Current.Capture.LastRegion =
                [outcome.Region.X, outcome.Region.Y, outcome.Region.Width, outcome.Region.Height];
            _settings.Save();
        }

        if (outcome.WindowHandle != IntPtr.Zero)
        {
            var image = _windowCapturer.CaptureWindow(outcome.WindowHandle, _settings.Current.Capture.WindowTransparentCorners, out var title);
            if (image is not null)
            {
                return new CaptureResult
                {
                    Image = image,
                    Mode = CaptureMode.WindowPick,
                    WindowTitle = title,
                    SourceRect = outcome.PickedWindow?.Bounds ?? outcome.Region,
                    RequestedAction = MapIntent(outcome.Intent),
                };
            }
            Log.Warn("Window capture failed after picking; falling back to the frozen region crop.");
        }

        if (outcome.Region.IsEmpty) return null;
        var cropRect = outcome.Region.Offset(-vs.X, -vs.Y);
        return new CaptureResult
        {
            Image = frozen.Crop(cropRect),
            Mode = mode,
            WindowTitle = outcome.PickedWindow?.Title,
            SourceRect = outcome.Region,
            RequestedAction = MapIntent(outcome.Intent),
        };
    }

    private CaptureResult CaptureScreen(PixelRect rect, CaptureMode mode)
    {
        var image = _screen.Capture(rect, _settings.Current.Capture.CaptureCursor);
        return new CaptureResult { Image = image, Mode = mode, SourceRect = rect };
    }

    private CaptureResult? CaptureActiveWindow()
    {
        var image = _windowCapturer.CaptureActiveWindow(_settings.Current.Capture.WindowTransparentCorners,
            out var title, out var bounds, out _);
        if (image is null)
        {
            Log.Warn("Active window capture returned no image.");
            return null;
        }
        return new CaptureResult { Image = image, Mode = CaptureMode.ActiveWindow, WindowTitle = title, SourceRect = bounds };
    }

    private CaptureResult? CaptureLastRegion()
    {
        var last = _settings.Current.Capture.LastRegion;
        if (last is not { Length: 4 })
        {
            Log.Warn("No last region stored; LastRegion capture ignored.");
            return null;
        }
        return CaptureScreen(new PixelRect(last[0], last[1], last[2], last[3]), CaptureMode.LastRegion);
    }

    private static AfterCaptureAction MapIntent(OverlayIntent intent) => intent switch
    {
        OverlayIntent.Edit => AfterCaptureAction.OpenEditor,
        OverlayIntent.Copy => AfterCaptureAction.CopyOnly,
        OverlayIntent.Save => AfterCaptureAction.SaveOnly,
        OverlayIntent.Pin => AfterCaptureAction.Pin,
        _ => AfterCaptureAction.CopyOnly,
    };
}
