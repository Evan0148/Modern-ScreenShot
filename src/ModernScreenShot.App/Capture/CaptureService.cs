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
    /// Runs a capture on the calling (UI) thread. Returns null when the user cancels. A hard
    /// pipeline failure (GDI/BitBlt/monitor errors) throws so the single caller (RunCaptureCore)
    /// can tell it apart from a cancel and surface the failure toast — swallowing it here made
    /// real failures indistinguishable from the user pressing Esc. DelayRegion shows the countdown
    /// bubble first and continues as a region capture when it is not cancelled.
    /// </summary>
    public CaptureResult? Capture(CaptureMode mode)
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
            persistEditorOptions: () => _settings.Save(),
            autoElementDetection: _settings.Current.Capture.AutoElementDetection,
            capturePriority: _settings.Current.Capture.CapturePriority && ElevationService.IsElevated,
            refreshFrozen: toggleCursor => RefreshFrozenFrame(vs, toggleCursor),
            lastRegion: GetLastRegion());
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
                var windowDoc = ApplyMacStyleIfEnabled(doc, image, CaptureMode.WindowPick, title, out var bakedImage);
                return new CaptureResult
                {
                    Image = bakedImage,
                    Mode = CaptureMode.WindowPick,
                    WindowTitle = title,
                    SourceRect = outcome.PickedWindow?.Bounds ?? outcome.Region,
                    RequestedAction = MapIntent(outcome.Intent),
                    AnnotationDocument = windowDoc,
                };
            }
            Log.Warn("Window capture failed after picking; falling back to the frozen region crop.");
        }

        // macOS-style shadow also covers the habitual region flow: when the selection coincides
        // exactly with a window (click-snapped or hand-drawn), capture that window via PrintWindow
        // and bake the shadow instead of returning the plain screen crop.
        if (mode == CaptureMode.Region && outcome.WindowHandle == IntPtr.Zero
            && outcome.SnappedWindow is { } snapped && _settings.Current.Capture.MacStyleWindowShadow)
        {
            bool macCorners = _settings.Current.Capture.WindowTransparentCorners || _settings.Current.Capture.MacStyleWindowShadow;
            var winImage = _windowCapturer.CaptureWindow(snapped.Handle, macCorners, out var title);
            if (winImage is not null)
            {
                var snappedDoc = ApplyMacStyleIfEnabled(doc, winImage, CaptureMode.Region, title, out var baked);
                return new CaptureResult
                {
                    Image = baked,
                    Mode = CaptureMode.Region,
                    WindowTitle = title,
                    SourceRect = outcome.Region,
                    RequestedAction = MapIntent(outcome.Intent),
                    AnnotationDocument = snappedDoc,
                };
            }
            Log.Warn("Capturing the snapped window failed; falling back to the frozen region crop.");
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
    /// window) confirms immediately with the Default intent; the toolbar is hidden; Esc cancels.</summary>
    private PixelRect? SelectScrollingRegion()
    {
        var vs = _monitors.GetVirtualScreen();
        if (vs.IsEmpty) return null;
        // Freeze BEFORE any overlay is visible so the frozen slice can never contain the overlay itself.
        var frozen = _screen.Capture(vs, includeCursor: false);
        _windows.Refresh(); // snapshot before overlay windows exist
        var session = new OverlaySession(CaptureMode.Region, frozen, vs, frozen.ToBitmapSource(), _monitors,
            _windows, _settings.Current.Capture.ShowMagnifier, _settings.Current.Editor, autoConfirmOnSelect: true,
            persistEditorOptions: () => _settings.Save(),
            autoElementDetection: _settings.Current.Capture.AutoElementDetection,
            capturePriority: _settings.Current.Capture.CapturePriority && ElevationService.IsElevated,
            refreshFrozen: toggleCursor => RefreshFrozenFrame(vs, toggleCursor),
            lastRegion: GetLastRegion());
        var outcome = session.Show();
        if (outcome is null || !outcome.Confirmed || outcome.Region.IsEmpty) return null;
        return outcome.Region;
    }

    /// <summary>
    /// Re-captures the overlay's frozen frame on demand (F5 honors the current cursor setting,
    /// ` / ! flip it for that one refresh). Runs while the session has hidden every overlay window,
    /// so the fresh frame can never contain the overlay UI.
    /// </summary>
    private PixelBuffer RefreshFrozenFrame(PixelRect virtualScreen, bool toggleCursor)
    {
        bool includeCursor = _settings.Current.Capture.CaptureCursor;
        if (toggleCursor) includeCursor = !includeCursor;
        return _screen.Capture(virtualScreen, includeCursor);
    }

    /// <summary>The persisted last region (virtual-screen physical px) for the overlay's Shift+R,
    /// or null when none was stored. Only saved on a region confirm, so it is usually present.</summary>
    private PixelRect? GetLastRegion()
    {
        if (_settings.Current.Capture.LastRegion is not { Length: 4 } last) return null;
        var rect = new PixelRect(last[0], last[1], last[2], last[3]);
        return rect.IsEmpty ? null : rect;
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
        var doc = ApplyMacStyleIfEnabled(null, image, CaptureMode.ActiveWindow, title, out var bakedImage);
        return new CaptureResult
        {
            Image = bakedImage,
            Mode = CaptureMode.ActiveWindow,
            WindowTitle = title,
            SourceRect = bounds,
            AnnotationDocument = doc,
        };
    }

    /// <summary>
    /// When macOS-style window shadow is enabled, bakes the MacShadow preset (pure-black, tight,
    /// clearly visible drop shadow over a transparent surround) into the returned image so the
    /// result is what-you-see-is-what-you-get in the editor canvas, pin, clipboard, saved files and
    /// history. The document's Effects are stored disabled with
    /// <see cref="AnnotationDocument.EffectsBaked"/> set, so exports never compose them again and the
    /// editor close hook does not leak them into the user's global effect preferences. A pre-existing
    /// document (a region selection snapped onto a window, carrying inline annotations) has its items
    /// shifted into the padded surround. When disabled, the inputs pass through unchanged.
    /// </summary>
    private AnnotationDocument? ApplyMacStyleIfEnabled(AnnotationDocument? doc, PixelBuffer image, CaptureMode mode, string? title, out PixelBuffer bakedImage)
    {
        bakedImage = image;
        if (!_settings.Current.Capture.MacStyleWindowShadow) return doc;
        var effects = BuiltInPresets.MacShadow();
        try
        {
            bakedImage = EffectPipeline.Compose(image, effects); // Compose itself early-returns when disabled
        }
        catch (Exception ex)
        {
            Log.Error("Baking the macOS-style shadow failed; keeping the clean window capture.", ex);
            return doc;
        }
        effects.Enabled = false;

        // Recompute the content offset exactly the way Compose built its canvas (rounded corners →
        // shadow margins → symmetric frame padding) so inline annotations land on the same pixels.
        int offsetX = 0, offsetY = 0;
        var rounded = effects.Frame.CornerRadius >= 0.5 ? FrameEffect.RoundCorners(image, effects.Frame.CornerRadius) : image;
        if (ShadowEffect.RenderShadowOnly(rounded, effects.Shadow, out int shadowX, out int shadowY) is not null)
        {
            offsetX = shadowX;
            offsetY = shadowY;
        }
        if (effects.Frame.Padding >= 0.5 || effects.Frame.Background != BackgroundKind.None)
        {
            int pad = Math.Max(0, (int)Math.Round(effects.Frame.Padding));
            offsetX += pad;
            offsetY += pad;
        }

        doc ??= new AnnotationDocument
        {
            ImageWidth = bakedImage.Width,
            ImageHeight = bakedImage.Height,
            WindowTitle = title,
            CaptureMode = mode.ToString(),
        };
        if (doc.Items.Count > 0)
        {
            foreach (var item in doc.Items) item.Move(offsetX, offsetY);
        }
        doc.ImageWidth = bakedImage.Width;
        doc.ImageHeight = bakedImage.Height;
        doc.Effects = effects;
        doc.EffectsBaked = true;
        Log.Info($"macOS-style shadow baked into {mode} capture ({bakedImage.Width}x{bakedImage.Height}).");
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

    /// <summary>Maps a toolbar-chosen intent to its action. Default returns null so dispatch
    /// falls back to the user's configured "action after capture" — Enter/double-click/click-snap
    /// carry no explicit choice and must honor that setting.</summary>
    private static AfterCaptureAction? MapIntent(OverlayIntent intent) => intent switch
    {
        OverlayIntent.Default => null,
        OverlayIntent.Edit => AfterCaptureAction.OpenEditor,
        OverlayIntent.Copy => AfterCaptureAction.CopyOnly,
        OverlayIntent.Save => AfterCaptureAction.SaveOnly,
        OverlayIntent.Pin => AfterCaptureAction.Pin,
        OverlayIntent.Ocr => AfterCaptureAction.OcrText,
        OverlayIntent.Translate => AfterCaptureAction.TranslateText,
        _ => null,
    };
}
