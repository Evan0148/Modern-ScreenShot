using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Overlay;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Annotation;
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
    private readonly ScrollingCaptureService _scrolling;

    public CaptureService(MonitorService monitors, ScreenCapturer screen, WindowCapturer windowCapturer,
        WindowEnumerator windows, SettingsStore settings, ScrollingCaptureService scrolling)
    {
        _monitors = monitors;
        _screen = screen;
        _windowCapturer = windowCapturer;
        _windows = windows;
        _settings = settings;
        _scrolling = scrolling;
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
                CaptureMode.Scrolling => CaptureScrolling(),
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
            _settings.Current.Capture.ShowMagnifier, _settings.Current.Editor,
            persistEditorOptions: () => _settings.Save());
        session.Doc.Effects = _settings.Current.Effects.Clone();
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
            try
            {
                _settings.Save();
            }
            catch (Exception ex)
            {
                // Losing the persisted region must not abort the capture the user just confirmed.
                Log.Warn($"Persisting LastRegion failed: {ex.Message}");
            }
        }

        AnnotationDocument? doc = null;
        if (outcome.AnnotationDocument is { } inlineDoc)
        {
            inlineDoc.WindowTitle = outcome.PickedWindow?.Title;
            inlineDoc.CaptureMode = mode.ToString();
            doc = inlineDoc;
        }

        if (outcome.WindowHandle != IntPtr.Zero)
        {
            // macOS-style shot needs the transparent rounded corners regardless of the corners toggle.
            bool macStyle = _settings.Current.Capture.MacStyleWindowShadow;
            bool transparentCorners = _settings.Current.Capture.WindowTransparentCorners || macStyle;
            var image = _windowCapturer.CaptureWindow(outcome.WindowHandle, transparentCorners, out var title);
            if (image is not null)
            {
                var windowDoc = ApplyMacStyleIfEnabled(doc, image, CaptureMode.WindowPick, title);
                return new CaptureResult
                {
                    Image = image,
                    Mode = CaptureMode.WindowPick,
                    WindowTitle = title,
                    SourceRect = outcome.PickedWindow?.Bounds ?? outcome.Region,
                    RequestedAction = MapIntent(outcome.Intent),
                    AnnotationDocument = windowDoc,
                    BakeEffectsOnDirectOutput = macStyle,
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
            AnnotationDocument = doc,
        };
    }

    /// <summary>
    /// Scrolling (long) capture: first a region is picked with the auto-confirming overlay, then the
    /// region is scrolled and stitched live by <see cref="ScrollingCaptureService"/>.
    /// </summary>
    private CaptureResult? CaptureScrolling()
    {
        var region = SelectScrollingRegion();
        if (region is not { } selected || selected.IsEmpty)
        {
            Log.Info("Scrolling capture cancelled: no region selected.");
            return null;
        }
        return _scrolling.Run(selected);
    }

    /// <summary>Shows the region overlay in auto-confirm mode: dragging a region (or clicking a
    /// window) confirms immediately with Edit intent; the toolbar is hidden; Esc cancels.</summary>
    private PixelRect? SelectScrollingRegion()
    {
        var vs = _monitors.GetVirtualScreen();
        if (vs.IsEmpty) return null;
        // Freeze BEFORE any overlay is visible so the frozen slice can never contain the overlay itself.
        var frozen = _screen.Capture(vs, includeCursor: false);
        _windows.Refresh(); // snapshot before overlay windows exist
        var session = new OverlaySession(CaptureMode.Region, frozen, vs, frozen.ToBitmapSource(), _monitors,
            _windows, _settings.Current.Capture.ShowMagnifier, _settings.Current.Editor, autoConfirmOnSelect: true,
            persistEditorOptions: () => _settings.Save());
        var outcome = session.Show();
        if (outcome is null || !outcome.Confirmed || outcome.Region.IsEmpty) return null;
        return outcome.Region;
    }

    private CaptureResult CaptureScreen(PixelRect rect, CaptureMode mode)
    {
        var image = _screen.Capture(rect, _settings.Current.Capture.CaptureCursor);
        return new CaptureResult { Image = image, Mode = mode, SourceRect = rect };
    }

    private CaptureResult? CaptureActiveWindow()
    {
        bool macStyle = _settings.Current.Capture.MacStyleWindowShadow;
        bool transparentCorners = _settings.Current.Capture.WindowTransparentCorners || macStyle;
        var image = _windowCapturer.CaptureActiveWindow(transparentCorners,
            out var title, out var bounds, out _);
        if (image is null)
        {
            Log.Warn("Active window capture returned no image.");
            return null;
        }
        var doc = ApplyMacStyleIfEnabled(null, image, CaptureMode.ActiveWindow, title);
        return new CaptureResult
        {
            Image = image,
            Mode = CaptureMode.ActiveWindow,
            WindowTitle = title,
            SourceRect = bounds,
            AnnotationDocument = doc,
            BakeEffectsOnDirectOutput = macStyle,
        };
    }

    /// <summary>
    /// When macOS-style window shadow is enabled, returns an <see cref="AnnotationDocument"/> whose
    /// <see cref="AnnotationDocument.Effects"/> is a fresh SoftFloat preset (soft shadow + transparent
    /// surround). Existing inline annotations are preserved; only their Effects are set. This does NOT
    /// touch the persisted <c>store.Current.Effects</c>, so a user's effect preference for ordinary
    /// captures is never overwritten. When disabled, the caller's document passes through unchanged.
    /// </summary>
    private AnnotationDocument? ApplyMacStyleIfEnabled(AnnotationDocument? doc, PixelBuffer image, CaptureMode mode, string? title)
    {
        if (!_settings.Current.Capture.MacStyleWindowShadow) return doc;
        doc ??= new AnnotationDocument
        {
            ImageWidth = image.Width,
            ImageHeight = image.Height,
            WindowTitle = title,
            CaptureMode = mode.ToString(),
        };
        var effects = BuiltInPresets.SoftFloat().Settings;
        effects.Enabled = true;
        doc.Effects = effects;
        return doc;
    }

    private CaptureResult? CaptureLastRegion()
    {
        var last = _settings.Current.Capture.LastRegion;
        if (last is not { Length: 4 })
        {
            Log.Warn("No last region stored; LastRegion capture ignored.");
            return null;
        }
        var rect = new PixelRect(last[0], last[1], last[2], last[3]);
        // Monitor topology may have changed since the region was stored; BitBlt of off-screen
        // coordinates silently yields black pixels, so clip to the current virtual screen.
        var clipped = rect.Intersect(_monitors.GetVirtualScreen());
        if (clipped.IsEmpty)
        {
            Log.Warn($"Stored last region {rect} is outside the current monitors; LastRegion capture ignored.");
            return null;
        }
        if (clipped != rect) Log.Warn($"Stored last region clipped to the current monitors: {clipped}.");
        return CaptureScreen(clipped, CaptureMode.LastRegion);
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
