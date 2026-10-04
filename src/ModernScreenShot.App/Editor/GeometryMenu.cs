using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Editor;

/// <summary>The drawing tools grouped under the merged "几何" toolbar entry, in menu order.</summary>
internal static class GeometryTools
{
    internal static readonly EditorTool[] All = [EditorTool.Rect, EditorTool.Ellipse, EditorTool.Line, EditorTool.Arrow];

    internal static bool IsGeometry(EditorTool tool) => Array.IndexOf(All, tool) >= 0;

    /// <summary>True for the line family, whose third-level variants are solid/dashed — the closed
    /// shapes vary by stroke/fill instead.</summary>
    internal static bool UsesDash(EditorTool tool) => tool is EditorTool.Line or EditorTool.Arrow;

    /// <summary>Tolerant parse of the persisted EditorSettings.GeometryTool name.</summary>
    internal static EditorTool Parse(string? name) =>
        Enum.TryParse(name, ignoreCase: true, out EditorTool tool) && IsGeometry(tool) ? tool : EditorTool.Rect;
}

/// <summary>
/// Flyout behind the merged "几何" toolbar entry, shared by the capture overlay and the editor.
/// Two columns: the left lists the four geometry tools (二级菜单); the right keeps the third level
/// (三级菜单) permanently visible — the style variants of the highlighted shape, stroke/fill for the
/// closed shapes and solid/dashed for the line family. Rows commit on mouse-UP so the
/// press-hold-drag-release flow from the toolbar button lands on a row directly.
/// </summary>
internal sealed class GeometryShapeMenu : Border
{
    private static readonly Brush BackBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0x1C, 0x1C, 0x1E)));
    private static readonly Brush ActiveToolBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x84, 0xFF)));
    private static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush DividerBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush FillHintBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)));

    private readonly Func<EditorTool> _effectiveTool;
    private readonly Func<EditorTool, bool> _isVariantOn;
    private readonly Action<EditorTool> _onToolPicked;
    private readonly Action<EditorTool, bool> _onVariantPicked;
    private readonly Dictionary<EditorTool, Border> _rows = [];
    private readonly StackPanel _shapesPanel = new() { Orientation = Orientation.Vertical };
    private readonly StackPanel _variantsPanel = new() { Orientation = Orientation.Vertical };
    private EditorTool _highlighted;

    /// <summary>Raised after any pick; the owner dismisses the menu.</summary>
    public event EventHandler? CloseRequested;

    /// <param name="effectiveTool">Shape shown as current: the active tool when a geometry tool is
    /// active, otherwise the last-used shape a plain click would activate.</param>
    /// <param name="isVariantOn">Current style flag of a shape (FillShape / DashedLine).</param>
    /// <param name="onToolPicked">A shape row was chosen: activate that tool.</param>
    /// <param name="onVariantPicked">A variant was chosen: apply the style flag and activate the tool.</param>
    public GeometryShapeMenu(Func<EditorTool> effectiveTool, Func<EditorTool, bool> isVariantOn,
        Action<EditorTool> onToolPicked, Action<EditorTool, bool> onVariantPicked)
    {
        _effectiveTool = effectiveTool;
        _isVariantOn = isVariantOn;
        _onToolPicked = onToolPicked;
        _onVariantPicked = onVariantPicked;

        foreach (var tool in GeometryTools.All)
        {
            var t = tool;
            var row = MakeRow(EditorIcons.For(t), L.Get($"Tool.{t}"), () =>
            {
                _onToolPicked(t);
                CloseRequested?.Invoke(this, EventArgs.Empty);
            });
            // Hover switches the third-level column; subscribed AFTER MakeRow's hover fill so the
            // tint survives the rebuild of the variants panel.
            row.MouseEnter += (_, _) => { _highlighted = t; BuildVariants(); };
            _rows[t] = row;
            _shapesPanel.Children.Add(row);
        }

        var divider = new Border { Width = 1, Background = DividerBrush, Margin = new Thickness(4, 2, 4, 2) };
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Pixel) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_shapesPanel, 0);
        Grid.SetColumn(divider, 1);
        Grid.SetColumn(_variantsPanel, 2);
        _variantsPanel.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(_shapesPanel);
        grid.Children.Add(divider);
        grid.Children.Add(_variantsPanel);

        Child = grid;
        Background = BackBrush;
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(5);
        Focusable = false;
        // Same popover treatment as the toolbar / F1 card.
        Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Direction = 270, Opacity = 0.45 };
        Refresh();
    }

    /// <summary>Re-syncs the shape and variant highlights with the owner's current state.</summary>
    public void Refresh()
    {
        _highlighted = _effectiveTool();
        foreach (var (tool, row) in _rows)
            row.Background = tool == _highlighted ? ActiveToolBrush : Brushes.Transparent;
        BuildVariants();
    }

    private void BuildVariants()
    {
        _variantsPanel.Children.Clear();
        foreach (var (labelKey, value, icon) in VariantsOf(_highlighted))
        {
            var row = MakeRow(icon, L.Get(labelKey), () =>
            {
                _onVariantPicked(_highlighted, value);
                CloseRequested?.Invoke(this, EventArgs.Empty);
            });
            row.Background = _isVariantOn(_highlighted) == value ? ActiveToolBrush : Brushes.Transparent;
            row.MinWidth = 92;
            _variantsPanel.Children.Add(row);
        }
    }

    private static Border MakeRow(FrameworkElement icon, string label, Action pick)
    {
        var stack = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        icon.VerticalAlignment = VerticalAlignment.Center;
        stack.Children.Add(icon);
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12.5,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(9, 0, 0, 0),
        });
        var row = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 5, 10, 5),
            MinWidth = 92,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = stack,
        };
        row.MouseEnter += (_, _) => { if (!ReferenceEquals(row.Background, ActiveToolBrush)) row.Background = HoverBrush; };
        row.MouseLeave += (_, _) => { if (!ReferenceEquals(row.Background, ActiveToolBrush)) row.Background = Brushes.Transparent; };
        // Mouse-UP commit: a plain click lands here, and so does the release of the press-hold-drag
        // flow started on the toolbar button.
        row.MouseLeftButtonUp += (_, e) => { e.Handled = true; pick(); };
        return row;
    }

    private static IEnumerable<(string LabelKey, bool Value, FrameworkElement Icon)> VariantsOf(EditorTool tool)
    {
        if (GeometryTools.UsesDash(tool))
        {
            yield return ("Prop.Solid", false, LineIcon(dashed: false));
            yield return ("Prop.Dashed", true, LineIcon(dashed: true));
        }
        else
        {
            yield return ("Prop.Stroke", false, ShapeOutline(tool, filled: false));
            yield return ("Prop.Fill", true, ShapeOutline(tool, filled: true));
        }
    }

    private static FrameworkElement ShapeOutline(EditorTool tool, bool filled) => tool == EditorTool.Rect
        ? BoxIcon(new RectangleGeometry(new Rect(3, 4.5, 12, 9)), filled)
        : BoxIcon(new EllipseGeometry(new Point(9, 9), 6.5, 5), filled);

    private static Canvas BoxIcon(Geometry geo, bool filled) => StrokePath(geo, filled ? FillHintBrush : null);

    private static Canvas LineIcon(bool dashed)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geo(g =>
            {
                g.BeginFigure(new Point(3, 14), false, false);
                g.LineTo(new Point(15, 4), true, false);
            }),
            Stroke = Brushes.White,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeDashArray = dashed ? new DoubleCollection { 2.6, 2.2 } : null,
        };
        return new Canvas { Width = 18, Height = 18, Children = { path } };
    }

    private static Canvas StrokePath(Geometry geo, Brush? fill)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = geo,
            Stroke = Brushes.White,
            StrokeThickness = 1.6,
            Fill = fill,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        var canvas = new Canvas { Width = 18, Height = 18 };
        canvas.Children.Add(path);
        return canvas;
    }

    private static Geometry Geo(Action<StreamGeometryContext> build)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open()) build(ctx);
        geo.Freeze();
        return geo;
    }

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }
}
