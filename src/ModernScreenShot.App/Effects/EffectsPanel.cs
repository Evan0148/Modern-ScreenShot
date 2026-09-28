using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ModernScreenShot.App.Controls;
using ModernScreenShot.App.Editor;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Settings;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Effects;

/// <summary>
/// Edits an <see cref="EffectSettings"/> instance in place (presets, shadow, reflection, frame) and
/// shows a live composited preview over a checkerboard. The editor owns debouncing and preview
/// rendering; the panel only raises <see cref="SettingsChanged"/>.
/// </summary>
public sealed class EffectsPanel : Grid
{
    private EffectSettings? _settings;
    private SettingsStore? _store;
    private bool _syncing;

    private readonly Image _previewImage = new() { Stretch = Stretch.Uniform, Margin = new Thickness(8) };
    private readonly ComboBox _presetCombo = new() { MinWidth = 120 };
    private readonly Button _savePresetButton = new();
    private readonly Button _deletePresetButton = new();

    private readonly CheckBox _enabledCheck = new();
    private readonly CheckBox _shadowCheck = new();
    private readonly CheckBox _reflectionCheck = new();

    private StackPanel _shadowSection = new();
    private StackPanel _reflectionSection = new();
    private StackPanel _solidBackgroundSection = new();
    private StackPanel _gradientSection = new();

    private readonly ColorPickerButton _shadowColor = new();
    private readonly ColorPickerButton _backgroundColor = new();
    private readonly ColorPickerButton _gradientStart = new();
    private readonly ColorPickerButton _gradientEnd = new();
    private readonly ColorPickerButton _borderColor = new();
    private readonly ComboBox _backgroundCombo = new() { MinWidth = 90 };

    private Slider _cornerRadius = null!, _framePadding = null!, _gradientAngle = null!, _borderThickness = null!;
    private Slider _shadowBlur = null!, _shadowSpread = null!, _shadowAngle = null!, _shadowDistance = null!, _shadowOpacity = null!;
    private Slider _reflHeight = null!, _reflStartOpacity = null!, _reflEndOpacity = null!, _reflGap = null!, _reflBlur = null!;

    /// <summary>Any setting changed; the editor re-renders the preview (debounced).</summary>
    public event EventHandler? SettingsChanged;

    public EffectsPanel()
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var previewBorder = new Border
        {
            Child = _previewImage,
            Margin = new Thickness(0, 0, 0, 8),
            MaxHeight = 240,
            Background = AnnotationRenderer.CheckerboardBrush(),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
        };
        SetRow(previewBorder, 0);
        Children.Add(previewBorder);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = BuildControls() };
        SetRow(scroll, 1);
        Children.Add(scroll);

        _presetCombo.SelectionChanged += (_, _) =>
        {
            if (!_syncing && _presetCombo.SelectedItem is PresetItem item) PresetSelected(item.Preset);
        };
    }

    private StackPanel BuildControls()
    {
        var root = new StackPanel();

        _enabledCheck.Content = L.Get("Effects.Enabled");
        _enabledCheck.Checked += (_, _) => Apply(s => s.Enabled = true);
        _enabledCheck.Unchecked += (_, _) => Apply(s => s.Enabled = false);
        root.Children.Add(_enabledCheck);

        var presetRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        presetRow.Children.Add(new TextBlock { Text = L.Get("Effects.Preset"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        presetRow.Children.Add(_presetCombo);
        _savePresetButton.Content = L.Get("Effects.SavePreset");
        _savePresetButton.Click += (_, _) => SaveUserPreset();
        _deletePresetButton.Content = L.Get("Effects.DeletePreset");
        _deletePresetButton.Click += (_, _) => DeleteUserPreset();
        presetRow.Children.Add(_savePresetButton);
        presetRow.Children.Add(_deletePresetButton);
        root.Children.Add(presetRow);

        // Shadow
        _shadowSection = Section(root, L.Get("Effects.Shadow"), _shadowCheck);
        _shadowCheck.Checked += (_, _) => Apply(s => s.Shadow.Enabled = true);
        _shadowCheck.Unchecked += (_, _) => Apply(s => s.Shadow.Enabled = false);
        _shadowBlur = LabeledSlider(_shadowSection, L.Get("Effects.Blur"), 0, 100, "{0:0}", v => Apply(s => s.Shadow.BlurRadius = v));
        _shadowSpread = LabeledSlider(_shadowSection, L.Get("Effects.Spread"), 0, 50, "{0:0}", v => Apply(s => s.Shadow.Spread = v));
        _shadowAngle = LabeledSlider(_shadowSection, L.Get("Effects.Angle"), 0, 360, "{0:0}°", v => Apply(s => s.Shadow.Angle = v));
        _shadowDistance = LabeledSlider(_shadowSection, L.Get("Effects.Distance"), 0, 100, "{0:0}", v => Apply(s => s.Shadow.Distance = v));
        _shadowOpacity = LabeledSlider(_shadowSection, L.Get("Effects.Opacity"), 0, 100, "{0:0}%", v => Apply(s => s.Shadow.Opacity = v / 100.0));
        ColorRow(_shadowSection, L.Get("Effects.Color"), _shadowColor);
        _shadowColor.ColorCommitted += (_, _) => Apply(s => s.Shadow.Color = _shadowColor.HexColor);

        // Reflection
        _reflectionSection = Section(root, L.Get("Effects.Reflection"), _reflectionCheck);
        _reflectionCheck.Checked += (_, _) => Apply(s => s.Reflection.Enabled = true);
        _reflectionCheck.Unchecked += (_, _) => Apply(s => s.Reflection.Enabled = false);
        _reflHeight = LabeledSlider(_reflectionSection, L.Get("Effects.Height"), 5, 100, "{0:0}%", v => Apply(s => s.Reflection.Height = v / 100.0));
        _reflStartOpacity = LabeledSlider(_reflectionSection, L.Get("Effects.StartOpacity"), 0, 100, "{0:0}%", v => Apply(s => s.Reflection.StartOpacity = v / 100.0));
        _reflEndOpacity = LabeledSlider(_reflectionSection, L.Get("Effects.EndOpacity"), 0, 100, "{0:0}%", v => Apply(s => s.Reflection.EndOpacity = v / 100.0));
        _reflGap = LabeledSlider(_reflectionSection, L.Get("Effects.Gap"), 0, 50, "{0:0}", v => Apply(s => s.Reflection.Gap = v));
        _reflBlur = LabeledSlider(_reflectionSection, L.Get("Effects.Blur"), 0, 20, "{0:0}", v => Apply(s => s.Reflection.Blur = v));

        // Frame
        var frameSection = Section(root, L.Get("Effects.Frame"), null);
        _cornerRadius = LabeledSlider(frameSection, L.Get("Effects.CornerRadius"), 0, 60, "{0:0}", v => Apply(s => s.Frame.CornerRadius = v));
        _framePadding = LabeledSlider(frameSection, L.Get("Effects.Padding"), 0, 120, "{0:0}", v => Apply(s => s.Frame.Padding = v));
        var bgRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        bgRow.Children.Add(new TextBlock { Text = L.Get("Effects.Background"), Width = 110, VerticalAlignment = VerticalAlignment.Center });
        foreach (var label in new[] { L.Get("Effects.BgNone"), L.Get("Effects.BgSolid"), L.Get("Effects.BgGradient") })
            _backgroundCombo.Items.Add(label);
        _backgroundCombo.SelectionChanged += (_, _) => Apply(s =>
            s.Frame.Background = _backgroundCombo.SelectedIndex switch { 1 => BackgroundKind.Solid, 2 => BackgroundKind.Gradient, _ => BackgroundKind.None });
        bgRow.Children.Add(_backgroundCombo);
        frameSection.Children.Add(bgRow);
        _borderThickness = LabeledSlider(frameSection, L.Get("Effects.Border"), 0, 10, "{0:0}", v => Apply(s => s.Frame.BorderThickness = v));
        ColorRow(frameSection, "", _borderColor);
        _borderColor.ColorCommitted += (_, _) => Apply(s => s.Frame.BorderColor = _borderColor.HexColor);

        _solidBackgroundSection = new StackPanel();
        ColorRow(_solidBackgroundSection, L.Get("Effects.Color"), _backgroundColor);
        _backgroundColor.ColorCommitted += (_, _) => Apply(s => s.Frame.BackgroundColor = _backgroundColor.HexColor);
        root.Children.Add(_solidBackgroundSection);

        _gradientSection = new StackPanel();
        ColorRow(_gradientSection, L.Get("Effects.GradientStart"), _gradientStart);
        _gradientStart.ColorCommitted += (_, _) => Apply(s => s.Frame.GradientStart = _gradientStart.HexColor);
        ColorRow(_gradientSection, L.Get("Effects.GradientEnd"), _gradientEnd);
        _gradientEnd.ColorCommitted += (_, _) => Apply(s => s.Frame.GradientEnd = _gradientEnd.HexColor);
        _gradientAngle = LabeledSlider(_gradientSection, L.Get("Effects.GradientAngle"), 0, 360, "{0:0}°", v => Apply(s => s.Frame.GradientAngle = v));
        root.Children.Add(_gradientSection);

        return root;
    }

    private static StackPanel Section(StackPanel root, string title, CheckBox? toggle)
    {
        var section = new StackPanel();
        var header = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 4) };
        header.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        if (toggle is not null)
        {
            toggle.Margin = new Thickness(8, 0, 0, 0);
            toggle.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(toggle);
        }
        section.Children.Add(header);
        root.Children.Add(section);
        return section;
    }

    private static void ColorRow(StackPanel section, string label, ColorPickerButton picker)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 0) };
        if (label.Length > 0)
            row.Children.Add(new TextBlock { Text = label, Width = 110, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(picker);
        section.Children.Add(row);
    }

    private Slider LabeledSlider(StackPanel section, string label, double min, double max, string format, Action<double> apply)
    {
        section.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 6, 0, 0) });
        var row = new DockPanel();
        var valueLabel = new TextBlock { Width = 44, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        var slider = new Slider { Minimum = min, Maximum = max, IsMoveToPointEnabled = true };
        slider.ValueChanged += (_, e) =>
        {
            valueLabel.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, format, e.NewValue);
            if (!_syncing) apply(e.NewValue);
        };
        // A bind value equal to the minimum raises no ValueChanged; seed the label so it is never blank.
        valueLabel.Text = string.Format(System.Globalization.CultureInfo.CurrentCulture, format, slider.Value);
        DockPanel.SetDock(valueLabel, Dock.Right);
        row.Children.Add(valueLabel);
        row.Children.Add(slider);
        section.Children.Add(row);
        return slider;
    }

    // ---- binding ----

    /// <summary>Binds the panel to a settings instance and refreshes everything.</summary>
    public void Bind(EffectSettings settings, SettingsStore store)
    {
        _settings = settings;
        _store = store;
        RebindPresets(selectMatching: true);
        SyncFromSettings();
    }

    private void RebindPresets(bool selectMatching)
    {
        _syncing = true;
        _presetCombo.Items.Clear();
        foreach (var p in BuiltInPresets.All()) _presetCombo.Items.Add(new PresetItem(p));
        foreach (var p in _store?.Current.UserPresets ?? []) _presetCombo.Items.Add(new PresetItem(p));
        if (selectMatching && _settings is not null)
        {
            string current = Serialize(_settings);
            for (int i = 0; i < _presetCombo.Items.Count; i++)
            {
                if (Serialize(((PresetItem)_presetCombo.Items[i]).Preset.Settings) == current)
                {
                    _presetCombo.SelectedIndex = i;
                    break;
                }
            }
        }
        _syncing = false;
        _deletePresetButton.IsEnabled = _presetCombo.SelectedItem is PresetItem { Preset.IsBuiltIn: false };
    }

    private static string Serialize(EffectSettings s) =>
        System.Text.Json.JsonSerializer.Serialize(s, ModernScreenShot.Core.JsonDefaults.Options);

    private void SyncFromSettings()
    {
        if (_settings is null) return;
        _syncing = true;
        var s = _settings;
        _enabledCheck.IsChecked = s.Enabled;
        _shadowCheck.IsChecked = s.Shadow.Enabled;
        _shadowBlur.Value = s.Shadow.BlurRadius;
        _shadowSpread.Value = s.Shadow.Spread;
        _shadowAngle.Value = s.Shadow.Angle;
        _shadowDistance.Value = s.Shadow.Distance;
        _shadowOpacity.Value = Math.Round(s.Shadow.Opacity * 100);
        _shadowColor.SetColor(s.Shadow.Color);
        _reflectionCheck.IsChecked = s.Reflection.Enabled;
        _reflHeight.Value = Math.Round(s.Reflection.Height * 100);
        _reflStartOpacity.Value = Math.Round(s.Reflection.StartOpacity * 100);
        _reflEndOpacity.Value = Math.Round(s.Reflection.EndOpacity * 100);
        _reflGap.Value = s.Reflection.Gap;
        _reflBlur.Value = s.Reflection.Blur;
        _cornerRadius.Value = s.Frame.CornerRadius;
        _framePadding.Value = s.Frame.Padding;
        _borderThickness.Value = s.Frame.BorderThickness;
        _borderColor.SetColor(s.Frame.BorderColor);
        _backgroundCombo.SelectedIndex = s.Frame.Background switch { BackgroundKind.Solid => 1, BackgroundKind.Gradient => 2, _ => 0 };
        _backgroundColor.SetColor(s.Frame.BackgroundColor);
        _gradientStart.SetColor(s.Frame.GradientStart);
        _gradientEnd.SetColor(s.Frame.GradientEnd);
        _gradientAngle.Value = s.Frame.GradientAngle;
        _syncing = false;
        UpdateDependentVisibility();
    }

    private void UpdateDependentVisibility()
    {
        if (_settings is null) return;
        _shadowSection.Visibility = _settings.Shadow.Enabled ? Visibility.Visible : Visibility.Collapsed;
        _reflectionSection.Visibility = _settings.Reflection.Enabled ? Visibility.Visible : Visibility.Collapsed;
        _solidBackgroundSection.Visibility = _settings.Frame.Background == BackgroundKind.Solid ? Visibility.Visible : Visibility.Collapsed;
        _gradientSection.Visibility = _settings.Frame.Background == BackgroundKind.Gradient ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Apply(Action<EffectSettings> mutate)
    {
        if (_settings is null || _syncing) return;
        mutate(_settings);
        UpdateDependentVisibility();
        _syncing = true;
        _presetCombo.SelectedIndex = -1;
        _syncing = false;
        _deletePresetButton.IsEnabled = false;
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SaveUserPreset()
    {
        if (_settings is null || _store is null) return;
        var name = PromptText(L.Get("Effects.SavePreset"), L.Get("Effects.PresetName"), Window.GetWindow(this));
        if (string.IsNullOrWhiteSpace(name)) return;
        _store.Current.UserPresets.RemoveAll(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        _store.Current.UserPresets.Add(new EffectPreset { Name = name, IsBuiltIn = false, Settings = _settings.Clone() });
        SavePresetsOrLog();
        RebindPresets(selectMatching: true);
    }

    private void DeleteUserPreset()
    {
        if (_store is null || _presetCombo.SelectedItem is not PresetItem { Preset.IsBuiltIn: false } item) return;
        _store.Current.UserPresets.RemoveAll(p => string.Equals(p.Name, item.Preset.Name, StringComparison.OrdinalIgnoreCase));
        SavePresetsOrLog();
        RebindPresets(selectMatching: false);
    }

    private void SavePresetsOrLog()
    {
        if (_store is null) return;
        try
        {
            _store.Save();
        }
        catch (Exception ex)
        {
            // Keep the in-memory list usable; the divergence from disk is at least logged.
            Log.Error("Persisting user effect presets failed", ex);
        }
    }

    private void PresetSelected(EffectPreset preset)
    {
        if (_settings is null) return;
        var clone = preset.Settings.Clone();
        _settings.Enabled = clone.Enabled;
        _settings.Shadow = clone.Shadow;
        _settings.Reflection = clone.Reflection;
        _settings.Frame = clone.Frame;
        SyncFromSettings();
        SettingsChanged?.Invoke(this, EventArgs.Empty);
    }

    private string? PromptText(string title, string label, Window? owner)
    {
        var box = new TextBox { Margin = new Thickness(0, 8, 0, 12), MinWidth = 260 };
        var ok = new Button { Content = L.Get("Action.Ok"), IsDefault = true, MinWidth = 72, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.Get("Action.Cancel"), IsCancel = true, MinWidth = 72 };
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        var win = new Window
        {
            Title = title,
            Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Owner = owner,
        };
        ok.Click += (_, _) => { if (!string.IsNullOrWhiteSpace(box.Text)) win.DialogResult = true; };
        box.Loaded += (_, _) => box.Focus();
        return win.ShowDialog() == true && !string.IsNullOrWhiteSpace(box.Text) ? box.Text.Trim() : null;
    }

    /// <summary>Called by the editor with the composited preview bitmap.</summary>
    public void SetPreview(BitmapSource image) => _previewImage.Source = image;

    private sealed record PresetItem(EffectPreset Preset)
    {
        public override string ToString() => Preset.IsBuiltIn ? L.Get(Preset.Name) : Preset.Name;
    }
}
