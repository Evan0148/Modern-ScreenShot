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
        hexRow.Children.Add(new TextBlock { Text = L.Get("Prop.Hex"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        hexRow.Children.Add(_hexBox);
        var popupContent = new StackPanel
        {
            Margin = new Thickness(8),
            Background = Brushes.White,
        };
        popupContent.Children.Add(_picker);
        popupContent.Children.Add(hexRow);

        _popup = new Popup
        {
            Child = new Border
            {
                Child = popupContent,
                Background = Brushes.White,
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
            },
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
        Click += (_, _) => { _popup.PlacementTarget = this; _popup.IsOpen = true; };
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
