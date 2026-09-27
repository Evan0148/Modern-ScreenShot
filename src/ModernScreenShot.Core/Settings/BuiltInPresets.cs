using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.Core.Settings;

/// <summary>Built-in effect presets. Name values are localization keys (Preset.*).</summary>
public static class BuiltInPresets
{
    public static EffectPreset Clean() => new()
    {
        Name = "Preset.Clean", IsBuiltIn = true,
        Settings = new EffectSettings
        {
            Shadow = new ShadowOptions { Enabled = true, BlurRadius = 24, Distance = 8, Angle = 90, Color = "#FF000000", Opacity = 0.35 },
            Reflection = new ReflectionOptions { Enabled = false },
            Frame = new FrameOptions { Padding = 32, Background = BackgroundKind.None },
        },
    };

    public static EffectPreset SoftFloat() => new()
    {
        Name = "Preset.SoftFloat", IsBuiltIn = true,
        Settings = new EffectSettings
        {
            Shadow = new ShadowOptions { Enabled = true, BlurRadius = 48, Spread = 2, Distance = 20, Angle = 90, Color = "#FF1A2340", Opacity = 0.28 },
            Frame = new FrameOptions { Padding = 48, CornerRadius = 10 },
        },
    };

    public static EffectPreset Dramatic() => new()
    {
        Name = "Preset.Dramatic", IsBuiltIn = true,
        Settings = new EffectSettings
        {
            Shadow = new ShadowOptions { Enabled = true, BlurRadius = 36, Spread = 4, Distance = 28, Angle = 45, Color = "#FF000000", Opacity = 0.6 },
            Frame = new FrameOptions { Padding = 40 },
        },
    };

    public static EffectPreset Mirror() => new()
    {
        Name = "Preset.Mirror", IsBuiltIn = true,
        Settings = new EffectSettings
        {
            Shadow = new ShadowOptions { Enabled = true, BlurRadius = 20, Distance = 6, Angle = 90, Opacity = 0.3 },
            Reflection = new ReflectionOptions { Enabled = true, Height = 0.35, StartOpacity = 0.45, EndOpacity = 0, Gap = 4, Blur = 1 },
            Frame = new FrameOptions { Padding = 32, CornerRadius = 8 },
        },
    };

    public static EffectPreset GradientCard() => new()
    {
        Name = "Preset.GradientCard", IsBuiltIn = true,
        Settings = new EffectSettings
        {
            Shadow = new ShadowOptions { Enabled = true, BlurRadius = 40, Distance = 16, Angle = 90, Color = "#FF20124D", Opacity = 0.45 },
            Frame = new FrameOptions
            {
                Padding = 64, CornerRadius = 12, Background = BackgroundKind.Gradient,
                GradientStart = "#FF6A8DFF", GradientEnd = "#FFB06AFF", GradientAngle = 135,
            },
        },
    };

    public static EffectPreset None() => new()
    {
        Name = "Preset.None", IsBuiltIn = true,
        Settings = new EffectSettings
        {
            Enabled = false,
            Shadow = new ShadowOptions { Enabled = false },
            Reflection = new ReflectionOptions { Enabled = false },
            Frame = new FrameOptions { Padding = 0 },
        },
    };

    public static IReadOnlyList<EffectPreset> All() => [Clean(), SoftFloat(), Dramatic(), Mirror(), GradientCard(), None()];
}
