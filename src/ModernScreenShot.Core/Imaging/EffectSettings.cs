namespace ModernScreenShot.Core.Imaging;

public sealed class ShadowOptions
{
    public bool Enabled { get; set; } = true;
    /// <summary>Blur radius in pixels (0-200).</summary>
    public double BlurRadius { get; set; } = 24;
    /// <summary>Expands the shadow mask before blurring (0-100 px).</summary>
    public double Spread { get; set; }
    /// <summary>ARGB hex, e.g. "#FF000000". Alpha is multiplied with Opacity.</summary>
    public string Color { get; set; } = "#FF000000";
    /// <summary>Direction the shadow is cast, in degrees. 0 = right, 90 = down (screen coordinates).</summary>
    public double Angle { get; set; } = 90;
    /// <summary>Offset distance in pixels (0-200).</summary>
    public double Distance { get; set; } = 8;
    /// <summary>0-1.</summary>
    public double Opacity { get; set; } = 0.35;

    public ShadowOptions Clone() => (ShadowOptions)MemberwiseClone();
}

public sealed class ReflectionOptions
{
    public bool Enabled { get; set; }
    /// <summary>Fraction (0.05-1) of the source height that is mirrored.</summary>
    public double Height { get; set; } = 0.35;
    public double StartOpacity { get; set; } = 0.45;
    public double EndOpacity { get; set; }
    /// <summary>Gap in pixels between the image and its reflection.</summary>
    public double Gap { get; set; } = 4;
    /// <summary>Optional blur radius applied to the reflection.</summary>
    public double Blur { get; set; }

    public ReflectionOptions Clone() => (ReflectionOptions)MemberwiseClone();
}

public enum BackgroundKind { None, Solid, Gradient }

public sealed class FrameOptions
{
    public double CornerRadius { get; set; }
    /// <summary>Extra margin around the composed content (after shadow/reflection bounds).</summary>
    public double Padding { get; set; } = 32;
    public BackgroundKind Background { get; set; } = BackgroundKind.None;
    public string BackgroundColor { get; set; } = "#FFFFFFFF";
    public string GradientStart { get; set; } = "#FF6A8DFF";
    public string GradientEnd { get; set; } = "#FFB06AFF";
    /// <summary>Gradient direction in degrees, 0 = left→right, 90 = top→bottom.</summary>
    public double GradientAngle { get; set; } = 135;
    /// <summary>Hairline border drawn inside the rounded image edge; 0 disables.</summary>
    public double BorderThickness { get; set; }
    public string BorderColor { get; set; } = "#33000000";

    public FrameOptions Clone() => (FrameOptions)MemberwiseClone();
}

public sealed class EffectSettings
{
    public bool Enabled { get; set; } = true;
    public ShadowOptions Shadow { get; set; } = new();
    public ReflectionOptions Reflection { get; set; } = new();
    public FrameOptions Frame { get; set; } = new();

    public EffectSettings Clone() => new()
    {
        Enabled = Enabled,
        Shadow = Shadow.Clone(),
        Reflection = Reflection.Clone(),
        Frame = Frame.Clone(),
    };
}

public sealed class EffectPreset
{
    public string Name { get; set; } = "";
    /// <summary>Built-in presets use a localization key as Name and cannot be deleted.</summary>
    public bool IsBuiltIn { get; set; }
    public EffectSettings Settings { get; set; } = new();
}
