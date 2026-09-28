using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Effects;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Output;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Output;
using ModernScreenShot.Core.Settings;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Editor;

/// <summary>Annotation editor over a captured image. Export goes through RenderFlattened → Core effects.</summary>
public partial class EditorWindow : Window
{
    private readonly CaptureResult _result;
    private readonly SettingsStore _settings;
    private readonly ClipboardService _clipboard;
    private readonly ImageExporter _exporter;
    private readonly AnnotationDocument? _document;
    private readonly Func<PixelBuffer, Window>? _pinFactory;
    private readonly Dictionary<EditorTool, RadioButton> _toolButtons = [];
    private readonly List<Button> _swatches = [];
    private AnnotationCanvas _canvas = null!;

    private bool _dirty;
    private bool _ready;
    private bool _syncingPanel;
    private bool _panelDirty;
    private bool _sliderMouseActive;
    private TextBox? _textOverlay;
    private TextItem? _textEditTarget;
    private PointD _textEditPosition;

    private WrapPanel _palette = null!;
    private TextBox _hexBox = null!;
    private Slider _thicknessSlider = null!, _opacitySlider = null!, _fontSizeSlider = null!, _strengthSlider = null!, _dimSlider = null!, _magZoomSlider = null!;
    private CheckBox _fillCheck = null!, _dashedCheck = null!, _boldCheck = null!;
    private ComboBox _mosaicModeCombo = null!, _spotShapeCombo = null!;
    private StackPanel _colorSection = null!, _strokeSection = null!, _fillSection = null!, _dashedSection = null!,
        _textSection = null!, _mosaicSection = null!, _spotlightSection = null!, _magnifierSection = null!, _hintSection = null!;
    private TextBlock _hintText = null!;
    private Button _undoButton = null!, _redoButton = null!, _deleteButton = null!, _frontButton = null!, _backButton = null!;
    private readonly Dictionary<Slider, DockPanel> _sliderRows = [];
    private Button _effectsButton = null!, _openFolderButton = null!;
    private TextBlock _zoomLabel = null!, _statusLabel = null!;
    private EffectsPanel _effectsPanel = null!;
    private bool _effectsVisible;
    private readonly DispatcherTimer _previewTimer = new() { Interval = TimeSpan.FromMilliseconds(60) };
    private int _previewRunId;
    private string? _lastSavedPath;

    public EditorWindow(CaptureResult result, SettingsStore settings, ClipboardService clipboard, ImageExporter exporter,
        AnnotationDocument? document = null, Func<PixelBuffer, Window>? pinFactory = null)
    {
        InitializeComponent();
        _result = result;
        _settings = settings;
        _clipboard = clipboard;
        _exporter = exporter;
        _document = document;
        _pinFactory = pinFactory;

        var editor = settings.Current.Editor;
        var wa = SystemParameters.WorkArea;
        Width = Math.Min(1400, wa.Width * 0.92);
        Height = Math.Min(920, wa.Height * 0.92);
        Title = L.Get("Editor.Title");

        BuildCanvas(result, editor);
        BuildToolbar();
        BuildProperties(editor);
        BuildEffectsPanel();
        BuildActions();
        _previewTimer.Tick += (_, _) => RenderPreview();
        _ready = true;
        UpdatePropertyPanel();
    }

    // ---- construction ----

    private void BuildCanvas(CaptureResult result, EditorSettings editor)
    {
        _canvas = new AnnotationCanvas(result.Image)
        {
            Tool = EditorTool.Select,
            StrokeColor = editor.StrokeColor,
            StrokeThickness = editor.StrokeThickness,
            FontSize = editor.FontSize,
            MosaicStrength = editor.MosaicCellSize,
            SpotlightDim = editor.SpotlightDim,
            MagnifierZoom = editor.MagnifierZoom,
        };
        _canvas.ScrollOwner = Scroll;
        _canvas.Document.WindowTitle = result.WindowTitle;
        _canvas.Document.CaptureMode = result.Mode.ToString();
        _canvas.Document.Effects = _settings.Current.Effects.Clone();

        _canvas.DocumentChanged += (_, _) => { _dirty = true; RefreshUndoRedo(); };
        _canvas.SelectionChanged += (_, _) => { UpdatePropertyPanel(); RefreshSelectionButtons(); };
        _canvas.TextEditRequested += (_, e) => OpenTextOverlay(e.Existing, e.Position);
        _canvas.ZoomRequested += CanvasOnZoomRequested;
        _canvas.Undo.Changed += (_, _) => RefreshUndoRedo();

        CanvasHost.Children.Add(_canvas);
        if (_document is not null) _canvas.LoadDocument(_document);
        Loaded += (_, _) => FitZoom();
    }

    private void BuildToolbar()
    {
        // WrapPanel: 14 tools overflow a single line at the default window width, which made the
        // last tools (Spotlight/Magnifier/Crop) unreachable.
        var panel = new WrapPanel();
        foreach (var (tool, key) in new[]
        {
            (EditorTool.Select, "Tool.Select"), (EditorTool.Rect, "Tool.Rect"), (EditorTool.Ellipse, "Tool.Ellipse"),
            (EditorTool.Line, "Tool.Line"), (EditorTool.Arrow, "Tool.Arrow"), (EditorTool.Pen, "Tool.Pen"),
            (EditorTool.Text, "Tool.Text"), (EditorTool.Step, "Tool.Step"), (EditorTool.Highlighter, "Tool.Highlighter"),
            (EditorTool.Mosaic, "Tool.Mosaic"), (EditorTool.Blur, "Tool.Blur"), (EditorTool.Spotlight, "Tool.Spotlight"),
            (EditorTool.Magnifier, "Tool.Magnifier"), (EditorTool.Crop, "Tool.Crop"),
        })
        {
            var rb = new RadioButton
            {
                GroupName = "editorTools",
                Content = L.Get(key),
                Margin = new Thickness(1, 0, 1, 0),
                Padding = new Thickness(8, 4, 8, 4),
                VerticalContentAlignment = VerticalAlignment.Center,
            };
            rb.Checked += (_, _) =>
            {
                _canvas.Tool = tool;
                if (_ready) UpdatePropertyPanel();
                RefreshSelectionButtons();
            };
            _toolButtons[tool] = rb;
            panel.Children.Add(rb);
        }
        _toolButtons[EditorTool.Select].IsChecked = true;

        panel.Children.Add(new Separator { Margin = new Thickness(8, 2, 8, 2) });
        _deleteButton = MakeToolButton(L.Get("Action.Delete"), (_, _) => _canvas.DeleteSelected());
        _frontButton = MakeToolButton(L.Get("Action.BringToFront"), (_, _) => _canvas.BringToFront());
        _backButton = MakeToolButton(L.Get("Action.SendToBack"), (_, _) => _canvas.SendToBack());
        panel.Children.Add(_deleteButton);
        panel.Children.Add(_frontButton);
        panel.Children.Add(_backButton);

        ToolbarHost.Child = panel;
    }

    private Button MakeToolButton(string text, RoutedEventHandler onClick)
    {
        var b = new Button
        {
            Content = text,
            Margin = new Thickness(1, 0, 1, 0),
            Padding = new Thickness(8, 4, 8, 4),
            Cursor = Cursors.Hand,
        };
        b.Click += onClick;
        return b;
    }

    private void BuildProperties(EditorSettings editor)
    {
        var root = PropertiesPanel;
        root.Children.Add(new TextBlock { Text = L.Get("Editor.Properties"), FontWeight = FontWeights.SemiBold, FontSize = 14 });

        // Colors
        _colorSection = Section(null);
        _palette = BuildPalette();
        _colorSection.Children.Add(_palette);
        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 0) };
        hexRow.Children.Add(new TextBlock { Text = L.Get("Prop.Hex"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        _hexBox = new TextBox { Width = 110, Text = _canvas.StrokeColor };
        _hexBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (PixelColor.TryParseHex(_hexBox.Text, out var c)) ApplyColor(c.ToHex());
            else _hexBox.Text = _canvas.StrokeColor;
        };
        hexRow.Children.Add(_hexBox);
        _colorSection.Children.Add(hexRow);
        root.Children.Add(_colorSection);

        // Stroke
        _strokeSection = Section(L.Get("Prop.Thickness"));
        _thicknessSlider = MakeSlider(_strokeSection, 1, 40, editor.StrokeThickness, "{0:0.#}", v => ApplySlider(
            () => _canvas.StrokeThickness = v,
            item => item.StrokeThickness = v));
        root.Children.Add(_strokeSection);

        // Fill / dashed
        _fillSection = Section(null);
        _fillCheck = new CheckBox { Content = L.Get("Prop.Fill") };
        _fillCheck.Checked += (_, _) => ApplyFill(true);
        _fillCheck.Unchecked += (_, _) => ApplyFill(false);
        _fillSection.Children.Add(_fillCheck);
        root.Children.Add(_fillSection);

        _dashedSection = Section(null);
        _dashedCheck = new CheckBox { Content = L.Get("Prop.Dashed") };
        _dashedCheck.Checked += (_, _) => ApplyInstant(() => _canvas.DashedLine = true, item => { if (item is LineItem l) l.Dashed = true; });
        _dashedCheck.Unchecked += (_, _) => ApplyInstant(() => _canvas.DashedLine = false, item => { if (item is LineItem l) l.Dashed = false; });
        _dashedSection.Children.Add(_dashedCheck);
        root.Children.Add(_dashedSection);

        // Text
        _textSection = Section(L.Get("Prop.FontSize"));
        _fontSizeSlider = MakeSlider(_textSection, 10, 72, editor.FontSize, "{0:0}", v => ApplySlider(
            () => _canvas.FontSize = v,
            item => { if (item is TextItem t) { t.FontSize = v; AnnotationCanvas.MeasureTextItem(t); } }));
        _boldCheck = new CheckBox { Content = L.Get("Prop.Bold"), Margin = new Thickness(0, 4, 0, 0) };
        _boldCheck.Checked += (_, _) => ApplyTextFlag(true);
        _boldCheck.Unchecked += (_, _) => ApplyTextFlag(false);
        _textSection.Children.Add(_boldCheck);
        root.Children.Add(_textSection);

        // Opacity
        var opSection = Section(L.Get("Prop.Opacity"));
        _opacitySlider = MakeSlider(opSection, 10, 100, 100, "{0:0}%", v => ApplySlider(
            () => _canvas.ItemOpacity = v / 100.0,
            item => item.Opacity = v / 100.0));
        root.Children.Add(opSection);

        // Mosaic
        _mosaicSection = Section(L.Get("Prop.MosaicMode") + " / " + L.Get("Prop.Strength"));
        _mosaicModeCombo = new ComboBox { Width = 120 };
        _mosaicModeCombo.Items.Add(L.Get("Prop.Pixelate"));
        _mosaicModeCombo.Items.Add(L.Get("Prop.BlurMode"));
        _mosaicModeCombo.SelectedIndex = 0;
        _mosaicModeCombo.SelectionChanged += (_, _) => ApplyInstant(
            () => _canvas.MosaicMode = _mosaicModeCombo.SelectedIndex == 1 ? MosaicMode.Blur : MosaicMode.Pixelate,
            item => { if (item is MosaicItem m) m.Mode = _mosaicModeCombo.SelectedIndex == 1 ? MosaicMode.Blur : MosaicMode.Pixelate; });
        _mosaicSection.Children.Add(_mosaicModeCombo);
        _mosaicSection.Children.Add(new TextBlock { Text = L.Get("Prop.Strength"), Margin = new Thickness(0, 6, 0, 0) });
        _strengthSlider = MakeSlider(_mosaicSection, 2, 60, editor.MosaicCellSize, "{0:0}", v => ApplySlider(
            () => _canvas.MosaicStrength = (int)v,
            item => { if (item is MosaicItem m) m.Strength = (int)v; }));
        root.Children.Add(_mosaicSection);

        // Spotlight
        _spotlightSection = Section(L.Get("Prop.Shape") + " / " + L.Get("Prop.Dim"));
        _spotShapeCombo = new ComboBox { Width = 120 };
        _spotShapeCombo.Items.Add(L.Get("Prop.ShapeRect"));
        _spotShapeCombo.Items.Add(L.Get("Prop.ShapeEllipse"));
        _spotShapeCombo.SelectedIndex = 0;
        _spotShapeCombo.SelectionChanged += (_, _) => ApplyInstant(
            () => _canvas.SpotlightElliptical = _spotShapeCombo.SelectedIndex == 1,
            item => { if (item is SpotlightItem s) s.Elliptical = _spotShapeCombo.SelectedIndex == 1; });
        _spotlightSection.Children.Add(_spotShapeCombo);
        _spotlightSection.Children.Add(new TextBlock { Text = L.Get("Prop.Dim"), Margin = new Thickness(0, 6, 0, 0) });
        _dimSlider = MakeSlider(_spotlightSection, 10, 95, (int)Math.Round(editor.SpotlightDim * 100), "{0:0}%", v => ApplySlider(
            () => _canvas.SpotlightDim = v / 100.0,
            item => { if (item is SpotlightItem s) s.DimOpacity = v / 100.0; }));
        root.Children.Add(_spotlightSection);

        // Magnifier
        _magnifierSection = Section(L.Get("Prop.Zoom"));
        _magZoomSlider = MakeSlider(_magnifierSection, 15, 50, (int)Math.Round(editor.MagnifierZoom * 10), "{0:0.0}×", v => ApplySlider(
            () => _canvas.MagnifierZoom = v / 10.0,
            item => { if (item is MagnifierItem m) m.Zoom = v / 10.0; }));
        root.Children.Add(_magnifierSection);

        // Hint
        _hintSection = Section(null);
        _hintText = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = SecondaryText() };
        _hintSection.Children.Add(_hintText);
        root.Children.Add(_hintSection);
    }

    private WrapPanel BuildPalette()
    {
        var wp = new WrapPanel { Margin = new Thickness(0, 4, 0, 4) };
        foreach (var hex in _settings.Current.Editor.Palette)
        {
            var sw = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(0, 0, 5, 5),
                Background = AnnotationRenderer.BrushFor(hex),
                BorderBrush = Brushes.Gray,
                BorderThickness = new Thickness(1),
                Tag = hex,
                Cursor = Cursors.Hand,
            };
            sw.Click += (_, _) => ApplyColor(hex);
            _swatches.Add(sw);
            wp.Children.Add(sw);
        }
        return wp;
    }

    private static StackPanel Section(string? title)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        if (title is not null)
            sp.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        return sp;
    }

    /// <summary>Slider with a right-aligned live value label — mirrors the EffectsPanel rows. Adds the row to <paramref name="section"/>.</summary>
    private Slider MakeSlider(StackPanel section, double min, double max, double initial, string format, Action<double> onChanged)
    {
        var valueLabel = new TextBlock
        {
            Width = 44,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Right,
            Foreground = SecondaryText(),
        };
        var s = new Slider { Minimum = min, Maximum = max, Value = initial, IsMoveToPointEnabled = true };
        s.ValueChanged += (_, e) =>
        {
            valueLabel.Text = string.Format(CultureInfo.CurrentCulture, format, e.NewValue);
            onChanged(e.NewValue);
        };
        valueLabel.Text = string.Format(CultureInfo.CurrentCulture, format, s.Value);
        var row = new DockPanel();
        DockPanel.SetDock(valueLabel, Dock.Right);
        row.Children.Add(valueLabel);
        row.Children.Add(s);
        _sliderRows[s] = row;
        section.Children.Add(row);
        s.PreviewMouseDown += (_, _) => { _sliderMouseActive = true; if (_canvas.Selected is not null) _canvas.BeginItemEdit(); };
        s.PreviewMouseUp += (_, _) => { _sliderMouseActive = false; FinishSliderEdit(); };
        return s;
    }

    private void FinishSliderEdit()
    {
        if (_panelDirty && _canvas.Selected is not null) _canvas.EndItemEdit();
        else _canvas.CancelItemEdit();
        _panelDirty = false;
    }

    // ---- property application ----

    private void ApplySlider(Action setDefault, Action<AnnotationItem> applyItem)
    {
        if (_syncingPanel) return;
        _panelDirty = true;
        // Keyboard changes (arrow keys) bypass PreviewMouseDown; snapshot here so the mutation
        // does not silently fold into the next unrelated undo entry.
        bool ownsSnapshot = false;
        if (!_sliderMouseActive && _canvas.Selected is not null)
        {
            _canvas.BeginItemEdit();
            ownsSnapshot = true;
        }
        setDefault();
        if (_canvas.Selected is { } item) applyItem(item);
        _canvas.InvalidateVisual();
        if (ownsSnapshot) { FinishSliderEdit(); }
    }

    private void ApplyInstant(Action setDefault, Action<AnnotationItem> applyItem)
    {
        if (_syncingPanel) return;
        setDefault();
        if (_canvas.Selected is not { } item) return;
        _canvas.BeginItemEdit();
        applyItem(item);
        _canvas.EndItemEdit();
    }

    private void ApplyColor(string hex)
    {
        ApplyInstant(
            () => { _canvas.StrokeColor = hex; _hexBox.Text = hex; UpdateSwatches(); },
            item =>
            {
                item.StrokeColor = hex;
                if (item is RectItem { Filled: true } r) r.FillColor = AnnotationCanvas.FillColorFor(hex);
                if (item is EllipseItem { Filled: true } e) e.FillColor = AnnotationCanvas.FillColorFor(hex);
            });
    }

    private void ApplyFill(bool filled)
    {
        ApplyInstant(
            () => _canvas.FillShape = filled,
            item =>
            {
                switch (item)
                {
                    case RectItem r:
                        r.Filled = filled;
                        if (filled) r.FillColor = AnnotationCanvas.FillColorFor(r.StrokeColor);
                        break;
                    case EllipseItem e:
                        e.Filled = filled;
                        if (filled) e.FillColor = AnnotationCanvas.FillColorFor(e.StrokeColor);
                        break;
                }
            });
    }

    private void ApplyTextFlag(bool bold)
    {
        ApplyInstant(
            () => _canvas.FontBold = bold,
            item => { if (item is TextItem t) { t.Bold = bold; AnnotationCanvas.MeasureTextItem(t); } });
    }

    private static Brush SecondaryText() =>
        System.Windows.Application.Current.TryFindResource("TextFillColorSecondaryBrush") as Brush ?? Brushes.Gray;

    private void UpdateSwatches()
    {
        foreach (var sw in _swatches)
            sw.BorderThickness = string.Equals((string)sw.Tag, _canvas.StrokeColor, StringComparison.OrdinalIgnoreCase)
                ? new Thickness(3)
                : new Thickness(1);
    }

    /// <summary>Shows the sections that apply to the active tool (or the selected item) and syncs control values.</summary>
    private void UpdatePropertyPanel()
    {
        _syncingPanel = true;
        var tool = _canvas.Tool;
        var sel = _canvas.Selected;
        if (tool == EditorTool.Select && sel is not null) ShowForItem(sel);
        else ShowForTool(tool);
        UpdateSwatches();
        _hexBox.Text = _canvas.StrokeColor;
        _syncingPanel = false;
    }

    private void ShowForTool(EditorTool tool)
    {
        bool stroke = tool is EditorTool.Rect or EditorTool.Ellipse or EditorTool.Line or EditorTool.Arrow
            or EditorTool.Pen or EditorTool.Highlighter;
        SetVisible(_strokeSection, stroke);
        SetVisible(_fillSection, tool is EditorTool.Rect or EditorTool.Ellipse);
        SetVisible(_dashedSection, tool is EditorTool.Line or EditorTool.Arrow);
        SetVisible(_textSection, tool == EditorTool.Text);
        SetVisible(_mosaicSection, tool is EditorTool.Mosaic or EditorTool.Blur);
        SetVisible(_spotlightSection, tool == EditorTool.Spotlight);
        SetVisible(_magnifierSection, tool == EditorTool.Magnifier);
        SetVisible(_hintSection, tool is EditorTool.Select or EditorTool.Crop);
        _hintText.Text = tool == EditorTool.Crop ? L.Get("Editor.CropHint") : L.Get("Editor.SelectHint");
        _syncingPanel = false;
        SyncControlValues(null);
    }

    private void ShowForItem(AnnotationItem item)
    {
        SetVisible(_strokeSection, item is not (MosaicItem or SpotlightItem or MagnifierItem or TextItem or StepItem));
        SetVisible(_fillSection, item is RectItem or EllipseItem);
        SetVisible(_dashedSection, item is LineItem);
        SetVisible(_textSection, item is TextItem);
        SetVisible(_mosaicSection, item is MosaicItem);
        SetVisible(_spotlightSection, item is SpotlightItem);
        SetVisible(_magnifierSection, item is MagnifierItem);
        SetVisible(_hintSection, false);
        SyncControlValues(item);
    }

    private void SyncControlValues(AnnotationItem? item)
    {
        _syncingPanel = true;
        if (item is not null) _canvas.StrokeColor = item.StrokeColor;
        _thicknessSlider.Value = item?.StrokeThickness ?? _canvas.StrokeThickness;
        _opacitySlider.Value = Math.Round((item?.Opacity ?? _canvas.ItemOpacity) * 100);
        _fillCheck.IsChecked = item switch { RectItem r => r.Filled, EllipseItem e => e.Filled, _ => _canvas.FillShape };
        _dashedCheck.IsChecked = item switch { LineItem l => l.Dashed, _ => _canvas.DashedLine };
        _fontSizeSlider.Value = item switch { TextItem t => t.FontSize, _ => _canvas.FontSize };
        _boldCheck.IsChecked = item switch { TextItem t => t.Bold, _ => _canvas.FontBold };
        _mosaicModeCombo.SelectedIndex = item switch { MosaicItem m when m.Mode == MosaicMode.Blur => 1, _ => _canvas.MosaicMode == MosaicMode.Blur ? 1 : 0 };
        _strengthSlider.Value = item switch { MosaicItem m => m.Strength, _ => _canvas.MosaicStrength };
        _spotShapeCombo.SelectedIndex = item switch { SpotlightItem s when s.Elliptical => 1, _ => _canvas.SpotlightElliptical ? 1 : 0 };
        _dimSlider.Value = Math.Round((item switch { SpotlightItem s => s.DimOpacity, _ => _canvas.SpotlightDim }) * 100);
        _magZoomSlider.Value = Math.Round((item switch { MagnifierItem m => m.Zoom, _ => _canvas.MagnifierZoom }) * 10);
        _syncingPanel = false;
    }

    private static void SetVisible(UIElement el, bool visible) => el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    // ---- actions bar ----

    private void BuildActions()
    {
        var panel = new DockPanel();
        _statusLabel = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = SecondaryText(),
            Margin = new Thickness(12, 0, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 320,
        };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_statusLabel);
        DockPanel.SetDock(right, Dock.Right);
        panel.Children.Add(right);

        var left = new StackPanel { Orientation = Orientation.Horizontal };
        _undoButton = MakeToolButton(L.Get("Action.Undo"), (_, _) => _canvas.DoUndo());
        _redoButton = MakeToolButton(L.Get("Action.Redo"), (_, _) => _canvas.DoRedo());
        left.Children.Add(_undoButton);
        left.Children.Add(_redoButton);
        left.Children.Add(new Separator { Margin = new Thickness(8, 2, 8, 2) });
        left.Children.Add(MakeToolButton(L.Get("Editor.Fit"), (_, _) => FitZoom()));
        var zoomOut = MakeToolButton("−", (_, _) => ZoomBy(1 / 1.25));
        var zoomIn = MakeToolButton("+", (_, _) => ZoomBy(1.25));
        zoomOut.Width = zoomIn.Width = 32;
        left.Children.Add(zoomOut);
        _zoomLabel = new TextBlock { Text = "100%", VerticalAlignment = VerticalAlignment.Center, Width = 52, TextAlignment = TextAlignment.Center };
        left.Children.Add(_zoomLabel);
        left.Children.Add(zoomIn);
        left.Children.Add(new Separator { Margin = new Thickness(8, 2, 8, 2) });
        left.Children.Add(MakeToolButton(L.Get("Action.Copy"), (_, _) => CopyResult()));
        left.Children.Add(MakeToolButton(L.Get("Action.Save"), (_, _) => SaveQuick()));
        left.Children.Add(MakeToolButton(L.Get("Action.SaveAs"), (_, _) => SaveAsDialog()));
        left.Children.Add(MakeToolButton(L.Get("Action.Pin"), (_, _) => PinResult()));
        left.Children.Add(new Separator { Margin = new Thickness(8, 2, 8, 2) });
        _effectsButton = MakeToolButton(L.Get("Editor.Effects"), (_, _) => ToggleEffects());
        left.Children.Add(_effectsButton);
        _openFolderButton = MakeToolButton(L.Get("Action.OpenFolder"), (_, _) => OpenLastFolder());
        left.Children.Add(_openFolderButton);
        panel.Children.Add(left);

        ActionsHost.Child = panel;
        RefreshUndoRedo();
        RefreshSelectionButtons();
    }

    private void RefreshUndoRedo()
    {
        _undoButton.IsEnabled = _canvas.Undo.CanUndo;
        _redoButton.IsEnabled = _canvas.Undo.CanRedo;
    }

    private void RefreshSelectionButtons()
    {
        if (!_ready) return;
        bool has = _canvas.Tool == EditorTool.Select && _canvas.Selected is not null;
        _deleteButton.IsEnabled = has;
        _frontButton.IsEnabled = has;
        _backButton.IsEnabled = has;
    }

    private void SetStatus(string message) => _statusLabel.Text = message;

    /// <summary>Reveals the last saved file in Explorer, or opens the default save folder.</summary>
    private void OpenLastFolder()
    {
        try
        {
            if (_lastSavedPath is { } path && System.IO.File.Exists(path))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                return;
            }
            string dir = string.IsNullOrWhiteSpace(_settings.Current.Output.SaveDirectory)
                ? AppPaths.DefaultSaveDir
                : _settings.Current.Output.SaveDirectory;
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Opening the folder failed", ex);
        }
    }

    // ---- zoom / pan ----

    private void ZoomBy(double factor)
    {
        var viewportCenter = new Point(Scroll.ViewportWidth / 2, Scroll.ViewportHeight / 2);
        var img = new PointD((Scroll.HorizontalOffset + viewportCenter.X) / _canvas.Zoom,
            (Scroll.VerticalOffset + viewportCenter.Y) / _canvas.Zoom);
        double oldZoom = _canvas.Zoom;
        _canvas.SetZoom(_canvas.Zoom * factor);
        AfterZoomChanged(oldZoom, img);
    }

    private void FitZoom()
    {
        double zw = Scroll.ViewportWidth > 40 ? (Scroll.ViewportWidth - 32) / _canvas.ImageWidth : 1;
        double zh = Scroll.ViewportHeight > 40 ? (Scroll.ViewportHeight - 32) / _canvas.ImageHeight : 1;
        double oldZoom = _canvas.Zoom;
        _canvas.SetZoom(Math.Min(1, Math.Min(zw, zh)));
        AfterZoomChanged(oldZoom, new PointD(0, 0));
        Scroll.ScrollToHome();
    }

    private void CanvasOnZoomRequested(object? sender, ZoomRequestEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        double oldZoom = _canvas.Zoom;
        _canvas.SetZoom(oldZoom * factor);
        AfterZoomChanged(oldZoom, e.ImagePoint);
    }

    private void AfterZoomChanged(double oldZoom, PointD imageAnchor)
    {
        _zoomLabel.Text = $"{_canvas.Zoom * 100:0}%";
        PositionTextOverlay();
        if (Math.Abs(_canvas.Zoom - oldZoom) > 0.00001 && _canvas.ScrollOwner is { } s)
        {
            s.ScrollToHorizontalOffset(s.HorizontalOffset + imageAnchor.X * (_canvas.Zoom - oldZoom));
            s.ScrollToVerticalOffset(s.VerticalOffset + imageAnchor.Y * (_canvas.Zoom - oldZoom));
        }
    }

    // ---- text overlay ----

    private void OpenTextOverlay(TextItem? existing, PointD position)
    {
        CloseTextOverlay(commit: false);
        _textEditTarget = existing;
        _textEditPosition = existing?.Position ?? position;
        var editor = _settings.Current.Editor;
        _textOverlay = new TextBox
        {
            AcceptsReturn = true,
            FontSize = (existing?.FontSize ?? _canvas.FontSize) * _canvas.Zoom,
            FontFamily = new FontFamily(existing?.FontFamily ?? editor.FontFamily),
            FontWeight = (existing?.Bold ?? _canvas.FontBold) == true ? FontWeights.Bold : FontWeights.Normal,
            Foreground = AnnotationRenderer.BrushFor(existing?.StrokeColor ?? _canvas.StrokeColor),
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
            BorderThickness = new Thickness(1.5),
            MinWidth = 130,
            Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        if (existing is not null) _textOverlay.Text = existing.Text;
        _textOverlay.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                e.Handled = true;
                CloseTextOverlay(commit: true);
            }
            else if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseTextOverlay(commit: false);
            }
        };
        _textOverlay.LostFocus += (_, _) => CloseTextOverlay(commit: true);
        CanvasHost.Children.Add(_textOverlay);
        PositionTextOverlay();
        _textOverlay.Focus();
    }

    private void PositionTextOverlay()
    {
        if (_textOverlay is null) return;
        _textOverlay.Margin = new Thickness(
            _textEditPosition.X * _canvas.Zoom - 2,
            _textEditPosition.Y * _canvas.Zoom - 2, 0, 0);
    }

    private void CloseTextOverlay(bool commit)
    {
        if (_textOverlay is null) return;
        var tb = _textOverlay;
        var target = _textEditTarget;
        _textOverlay = null;
        _textEditTarget = null;
        CanvasHost.Children.Remove(tb);
        if (commit) _canvas.CommitTextEdit(target, tb.Text, _textEditPosition);
    }

    // ---- effects panel ----

    private void BuildEffectsPanel()
    {
        _effectsPanel = new EffectsPanel();
        _effectsPanel.Bind(_canvas.Document.Effects, _settings);
        _effectsPanel.SettingsChanged += (_, _) => { _dirty = true; SchedulePreview(); };
        _canvas.DocumentChanged += (_, _) => SchedulePreview();

        // Wrap the properties area in a grid so the two panels can be swapped.
        var scroll = (ScrollViewer)PropertiesHost.Child;
        PropertiesHost.Child = null; // disconnect before re-parenting
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        var grid = new Grid();
        grid.Children.Add(scroll);
        grid.Children.Add(_effectsPanel);
        _effectsPanel.Visibility = Visibility.Collapsed;
        PropertiesHost.Child = grid;
    }

    private void ToggleEffects()
    {
        _effectsVisible = !_effectsVisible;
        _effectsPanel.Visibility = _effectsVisible ? Visibility.Visible : Visibility.Collapsed;
        _effectsButton.FontWeight = _effectsVisible ? FontWeights.Bold : FontWeights.Normal;
        if (_effectsVisible) SchedulePreview();
    }

    private void SchedulePreview()
    {
        if (!_effectsVisible) return;
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    /// <summary>Flattens the annotation view at reduced scale and composites effects on a background thread.</summary>
    private void RenderPreview()
    {
        int id = ++_previewRunId;
        PixelBuffer preview;
        try
        {
            preview = RenderFlattenedPreview(1200);
        }
        catch (Exception ex)
        {
            Log.Error("Preview flatten failed", ex);
            return;
        }
        var settings = _canvas.Document.Effects.Clone();
        bool apply = settings.Enabled;
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var composed = apply ? EffectPipeline.Compose(preview, settings) : preview;
                var bitmap = composed.ToBitmapSource();
                Dispatcher.BeginInvoke(() =>
                {
                    if (id == _previewRunId) _effectsPanel.SetPreview(bitmap);
                });
            }
            catch (Exception ex)
            {
                Log.Error("Preview compose failed", ex);
            }
        });
    }

    /// <summary>Same as RenderFlattened but rendered at a reduced scale (preview only).</summary>
    private PixelBuffer RenderFlattenedPreview(int maxSide)
    {
        var doc = _canvas.Document;
        var crop = doc.Crop ?? new PixelRect(0, 0, doc.ImageWidth, doc.ImageHeight);
        if (crop.IsEmpty) crop = new PixelRect(0, 0, doc.ImageWidth, doc.ImageHeight);
        double scale = Math.Min(1.0, maxSide / (double)Math.Max(crop.Width, crop.Height));
        int w = Math.Max(1, (int)Math.Round(crop.Width * scale));
        int h = Math.Max(1, (int)Math.Round(crop.Height * scale));

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            var transform = new TransformGroup();
            transform.Children.Add(new ScaleTransform(scale, scale));
            transform.Children.Add(new TranslateTransform(-crop.X, -crop.Y));
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            dc.PushTransform(transform);
            dc.DrawImage(_canvas.BaseImage, new Rect(0, 0, doc.ImageWidth, doc.ImageHeight));
            AnnotationRenderer.RenderDocument(dc, doc, _canvas.BaseImage, _canvas.MosaicSource);
            dc.Pop();
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        return BitmapInterop.FromBitmapSource(rtb);
    }

    // ---- export ----

    /// <summary>Renders base image + annotations + crop at 1:1 image pixels, then runs the Core effect pipeline.</summary>
    public PixelBuffer RenderFlattened()
    {
        var doc = _canvas.Document;
        var crop = doc.Crop ?? new PixelRect(0, 0, doc.ImageWidth, doc.ImageHeight);
        if (crop.IsEmpty) crop = new PixelRect(0, 0, doc.ImageWidth, doc.ImageHeight);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, crop.Width, crop.Height)));
            dc.PushTransform(new TranslateTransform(-crop.X, -crop.Y));
            dc.DrawImage(_canvas.BaseImage, new Rect(0, 0, doc.ImageWidth, doc.ImageHeight));
            AnnotationRenderer.RenderDocument(dc, doc, _canvas.BaseImage, _canvas.MosaicSource);
            dc.Pop();
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(crop.Width, crop.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var buffer = BitmapInterop.FromBitmapSource(rtb);
        if (_settings.Current.Output.ApplyEffectsOnExport && doc.Effects.Enabled)
            buffer = EffectPipeline.Compose(buffer, doc.Effects);
        return buffer;
    }

    private void CopyResult()
    {
        try
        {
            if (_clipboard.TryPutImage(RenderFlattened()))
            {
                _dirty = false; // exported state is the baseline for the close prompt
                SetStatus(L.Get("Toast.Copied"));
            }
            else SetStatus(L.Get("Toast.CopyFailed"));
        }
        catch (Exception ex)
        {
            Log.Error("Editor copy failed", ex);
            SetStatus(L.Get("Toast.CopyFailed"));
        }
    }

    private void SaveQuick()
    {
        try
        {
            var path = _exporter.QuickSave(RenderFlattened(), _canvas.Document.WindowTitle ?? _result.WindowTitle, _result.Mode.ToString());
            _lastSavedPath = path;
            _dirty = false;
            SetStatus(L.Get("Toast.Saved", path));
        }
        catch (Exception ex)
        {
            Log.Error("Editor quick save failed", ex);
            SetStatus(L.Get("Toast.SaveFailed", ex.Message));
        }
    }

    private void SaveAsDialog()
    {
        try
        {
            var suggested = FileNameTemplate.Format(_settings.Current.Output.FileNameTemplate, DateTime.Now,
                _settings.Current.Output.Counter, _canvas.Document.WindowTitle ?? _result.WindowTitle, _result.Mode.ToString());
            var path = _exporter.SaveAs(RenderFlattened(), suggested);
            if (path is null) return;
            _lastSavedPath = path;
            _dirty = false;
            SetStatus(L.Get("Toast.Saved", path));
        }
        catch (Exception ex)
        {
            Log.Error("Editor save-as failed", ex);
            SetStatus(L.Get("Toast.SaveFailed", ex.Message));
        }
    }

    private void PinResult()
    {
        if (_pinFactory is { } pin)
        {
            var win = pin(RenderFlattened());
            win.Show();
            win.Activate();
            _dirty = false;
            SetStatus(L.Get("Toast.Pinned"));
            return;
        }
        Log.Info("No pin factory registered; copying instead.");
        CopyResult();
    }

    // ---- keyboard ----

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is TextBoxBase) return; // let the text editor work
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z:
                    e.Handled = true;
                    if (shift) _canvas.DoRedo();
                    else _canvas.DoUndo();
                    return;
                case Key.Y: e.Handled = true; _canvas.DoRedo(); return;
                case Key.C: e.Handled = true; CopyResult(); return;
                case Key.S: e.Handled = true; if (shift) SaveAsDialog(); else SaveQuick(); return;
                case Key.P: e.Handled = true; PinResult(); return;
            }
            return;
        }
        switch (e.Key)
        {
            case Key.Escape:
                CloseTextOverlay(commit: false);
                _canvas.AbortInteraction();
                e.Handled = true;
                return;
            case Key.Delete or Key.Back when _canvas.Selected is not null && _canvas.Tool == EditorTool.Select:
                e.Handled = true;
                _canvas.DeleteSelected();
                return;
            case Key.Enter when _canvas.Tool == EditorTool.Crop:
                e.Handled = true;
                _canvas.ApplyCrop();
                FitZoom();
                return;
            case Key.Space when Keyboard.FocusedElement is not System.Windows.Controls.Primitives.ButtonBase:
                // Don't hijack space while a button/checkbox has focus; otherwise it pans the canvas.
                _canvas.SpaceHeld = true;
                e.Handled = true;
                return;
        }
        EditorTool? tool = e.Key switch
        {
            Key.V => EditorTool.Select,
            Key.R => EditorTool.Rect,
            Key.E => EditorTool.Ellipse,
            Key.L => EditorTool.Line,
            Key.A => EditorTool.Arrow,
            Key.P => EditorTool.Pen,
            Key.T => EditorTool.Text,
            Key.N => EditorTool.Step,
            Key.H => EditorTool.Highlighter,
            Key.M => EditorTool.Mosaic,
            Key.B => EditorTool.Blur,
            Key.S => EditorTool.Spotlight,
            Key.G => EditorTool.Magnifier,
            Key.C => EditorTool.Crop,
            _ => null,
        };
        if (tool is { } t)
        {
            e.Handled = true;
            CloseTextOverlay(commit: true);
            SetTool(t);
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        if (e.Key == Key.Space) _canvas.SpaceHeld = false;
        base.OnPreviewKeyUp(e);
    }

    private void SetTool(EditorTool tool)
    {
        if (_toolButtons.TryGetValue(tool, out var rb)) rb.IsChecked = true;
    }

    // ---- lifecycle ----

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_dirty)
        {
            var answer = MessageBox.Show(this, L.Get("Editor.UnsavedBody"), L.Get("Editor.UnsavedTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (answer == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        CloseTextOverlay(commit: false);
        _previewTimer.Stop();
        // Remember the last-used effect settings for the next capture.
        _settings.Current.Effects = _canvas.Document.Effects.Clone();
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Persisting editor settings on close failed", ex);
        }
        base.OnClosed(e);
    }
}
