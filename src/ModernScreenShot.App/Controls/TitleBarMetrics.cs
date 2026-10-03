using System.Windows;

namespace ModernScreenShot.App.Controls;

/// <summary>
/// Single source of truth for the app's Windows 11 native title bar geometry and the theme
/// resource keys its controls consume. Every window and probe that needs a title-bar dimension
/// must reference these members instead of re-declaring literals, so a future recalibration
/// (task 1 native measurement) lands in exactly one place.
///
/// Values are DIPs. <see cref="CaptionHeight"/> models the Win11 standard caption height
/// (32 DIP); the button defaults mirror the documented Win11 caption button box and are the
/// calibration baseline until the on-device measurement refines them.
/// </summary>
public static class TitleBarMetrics
{
    /// <summary>Title bar / native caption height in DIPs (Win11 standard).</summary>
    public const double CaptionHeight = 32;

    /// <summary>Caption button width in DIPs (Win11 baseline; recalibrated from native measurement).</summary>
    public const double ButtonWidth = 46;

    /// <summary>Caption button height in DIPs (Win11 baseline; recalibrated from native measurement).</summary>
    public const double ButtonHeight = 32;

    /// <summary>Caption glyph (Segoe Fluent Icons) font size in DIPs.</summary>
    public const double GlyphSize = 10;

    /// <summary>Square box reserved for an optional leading app icon in DIPs.</summary>
    public const double IconBox = 16;

    /// <summary>Title text font size in DIPs (matches the existing bar and Win11's compact title).</summary>
    public const double TitleFontSize = 12;

    /// <summary>Title text margins (left inset, no vertical, right gap before the caption buttons).</summary>
    public static readonly Thickness TitleMargin = new(16, 0, 8, 0);

    /// <summary>Glyph font chain: Segoe Fluent Icons with an MDL2 fallback for older systems.</summary>
    public const string GlyphFontFamily = "Segoe Fluent Icons, Segoe MDL2 Assets";

    /// <summary>WPF-UI 4.3.0 resource key: subtle secondary fill (caption button hover).</summary>
    public const string SubtleFillColorSecondaryBrushKey = "SubtleFillColorSecondaryBrush";

    /// <summary>WPF-UI 4.3.0 resource key: subtle tertiary fill (caption button pressed).</summary>
    public const string SubtleFillColorTertiaryBrushKey = "SubtleFillColorTertiaryBrush";

    /// <summary>WPF-UI 4.3.0 resource key: primary text fill (rest caption glyph / active title).</summary>
    public const string TextFillColorPrimaryBrushKey = "TextFillColorPrimaryBrush";

    /// <summary>WPF-UI 4.3.0 resource key: tertiary text fill (inactive/disabled glyph and title).</summary>
    public const string TextFillColorTertiaryBrushKey = "TextFillColorTertiaryBrush";
}
