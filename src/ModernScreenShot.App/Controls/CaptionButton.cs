using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;

namespace ModernScreenShot.App.Controls;

/// <summary>
/// A Windows 11 native caption button whose entire visual state machine lives in its
/// <see cref="ControlTemplate"/> triggers — rest is transparent, hover uses the theme's
/// SubtleFillColorSecondaryBrush, pressed uses SubtleFillColorTertiaryBrush, disabled and an
/// inactive parent window dim the glyph to TextFillColorTertiaryBrush. There are deliberately no
/// MouseEnter/MouseLeave handlers assigning Background: ad-hoc property writes were the root of
/// the "stuck hover" residue, so state is expressed declaratively and theme resources are pulled
/// with DynamicResource so runtime light/dark switches follow.
///
/// The close variant is the one documented exception to theme-driven colors: hover/pressed fill
/// with a fixed deep red (#C42B1C) and a white glyph, because the theme Critical brush is a light
/// desaturated red in dark mode that drops the white glyph below the 3:1 UI contrast floor
/// (see KNOWN_ISSUES.md).
///
/// The implicit WPF-UI style is resolved through the base <see cref="Button"/> type in the
/// constructor; a WPF-UI control subclass otherwise falls back to the OS Aero chrome style.
/// </summary>
public sealed class CaptionButton : Button
{
    /// <summary>Glyph font chain shared via <see cref="TitleBarMetrics.GlyphFontFamily"/>.</summary>
    private static readonly FontFamily GlyphFont = new(TitleBarMetrics.GlyphFontFamily);

    /// <summary>Fixed Win11 caption-close red — frozen and never themed (see class remarks).</summary>
    private static readonly SolidColorBrush CloseHoverBrush = CreateFrozen(Color.FromRgb(0xC4, 0x2B, 0x1C));

    private static SolidColorBrush CreateFrozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(CaptionButton), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IsCloseButtonProperty = DependencyProperty.Register(
        nameof(IsCloseButton), typeof(bool), typeof(CaptionButton), new PropertyMetadata(false));

    /// <summary>False when the owning window is deactivated; drives the dimmed inactive glyph.</summary>
    public static readonly DependencyProperty IsWindowActiveProperty = DependencyProperty.Register(
        nameof(IsWindowActive), typeof(bool), typeof(CaptionButton), new PropertyMetadata(true));

    /// <summary>True for a button whose input the OS routes entirely through non-client
    /// <c>HTMAXBUTTON</c> surface (the Win11 Snap Layouts maximize button). WPF's own
    /// <see cref="UIElement.IsMouseOver"/>/<see cref="ButtonBase.IsPressed"/> stay stale in that
    /// region, so the pointer triggers are suppressed and only the NC state drives the fill.</summary>
    public static readonly DependencyProperty IsNcModeProperty = DependencyProperty.Register(
        nameof(IsNcMode), typeof(bool), typeof(CaptionButton), new PropertyMetadata(false));

    /// <summary>Non-client hover: set by the Snap Layouts bridge when the pointer is over the
    /// maximize button, whose input the OS routes as <c>HTMAXBUTTON</c> non-client surface (so
    /// <see cref="UIElement.IsMouseOver"/> stays false there).</summary>
    public static readonly DependencyProperty IsNcHoverProperty = DependencyProperty.Register(
        nameof(IsNcHover), typeof(bool), typeof(CaptionButton), new PropertyMetadata(false));

    /// <summary>Non-client pressed: set by the Snap Layouts bridge while the maximize button is held
    /// down in the non-client region.</summary>
    public static readonly DependencyProperty IsNcPressedProperty = DependencyProperty.Register(
        nameof(IsNcPressed), typeof(bool), typeof(CaptionButton), new PropertyMetadata(false));

    /// <summary>Opt-in for <see cref="WindowChrome.IsHitTestVisibleInChrome"/> on this button.</summary>
    public static readonly DependencyProperty IsHitTestVisibleInChromeProperty = DependencyProperty.Register(
        nameof(IsHitTestVisibleInChrome), typeof(bool), typeof(CaptionButton),
        new PropertyMetadata(true, OnIsHitTestVisibleInChromeChanged));

    /// <summary>Segoe Fluent Icons glyph character shown centered in the button.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>True for the close button (fixed red hover/pressed with a white glyph).</summary>
    public bool IsCloseButton
    {
        get => (bool)GetValue(IsCloseButtonProperty);
        set => SetValue(IsCloseButtonProperty, value);
    }

    /// <summary>Window activation state; false dims the glyph to the tertiary text fill.</summary>
    public bool IsWindowActive
    {
        get => (bool)GetValue(IsWindowActiveProperty);
        set => SetValue(IsWindowActiveProperty, value);
    }

    /// <summary>True when the button is driven by the non-client Snap Layouts path (maximize).</summary>
    public bool IsNcMode
    {
        get => (bool)GetValue(IsNcModeProperty);
        set => SetValue(IsNcModeProperty, value);
    }

    /// <summary>Non-client hover state (Snap Layouts maximize button); drives the hover fill.</summary>
    public bool IsNcHover
    {
        get => (bool)GetValue(IsNcHoverProperty);
        set => SetValue(IsNcHoverProperty, value);
    }

    /// <summary>Non-client pressed state (Snap Layouts maximize button); drives the pressed fill.</summary>
    public bool IsNcPressed
    {
        get => (bool)GetValue(IsNcPressedProperty);
        set => SetValue(IsNcPressedProperty, value);
    }

    /// <summary>When true the button stays clickable inside the WindowChrome caption band.</summary>
    public bool IsHitTestVisibleInChrome
    {
        get => (bool)GetValue(IsHitTestVisibleInChromeProperty);
        set => SetValue(IsHitTestVisibleInChromeProperty, value);
    }

    public CaptionButton()
        : this(string.Empty, false)
    {
    }

    /// <summary>Creates a caption button for <paramref name="glyph"/>; <paramref name="isClose"/>
    /// selects the fixed-red close variant.</summary>
    public CaptionButton(string glyph, bool isClose)
    {
        Width = TitleBarMetrics.ButtonWidth;
        Height = TitleBarMetrics.ButtonHeight;
        Focusable = false;
        IsTabStop = false;
        Background = Brushes.Transparent;
        // Native caption buttons keep the arrow cursor; the WPF-UI Button style would show a hand.
        Cursor = Cursors.Arrow;
        Template = BuildTemplate();

        // WPF-UI implicit styles are keyed by the exact runtime type; a Button subclass otherwise
        // resolves the OS Aero chrome style. Pull the themed Button style through the base type
        // and keep it as a (dynamic) resource reference so runtime theme switches re-resolve.
        SetResourceReference(StyleProperty, typeof(Button));

        WindowChrome.SetIsHitTestVisibleInChrome(this, IsHitTestVisibleInChrome);
        Configure(glyph, isClose);
    }

    /// <summary>Sets the glyph and close-variant flag; chainable for call-site composition.</summary>
    public CaptionButton Configure(string glyph, bool isClose)
    {
        Glyph = glyph;
        IsCloseButton = isClose;
        return this;
    }

    /// <summary>Assigns the automation name used by screen readers.</summary>
    public void SetAutomationName(string name) => AutomationProperties.SetName(this, name);

    private static void OnIsHitTestVisibleInChromeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is CaptionButton button)
        {
            WindowChrome.SetIsHitTestVisibleInChrome(button, (bool)e.NewValue);
        }
    }

    private static ControlTemplate BuildTemplate()
    {
        var root = new FrameworkElementFactory(typeof(Border), "Root");
        root.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

        var glyph = new FrameworkElementFactory(typeof(TextBlock), "GlyphText");
        glyph.SetValue(TextBlock.TextProperty, new TemplateBindingExtension(GlyphProperty));
        glyph.SetValue(TextBlock.FontFamilyProperty, GlyphFont);
        glyph.SetValue(TextBlock.FontSizeProperty, TitleBarMetrics.GlyphSize);
        glyph.SetValue(TextBlock.ForegroundProperty, new TemplateBindingExtension(Control.ForegroundProperty));
        glyph.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        glyph.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        glyph.SetValue(UIElement.IsHitTestVisibleProperty, false);
        root.AppendChild(glyph);

        var template = new ControlTemplate(typeof(CaptionButton)) { VisualTree = root };
        AddTriggers(template);
        return template;
    }

    private static void AddTriggers(ControlTemplate template)
    {
        // Base enabled glyph color; template triggers outrank the WPF-UI style setter, which is
        // what lets the inactive/close/disabled triggers below override it.
        var enabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = true };
        enabled.Setters.Add(new Setter(
            Control.ForegroundProperty,
            new DynamicResourceExtension(TitleBarMetrics.TextFillColorPrimaryBrushKey)));
        template.Triggers.Add(enabled);

        // Rest: transparent fill.
        var rest = new Trigger { Property = UIElement.IsMouseOverProperty, Value = false };
        rest.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent) { TargetName = "Root" });
        template.Triggers.Add(rest);

        // Inactive parent window: dim the glyph (hover/pressed fills still apply).
        var inactive = new Trigger { Property = IsWindowActiveProperty, Value = false };
        inactive.Setters.Add(new Setter(
            Control.ForegroundProperty,
            new DynamicResourceExtension(TitleBarMetrics.TextFillColorTertiaryBrushKey)));
        template.Triggers.Add(inactive);

        // Hover (non-close): subtle secondary fill.
        var hover = new MultiTrigger();
        hover.Conditions.Add(new System.Windows.Condition(UIElement.IsMouseOverProperty, true));
        hover.Conditions.Add(new System.Windows.Condition(IsCloseButtonProperty, false));
        hover.Conditions.Add(new System.Windows.Condition(IsNcModeProperty, false));
        hover.Setters.Add(new Setter(
            Border.BackgroundProperty,
            new DynamicResourceExtension(TitleBarMetrics.SubtleFillColorSecondaryBrushKey)) { TargetName = "Root" });
        template.Triggers.Add(hover);

        // Non-client hover (Snap Layouts maximize button): same fill as pointer hover, driven from
        // the NC hook because IsMouseOver cannot become true over HTMAXBUTTON surface.
        var ncHover = new MultiTrigger();
        ncHover.Conditions.Add(new System.Windows.Condition(IsNcHoverProperty, true));
        ncHover.Conditions.Add(new System.Windows.Condition(IsCloseButtonProperty, false));
        ncHover.Setters.Add(new Setter(
            Border.BackgroundProperty,
            new DynamicResourceExtension(TitleBarMetrics.SubtleFillColorSecondaryBrushKey)) { TargetName = "Root" });
        template.Triggers.Add(ncHover);

        // Pressed (non-close): subtle tertiary fill.
        var pressed = new MultiTrigger();
        pressed.Conditions.Add(new System.Windows.Condition(ButtonBase.IsPressedProperty, true));
        pressed.Conditions.Add(new System.Windows.Condition(IsCloseButtonProperty, false));
        pressed.Conditions.Add(new System.Windows.Condition(IsNcModeProperty, false));
        pressed.Setters.Add(new Setter(
            Border.BackgroundProperty,
            new DynamicResourceExtension(TitleBarMetrics.SubtleFillColorTertiaryBrushKey)) { TargetName = "Root" });
        template.Triggers.Add(pressed);

        // Non-client pressed: same fill as pointer pressed; declared after nc-hover so it wins when
        // both are active.
        var ncPressed = new MultiTrigger();
        ncPressed.Conditions.Add(new System.Windows.Condition(IsNcPressedProperty, true));
        ncPressed.Conditions.Add(new System.Windows.Condition(IsCloseButtonProperty, false));
        ncPressed.Setters.Add(new Setter(
            Border.BackgroundProperty,
            new DynamicResourceExtension(TitleBarMetrics.SubtleFillColorTertiaryBrushKey)) { TargetName = "Root" });
        template.Triggers.Add(ncPressed);

        // Close variant: fixed deep red + white glyph on hover and pressed.
        AddCloseStateTrigger(template, UIElement.IsMouseOverProperty);
        AddCloseStateTrigger(template, ButtonBase.IsPressedProperty);

        // Disabled last so it clears any hover/pressed fill that might otherwise linger.
        var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
        disabled.Setters.Add(new Setter(Border.BackgroundProperty, Brushes.Transparent) { TargetName = "Root" });
        disabled.Setters.Add(new Setter(
            Control.ForegroundProperty,
            new DynamicResourceExtension(TitleBarMetrics.TextFillColorTertiaryBrushKey)));
        template.Triggers.Add(disabled);
    }

    private static void AddCloseStateTrigger(ControlTemplate template, DependencyProperty stateProperty)
    {
        var trigger = new MultiTrigger();
        trigger.Conditions.Add(new System.Windows.Condition(IsCloseButtonProperty, true));
        trigger.Conditions.Add(new System.Windows.Condition(stateProperty, true));
        trigger.Setters.Add(new Setter(Border.BackgroundProperty, CloseHoverBrush) { TargetName = "Root" });
        trigger.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
        template.Triggers.Add(trigger);
    }
}
