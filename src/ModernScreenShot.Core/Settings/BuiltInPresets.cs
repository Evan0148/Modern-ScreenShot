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

    /// <summary>
    /// macOS-style window shot: pure black, soft, clearly visible drop shadow with a slight
    /// downward offset — the ⌘⇧4 space-click look. Not part of All(): it is a capture-time style,
    /// not a user-facing editor preset. Tuning notes: the shadow margin that RenderShadowOnly adds
    /// (ceil(blur*1.5)+2 per side) already guarantees the falloff never clips, so Frame.Padding
    /// stays 0 — extra padding only grows the transparent surround into a "window on a card" look.
    /// With blur 38 the box ≈ gaussian σ≈19, so the contact edge sits near opacity*50% and fades
    /// to nothing within ~60px: opacity must stay high enough to read on dark backgrounds.
    /// </summary>
    public static EffectSettings MacShadow() => new()
    {
        Enabled = true,
        Shadow = new ShadowOptions
        {
            Enabled = true,
            BlurRadius = 38,        // three-pass box ≈ gaussian σ≈19: soft, slightly wide mac falloff
            Spread = 0,             // no dilation: dilation creates the flat "thick stroke" look
            Color = "#FF000000",    // pure black — no blue/purple tint
            Angle = 90,             // light from straight above
            Distance = 14,          // subtle downward offset
            Opacity = 0.40,         // ~20% at the contact edge, 40% right under the bottom edge
        },
        Reflection = new ReflectionOptions { Enabled = false },
        Frame = new FrameOptions
        {
            CornerRadius = 8,       // hugs the Win11 window corner radius
            Padding = 0,            // shadow margins are the surround — do not add more
            Background = BackgroundKind.None,
        },
    };

    public static IReadOnlyList<EffectPreset> All() => [Clean(), SoftFloat(), Dramatic(), Mirror(), GradientCard(), None()];
}
