using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Settings;

namespace ModernScreenShot.App.Capture;

public sealed class CaptureResult
{
    public required PixelBuffer Image { get; init; }
    public required CaptureMode Mode { get; init; }
    public string? WindowTitle { get; init; }
    /// <summary>Source rectangle in physical virtual-screen pixels.</summary>
    public PixelRect SourceRect { get; init; }
    /// <summary>Action chosen in the overlay toolbar; null means use the settings default.</summary>
    public AfterCaptureAction? RequestedAction { get; init; }
    /// <summary>Annotations drawn inline in the overlay (image pixels relative to the crop). Image itself is the clean crop.</summary>
    public AnnotationDocument? AnnotationDocument { get; init; }
    /// <summary>
    /// When true, direct outputs (copy/save/pin) must bake <see cref="AnnotationDocument"/>.Effects into
    /// the pixels via the effect pipeline. Set only for macOS-style window shots, whose transparent
    /// shadow surround must survive even without opening the editor. Ordinary captures leave this false
    /// so direct output stays a clean crop (effects remain an editor-only step for them).
    /// </summary>
    public bool BakeEffectsOnDirectOutput { get; init; }
    public DateTime Time { get; init; } = DateTime.Now;
}
