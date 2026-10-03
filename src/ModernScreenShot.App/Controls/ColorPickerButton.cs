using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Controls;

/// <summary>A button showing the current color; clicking opens a popup with a ColorPicker + hex box.</summary>
public sealed class ColorPickerButton : Button
{
    private readonly ColorPicker _picker = new();
    private readonly TextBox _hexBox = new() { Width = 96 };
    private Popup? _popup;
    private bool _syncing;

    public ColorPickerButton()
    {
        Width = 44;
        Height = 22;
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
        BorderThickness = new Thickness(1);
        Background = Brushes.White;
        Cursor = Cursors.Hand;
        ToolTip = L.Get("Prop.CustomColor");

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 6, 4, 0) };
        var hexLabel = new TextBlock { Text = L.Get("Prop.Hex"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
        // Theme-following surfaces: hardcoded white here rendered white-on-white text in dark theme.
        hexLabel.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
        hexRow.Children.Add(hexLabel);
        hexRow.Children.Add(_hexBox);
        var popupContent = new StackPanel
        {
            Margin = new Thickness(8),
        };
        popupContent.SetResourceReference(Panel.BackgroundProperty, "ApplicationBackgroundBrush");
        popupContent.Children.Add(_picker);
        popupContent.Children.Add(hexRow);

        var popupBorder = new Border
        {
            Child = popupContent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            // Pop origin at the trigger button's top edge (Placement=Bottom), per motion audit #3.
            RenderTransformOrigin = new Point(0.5, 0),
        };
        popupBorder.SetResourceReference(Border.BackgroundProperty, "ApplicationBackgroundBrush");
        popupBorder.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        _popup = new Popup
        {
            Child = popupBorder,
            Placement = PlacementMode.Bottom,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        _picker.ColorChanged += (_, _) => SyncFromPicker();
        _hexBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (Core.Imaging.PixelColor.TryParseHex(_hexBox.Text, out var c))
            {
                _syncing = true;
                _picker.Color = Color.FromArgb(c.A, c.R, c.G, c.B);
                _syncing = false;
                UpdateVisual();
                RaiseColorChanged();
            }
        };
        // Popup entrance (motion audit #3): PopIn the popup's child right before IsOpen. The
        // discrete from-keyframes re-seed opacity 0 / scale 0.96 on every open, so repeated
        // open/close always replays cleanly (Completed commits 1/1 as plain values afterwards).
        // Never pops from scale(0); reduced motion falls back to the opacity fade only.
        Click += (_, _) =>
        {
            _popup.PlacementTarget = this;
            UiMotion.PopIn(popupBorder, fromScale: 0.96, ms: 150);
            _popup.IsOpen = true;
        };
    }

    /// <summary>Current color as ARGB hex string.</summary>
    public string HexColor { get; private set; } = "#FF000000";

    /// <summary>Fired whenever the user commits a color change.</summary>
    public event EventHandler? ColorCommitted;

    public void SetColor(string hex)
    {
        // An unparsable value would desync HexColor from what the picker/visual actually show;
        // keep the previous color instead of accepting the broken string.
        if (!Core.Imaging.PixelColor.TryParseHex(hex, out var c)) return;
        HexColor = hex;
        _syncing = true;
        _picker.Color = Color.FromArgb(c.A, c.R, c.G, c.B);
        _hexBox.Text = c.ToHex();
        _syncing = false;
        UpdateVisual();
    }

    private void SyncFromPicker()
    {
        if (_syncing) return;
        var c = _picker.PixelColorValue;
        HexColor = c.ToHex();
        _hexBox.Text = c.ToHex();
        UpdateVisual();
        RaiseColorChanged();
    }

    private void UpdateVisual() => Background = new SolidColorBrush((Color)_picker.Color);

    private void RaiseColorChanged() => ColorCommitted?.Invoke(this, EventArgs.Empty);
}
