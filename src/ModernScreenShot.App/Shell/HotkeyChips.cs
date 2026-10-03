using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ModernScreenShot.App.Localization;
using ModernScreenShot.Core.Settings;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Shell;

/// <summary>
/// Win11-settings-style outlined key caps: renders a <see cref="HotkeyBinding"/> as a row of
/// subtle-filled, hairline-outlined chips, one per key (modifiers as text, main key as a
/// letter/digit or a Segoe glyph). Two sizes — compact for the settings hotkey row, large for the
/// edit dialog's capture zone. The empty state is rendered by the callers (<see cref="Placeholder"/>
/// in the row, a hint text in the dialog) so both can decide what an unbound action should look
/// like in context. All colors come from theme resources, so light/dark themes both work.
/// </summary>
internal static class HotkeyChips
{
    public enum ChipSize { Inline, Large }

    /// <summary>Segoe Fluent Icons with an MDL2 fallback for systems without Fluent Icons.</summary>
    internal static readonly FontFamily GlyphFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private const double SpacingInline = 4, SpacingLarge = 8;

    /// <summary>
    /// Builds the chip row for a binding. When <paramref name="error"/> is set (a bare key without
    /// any modifier was captured) the text and border turn critical-red; the fill stays subtle.
    /// </summary>
    public static StackPanel Build(HotkeyBinding binding, ChipSize size, bool error = false)
    {
        double spacing = size == ChipSize.Large ? SpacingLarge : SpacingInline;
        // The negative right margin swallows the last chip's trailing spacing so right-aligned
        // rows (and centered capture zones) stay visually aligned.
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, -spacing, 0),
        };
        if (binding.IsEmpty) return panel;
        foreach (var (text, glyph) in Labels(binding))
            panel.Children.Add(Chip(text, glyph, size, error, spacing));
        return panel;
    }

    /// <summary>Modifier-only chips for the dialog's live "waiting for the main key" preview.</summary>
    public static StackPanel BuildModifiers(ModifierKeys modifiers, ChipSize size)
    {
        double spacing = size == ChipSize.Large ? SpacingLarge : SpacingInline;
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, -spacing, 0),
        };
        foreach (var (text, _) in ModifierLabels(modifiers))
            panel.Children.Add(Chip(text, glyph: false, size, error: false, spacing));
        return panel;
    }

    /// <summary>The inline placeholder for an unbound action: plus glyph + "configure" label inside
    /// the same subtle outline the chips use, in the secondary text color. Click handling stays
    /// with the wrapping row button in the settings window.</summary>
    public static FrameworkElement Placeholder()
    {
        var plus = new TextBlock
        {
            Text = "\uE710", // Add
            FontFamily = GlyphFont,
            FontSize = 10,
            VerticalAlignment = VerticalAlignment.Center,
        };
        plus.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var label = new TextBlock
        {
            Text = L.Get("Settings.HotkeyConfigure"),
            FontSize = 12,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(plus);
        panel.Children.Add(label);
        var border = new Border
        {
            CornerRadius = new CornerRadius(5),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 4, 10, 4),
            VerticalAlignment = VerticalAlignment.Center,
            Child = panel,
        };
        border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
        return border;
    }

    private static Border Chip(string text, bool glyph, ChipSize size, bool error, double spacing)
    {
        bool large = size == ChipSize.Large;
        var label = new TextBlock
        {
            Text = text,
            FontSize = large ? 14 : 12,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (glyph) label.FontFamily = GlyphFont;
        // Neutral key-cap text; the error state recolors text and border only — the fill stays
        // subtle, matching the Win11 settings look (no solid red blocks).
        label.SetResourceReference(
            TextBlock.ForegroundProperty,
            error ? "SystemFillColorCriticalBrush" : "TextFillColorPrimaryBrush");

        var chip = new Border
        {
            CornerRadius = new CornerRadius(large ? 6 : 5),
            MinWidth = large ? 0 : 30,
            Padding = large ? new Thickness(16, 10, 16, 10) : new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 0, spacing, 0),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = label,
        };
        chip.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        chip.SetResourceReference(
            Border.BorderBrushProperty,
            error ? "SystemFillColorCriticalBrush" : "CardStrokeColorDefaultBrush");
        return chip;
    }

    private static List<(string Text, bool Glyph)> Labels(HotkeyBinding binding)
    {
        var labels = ModifierLabels((ModifierKeys)binding.Modifiers);
        labels.Add(MainKeyLabel(binding.VirtualKey));
        return labels;
    }

    private static List<(string Text, bool Glyph)> ModifierLabels(ModifierKeys modifiers)
    {
        var labels = new List<(string, bool)>(4);
        // Same order as HotkeyService.Format ("Ctrl+Shift+Alt+Win") so row text and chips agree.
        if (modifiers.HasFlag(ModifierKeys.Control)) labels.Add(("Ctrl", false));
        if (modifiers.HasFlag(ModifierKeys.Shift)) labels.Add(("Shift", false));
        if (modifiers.HasFlag(ModifierKeys.Alt)) labels.Add(("Alt", false));
        if (modifiers.HasFlag(ModifierKeys.Windows)) labels.Add(("Win", false));
        return labels;
    }

    private static (string Text, bool Glyph) MainKeyLabel(int vk) => vk switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => (((char)vk).ToString(), false),
        0x5B or 0x5C => ("\uE782", true),  // Windows logo (LWin/RWin captured as the main key)
        0x25 => ("\uE0E7", true),          // Left arrow
        0x26 => ("\uE0E4", true),          // Up arrow
        0x27 => ("\uE0E5", true),          // Right arrow
        0x28 => ("\uE0E6", true),          // Down arrow
        0x0D => ("\uE751", true),          // Enter
        0x09 => ("\uE7C6", true),          // Tab
        _ => (HotkeyService.VirtualKeyName(vk), false), // Space, Back, Numpad..., F-keys show as text
    };
}
