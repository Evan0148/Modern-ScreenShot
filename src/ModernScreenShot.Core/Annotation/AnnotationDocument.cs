using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.Core.Annotation;

/// <summary>
/// Everything needed to re-open a capture for editing. The base image is stored separately (original.png).
/// </summary>
public sealed class AnnotationDocument
{
    public int Version { get; set; } = 1;
    public int ImageWidth { get; set; }
    public int ImageHeight { get; set; }
    public List<AnnotationItem> Items { get; set; } = [];
    /// <summary>Non-destructive crop in base image pixels; null = full image.</summary>
    public PixelRect? Crop { get; set; }
    public EffectSettings Effects { get; set; } = new();
    public string? WindowTitle { get; set; }
    public string? CaptureMode { get; set; }

    public void RenumberSteps()
    {
        int n = 1;
        foreach (var s in Items.OfType<StepItem>()) s.Number = n++;
    }

    public int NextStepNumber() => Items.OfType<StepItem>().Count() + 1;
}
