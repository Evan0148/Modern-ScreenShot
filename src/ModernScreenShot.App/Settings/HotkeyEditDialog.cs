using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ModernScreenShot.App.Controls;
using ModernScreenShot.App.Localization;
using ModernScreenShot.App.Shell;
using ModernScreenShot.Core.Settings;
using L = ModernScreenShot.App.Localization.LocalizationService;
using UiButton = Wpf.Ui.Controls.Button;

namespace ModernScreenShot.App.Settings;

/// <summary>
/// Modal hotkey editor styled with the settings window's theme resources. Top-down: a custom
/// title bar (Controls/AppTitleBar, close button + drag; carries the action name as its title),
/// the "must start with Win/Ctrl/Alt/Shift" hint, a large subtle capture zone with live key
/// chips, reset/clear links, then error / AltGr / in-app-conflict messages and Save/Cancel buttons.
///
/// Capture state machine (window-level PreviewKeyDown, logic ported from the former inline
/// recorder button):
///  - modifier pressed → live modifier chips ("waiting"), never an error;
///  - modifier + main key → captured binding, chips render neutral, Save enabled;
///  - bare main key (no modifier) → captured binding, chips render critical-red, error message, Save disabled;
///  - Esc (or Alt+F4) closes the dialog = cancel, it never clears the binding;
///  - Reset restores the action's default binding; Clear empties the capture area and enables
///    Save with the empty binding = unbinding the action (shown with a caution hint);
///  - an in-app duplicate or a Ctrl+Alt (AltGr) combination warns but does not block Save.
///
/// While open, global hotkeys are suspended via <see cref="ShowAndEdit"/>: RegisterHotKey would
/// otherwise swallow the very combinations this dialog must capture. Resume happens on every exit
/// path (save, cancel, Esc, Alt+F4, exception) through that method's finally block.
/// </summary>
internal sealed class HotkeyEditDialog : Window
{
    private readonly string _action;
    private readonly IReadOnlyDictionary<string, HotkeyBinding> _allBindings;
    private readonly HotkeyService? _hotkeys;
    private readonly LocalizationService _localization;
    private readonly HotkeyBinding _original;
    private HotkeyBinding _captured; // last captured combination; may be invalid (no modifier)
    private bool _cleared;           // Clear clicked → show the empty hint even if a binding exists

    private readonly Border _root = new();
    private readonly AppTitleBar _titleBar = new() { ShowClose = true };
    private readonly TextBlock _hint = new();
    private readonly TextBlock _zoneHint = new();
    private readonly ContentControl _chipsHost = new();
    private readonly Border _captureZone = new();
    private readonly StackPanel _messages = new();
    private readonly TextBlock _resetLabel = new();
    private readonly TextBlock _clearLabel = new();
    private readonly UiButton _resetButton;
    private readonly UiButton _clearButton;
    private readonly UiButton _saveButton = new()
    {
        Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, // matches the settings window's OK
        MinWidth = 120,
        MinHeight = 34,
    };
    private readonly UiButton _cancelButton = new()
    {
        MinWidth = 120, // default (secondary) appearance, like every other plain WPF-UI button
        MinHeight = 34,
    };

    /// <summary>The captured binding; only meaningful after ShowAndEdit returns true.</summary>
    public HotkeyBinding Result => _captured.Clone();

    public HotkeyEditDialog(string action, IReadOnlyDictionary<string, HotkeyBinding> allBindings,
        HotkeyService? hotkeys, LocalizationService localization)
    {
        _action = action;
        _allBindings = allBindings;
        _hotkeys = hotkeys;
        _localization = localization;
        _original = allBindings.GetValueOrDefault(action, new HotkeyBinding()).Clone();
        _captured = _original.Clone();

        _resetButton = LinkButton("\uE777", _resetLabel); // Sync arrow = reset to default
        _clearButton = LinkButton("\uE894", _clearLabel); // Delete = clear the capture area
        _resetButton.Click += (_, _) =>
        {
            _captured = HotkeySettings.CreateDefaults().GetValueOrDefault(_action, new HotkeyBinding()).Clone();
            _cleared = false;
            UpdatePreview();
        };
        _clearButton.Click += (_, _) =>
        {
            _captured = new HotkeyBinding();
            _cleared = true;
            UpdatePreview();
        };
        _saveButton.Click += (_, _) =>
        {
            if (!_saveButton.IsEnabled) return;
            DialogResult = true;
            Close();
        };
        _cancelButton.Click += (_, _) => { DialogResult = false; Close(); };

        BuildUi();
        RefreshTexts();
        UpdatePreview();

        PreviewKeyDown += OnCaptureKeyDown;
        PreviewKeyUp += OnCaptureKeyUp;
        Opacity = 0; // fade-in preset (OpenEditor pattern): WindowFadeIn lifts it right after ShowDialog
        Loaded += (_, _) =>
        {
            UiMotion.WindowFadeIn(this, 150);
            UiMotion.PopIn(_root, 0.96, 150);
        };
        _localization.LanguageChanged += OnLanguageChanged;
        Closed += (_, _) => _localization.LanguageChanged -= OnLanguageChanged;
    }

    /// <summary>
    /// Opens the dialog modally over <paramref name="owner"/>. Suspends the global hotkeys for the
    /// whole capture and always resumes — save, cancel, Esc, Alt+F4 and exceptions all funnel
    /// through the finally block.
    /// </summary>
    public bool ShowAndEdit(Window owner)
    {
        Owner = owner;
        _hotkeys?.Suspend();
        try
        {
            ShowDialog();
            return DialogResult == true;
        }
        finally
        {
            _hotkeys?.Resume();
        }
    }

    // ---- UI construction (pure C#, styled with theme resources like SettingsWindow) ----

    private void BuildUi()
    {
        WindowStyle = WindowStyle.None; // chromeless: the AppTitleBar row below draws the title
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize; // set before Attach so its maximize-hook guard sees it
        AppTitleBar.Attach(this, resizeBorderThickness: 0); // caption drag, no resize borders
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 520; // ~480 content + shadow bleed
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;

        _root.CornerRadius = new CornerRadius(8);
        _root.Padding = new Thickness(0); // the title bar row spans the full card width
        _root.BorderThickness = new Thickness(1);
        _root.RenderTransformOrigin = new Point(0.5, 0.5); // PopIn scales around the centre
        _root.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 3, Opacity = 0.4 };
        _root.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        _root.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        _hint.FontSize = 12;
        _hint.TextWrapping = TextWrapping.Wrap;
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");

        _zoneHint.FontSize = 12;
        _zoneHint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        _chipsHost.Content = null;
        var zoneContent = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        zoneContent.Children.Add(_chipsHost);
        zoneContent.Children.Add(_zoneHint);
        _captureZone.Height = 72;
        _captureZone.CornerRadius = new CornerRadius(8);
        _captureZone.BorderThickness = new Thickness(1);
        _captureZone.Child = zoneContent;
        _captureZone.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
        _captureZone.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var links = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 8, 0, 0),
        };
        links.Children.Add(_resetButton);
        links.Children.Add(_clearButton);

        _messages.Margin = new Thickness(0, 6, 0, 0);

        // Bottom bar mirrors the settings window's footer: right-aligned, the default cancel left
        // of the primary save.
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        _saveButton.Margin = new Thickness(8, 0, 0, 0);
        buttons.Children.Add(_cancelButton);
        buttons.Children.Add(_saveButton);

        var rows = new Grid { Margin = new Thickness(24, 8, 24, 24) }; // was _root padding + title gap
        for (int i = 0; i < 5; i++)
            rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _hint.Margin = new Thickness(0, 0, 0, 12);
        rows.Children.Add(_hint);
        rows.Children.Add(_captureZone);
        rows.Children.Add(links);
        rows.Children.Add(_messages);
        rows.Children.Add(buttons);
        Grid.SetRow(_hint, 0);
        Grid.SetRow(_captureZone, 1);
        Grid.SetRow(links, 2);
        Grid.SetRow(_messages, 3);
        Grid.SetRow(buttons, 4);

        var outer = new Grid(); // row 0: title bar (full card width), row 1: content
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // title bar hugs its 32 DIP
        outer.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // content fills the rest
        outer.Children.Add(_titleBar);
        outer.Children.Add(rows);
        Grid.SetRow(rows, 1); // now meaningful: the bar takes row 0, content row 1
        // A Border rounds only its own chrome, not its children — without a clip the close button's
        // square red hover fill would poke past the 8px rounded corner (DWM rounding is skipped for
        // this layered window). Clip the content to a rounded rect (radius 7 = 8 minus the 1px border).
        outer.ClipToBounds = true;
        outer.SizeChanged += (_, args) =>
            outer.Clip = new System.Windows.Media.RectangleGeometry(
                new Rect(0, 0, args.NewSize.Width, args.NewSize.Height), 7, 7);
        _root.Child = outer;

        Content = new Grid { Margin = new Thickness(20) }; // room for the drop shadow to bleed
        ((Grid)Content).Children.Add(_root);
    }

    /// <summary>All user-visible strings; re-run on language switches while the dialog is open.</summary>
    private void RefreshTexts()
    {
        Title = L.Get("Settings.Hotkeys");
        _titleBar.Text = L.Get($"Mode.{_action}");
        _hint.Text = L.Get("Settings.HotkeyDialogHint");
        _zoneHint.Text = L.Get("Settings.PressKeys");
        _resetLabel.Text = L.Get("Settings.HotkeyReset");
        _clearLabel.Text = L.Get("Settings.HotkeyClear");
        _saveButton.Content = L.Get("Action.Save");
        _cancelButton.Content = L.Get("Action.Cancel");
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        RefreshTexts();
        UpdatePreview(); // messages embed the conflicting action's localized name
    }

    // ---- capture ----

    private void OnCaptureKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true; // the whole window is a capture surface; buttons stay mouse-driven
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape:
            case Key.F4 when e.Key == Key.System: // Alt+F4 closes instead of being recorded
                Close(); // cancel — Resume runs in ShowAndEdit's finally
                return;
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                 or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin:
                UpdatePreview(); // live modifier preview ("waiting"), never an error
                return;
            case Key.None:
                return;
            default:
            {
                int vk = KeyInterop.VirtualKeyFromKey(key);
                if (vk == 0) return;
                _captured = new HotkeyBinding { Modifiers = ToWin32Modifiers(Keyboard.Modifiers), VirtualKey = vk };
                _cleared = false;
                UpdatePreview();
                return;
            }
        }
    }

    private void OnCaptureKeyUp(object sender, KeyEventArgs e)
    {
        // Only-modifier presses that end without a main key fall back to the resting display.
        if (_captured.IsEmpty && Keyboard.Modifiers == ModifierKeys.None) UpdatePreview();
    }

    private static int ToWin32Modifiers(ModifierKeys modifiers)
    {
        int m = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) m |= HotkeySettings.Alt;
        if (modifiers.HasFlag(ModifierKeys.Control)) m |= HotkeySettings.Ctrl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) m |= HotkeySettings.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) m |= HotkeySettings.Win;
        return m;
    }

    // ---- preview state machine ----

    private void UpdatePreview()
    {
        bool captured = !_captured.IsEmpty;
        bool waiting = !captured && Keyboard.Modifiers != ModifierKeys.None;
        bool error = captured && _captured.Modifiers == 0;
        bool valid = captured && _captured.Modifiers != 0;

        if (captured)
        {
            _chipsHost.Content = HotkeyChips.Build(_captured, HotkeyChips.ChipSize.Large, error);
            _zoneHint.Visibility = Visibility.Collapsed;
        }
        else if (waiting)
        {
            _chipsHost.Content = HotkeyChips.BuildModifiers(Keyboard.Modifiers, HotkeyChips.ChipSize.Large);
            _zoneHint.Visibility = Visibility.Collapsed;
        }
        else
        {
            _chipsHost.Content = null;
            _zoneHint.Visibility = Visibility.Visible;
        }

        // A valid combo enables Save; an explicitly cleared binding also enables it — saving the
        // empty binding unbinds the action (ReRegister skips empty bindings), restoring the
        // unbind capability the old inline recorder's Esc-clear behavior provided.
        bool unbound = _cleared && _captured.IsEmpty;
        _saveButton.IsEnabled = valid || unbound;
        _clearButton.Visibility = captured || _cleared ? Visibility.Visible : Visibility.Collapsed;

        _messages.Children.Clear();
        if (error)
        {
            _messages.Children.Add(MessageRow("\uE783", L.Get("Settings.HotkeyError"), "SystemFillColorCriticalBrush"));
            return; // an error excludes the warnings below by construction (no modifier → no AltGr/conflict)
        }
        if (!valid)
        {
            if (unbound)
                _messages.Children.Add(MessageRow("\uE7BA", L.Get("Settings.HotkeyUnboundHint"),
                    "SystemFillColorCautionBrush"));
            return;
        }

        // Ctrl+Alt without Win = AltGr layouts will not be able to type this combination.
        if ((_captured.Modifiers & HotkeySettings.Ctrl) != 0 && (_captured.Modifiers & HotkeySettings.Alt) != 0
            && (_captured.Modifiers & HotkeySettings.Win) == 0)
            _messages.Children.Add(MessageRow("\uE7BA", L.Get("Settings.HotkeyAltGr"), "SystemFillColorCautionBrush"));

        // In-app duplicates warn but do not block saving (same policy as the settings rows).
        var others = ConflictNames();
        if (others.Count > 0)
            _messages.Children.Add(MessageRow("\uE7BA",
                L.Get("Settings.HotkeyConflictRow", string.Join(", ", others)),
                "SystemFillColorCautionBrush"));
    }

    /// <summary>Localized names of the other actions already bound to the captured combination.</summary>
    private List<string> ConflictNames()
    {
        var names = new List<string>();
        foreach (var (otherAction, binding) in _allBindings)
        {
            if (otherAction == _action || binding.IsEmpty) continue;
            if (binding.Modifiers == _captured.Modifiers && binding.VirtualKey == _captured.VirtualKey)
                names.Add(L.Get($"Mode.{otherAction}"));
        }
        return names;
    }

    private static FrameworkElement MessageRow(string glyph, string message, string brushKey)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = HotkeyChips.GlyphFont,
            FontSize = 13,
            Margin = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        icon.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        var text = new TextBlock { FontSize = 12, Text = message, TextWrapping = TextWrapping.Wrap };
        text.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(icon);
        grid.Children.Add(text);
        Grid.SetColumn(text, 1);
        return grid;
    }

    /// <summary>Borderless text link (secondary text color at rest, primary on hover) built from a
    /// glyph plus a label whose text the caller keeps updating for language switches. The glyph and
    /// label inherit the button's foreground so the hover swap covers both.</summary>
    private static UiButton LinkButton(string glyph, TextBlock label)
    {
        var icon = new TextBlock
        {
            Text = glyph,
            FontFamily = HotkeyChips.GlyphFont,
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        label.FontSize = 12;
        label.Margin = new Thickness(6, 0, 0, 0);
        label.VerticalAlignment = VerticalAlignment.Center;
        var panel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(icon);
        panel.Children.Add(label);
        var button = new UiButton
        {
            Content = panel,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            Cursor = Cursors.Hand,
            Padding = new Thickness(8, 4, 8, 4),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        button.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
        button.MouseEnter += (_, _) =>
            button.SetResourceReference(Control.ForegroundProperty, "TextFillColorPrimaryBrush");
        button.MouseLeave += (_, _) =>
            button.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
        return button;
    }
}
