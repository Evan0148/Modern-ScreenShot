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
    public DateTime Time { get; init; } = DateTime.Now;
}
