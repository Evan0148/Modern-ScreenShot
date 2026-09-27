using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.History;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Settings;

namespace ModernScreenShot.App.Output;

/// <summary>Writes confirmed captures into the history store (original + document + thumbnail + meta).</summary>
public sealed class HistoryRecorder
{
    private readonly HistoryStore _store;
    private readonly SettingsStore _settings;

    public HistoryRecorder(HistoryStore store, SettingsStore settings)
    {
        _store = store;
        _settings = settings;
    }

    public HistoryEntry? Record(PixelBuffer image, AnnotationDocument document)
    {
        try
        {
            var entry = _store.Create(image.Width, image.Height, document.WindowTitle, document.CaptureMode);
            BitmapInterop.SavePng(image, entry.OriginalPath);
            _store.SaveDocument(entry, document);
            BitmapInterop.SavePngScaled(image, entry.ThumbnailPath, 320);
            _store.SaveMeta(entry);
            _store.Prune(_settings.Current.HistoryMaxCount);
            Log.Info($"Capture recorded in history: {entry.Id}");
            return entry;
        }
        catch (Exception ex)
        {
            Log.Error("Recording capture in history failed", ex);
            return null;
        }
    }
}
