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
    public DateTime Time { get; init; } = DateTime.Now;
}
