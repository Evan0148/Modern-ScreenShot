using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Editor;
using ModernScreenShot.App.Interop;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Settings;
using CaptureMode = ModernScreenShot.Core.Settings.CaptureMode;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Overlay;

/// <summary>
/// Borderless topmost window covering exactly one monitor. Placement is done through SetWindowPos
/// in physical pixels (before the window becomes visible, so it never flashes); everything inside
/// is drawn and hit-tested in DIPs and converted through <see cref="Scale"/>.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private readonly OverlaySession _session;
    private readonly OverlayRenderer _renderer;
    private readonly Border _toolbarHost;
    private readonly Canvas _toolbarLayer;
    private readonly TranslateTransform _toolbarOffset = new();
    private Size _toolbarSize;
    private bool _toolbarShown;
    private double _dpiCache = -1;

    // Mouse-move coalescing: PreviewMouseMove fires far more often than the display refreshes, and
    // each event drove a full session update + repaint of every overlay window, which is what made
    // the selection/toolbar feel jumpy. We record the latest cursor position and flush it once per
    // rendered frame instead.
    private bool _movePending;
    private VPoint _pendingMove;
    private bool _renderHooked;

    internal MonitorInfo Monitor { get; }

    public OverlayWindow(OverlaySession session, MonitorInfo monitor, BitmapSource frozenSource, bool activate)
    {
        _session = session;
        Monitor = monitor;
        Title = "ModernScreenShot Overlay";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = activate;
        Topmost = true;
        UseLayoutRounding = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;

        var scale = monitor.DpiX / 96.0;
        Width = monitor.Bounds.Width / scale;
        Height = monitor.Bounds.Height / scale;

        _renderer = new OverlayRenderer(session, this, frozenSource);
        _toolbarHost = BuildToolbar();
        // Canvas + TranslateTransform lets the toolbar move by only updating a transform, which
        // skips the full window Measure/Arrange pass that a Margin change would trigger every frame.
        _toolbarLayer = new Canvas { IsHitTestVisible = true };
        _toolbarHost.RenderTransform = _toolbarOffset;
        _toolbarLayer.Children.Add(_toolbarHost);
        var grid = new Grid();
        grid.Children.Add(_renderer);
        grid.Children.Add(_toolbarLayer);
        Content = grid;

        SourceInitialized += (_, _) => PlaceWindow(activate);
        Loaded += (_, _) =>
        {
            _dpiCache = -1;
            if (!_renderHooked) { CompositionTarget.Rendering += OnRenderingFrame; _renderHooked = true; }
            if (activate) { Activate(); _renderer.Focus(); }
        };
        DpiChanged += (_, _) => _dpiCache = -1;
        _session.AnnotationChanged += OnAnnotationChanged;
        _session.Undo.Changed += OnAnnotationChanged;
        _session.TextEditRequested += OnTextEditRequested;
        Closed += (_, _) =>
        {
            if (_renderHooked) { CompositionTarget.Rendering -= OnRenderingFrame; _renderHooked = false; }
            _session.AnnotationChanged -= OnAnnotationChanged;
            _session.Undo.Changed -= OnAnnotationChanged;
            _session.TextEditRequested -= OnTextEditRequested;
            _session.OnWindowClosed(this);
        };

        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseLeftButtonDown += OnPreviewLeftButtonDown;
        PreviewMouseLeftButtonUp += OnPreviewLeftButtonUp;
        PreviewMouseMove += OnPreviewMouseMove;
        PreviewMouseRightButtonDown += (_, e) => e.Handled = true;
        PreviewMouseRightButtonUp += (_, e) => { e.Handled = true; _session.Cancel(); };
    }

    /// <summary>Window's current DPI scale (physical px per DIP).</summary>
    internal double Scale
    {
        get
        {
            if (_dpiCache <= 0)
            {
                if (IsLoaded)
                {
                    try
                    {
                        var dpi = VisualTreeHelper.GetDpi(this);
                        if (dpi.DpiScaleX > 0) _dpiCache = dpi.DpiScaleX;
                    }
                    catch (Exception ex)
                    {
                        Services.Log.Warn($"VisualTreeHelper.GetDpi failed: {ex.Message}");
                    }
                }
                if (_dpiCache <= 0) _dpiCache = Monitor.DpiX / 96.0;
            }
            return _dpiCache;
        }
    }

    internal Point ToLocalDip(VPoint p) => new(
        (p.X - Monitor.Bounds.X) / Scale,
        (p.Y - Monitor.Bounds.Y) / Scale);

    internal Rect ToLocalDip(PixelRect virtualRect)
    {
        double s = Scale;
        return new Rect(
            (virtualRect.X - Monitor.Bounds.X) / s,
            (virtualRect.Y - Monitor.Bounds.Y) / s,
            virtualRect.Width / s,
            virtualRect.Height / s);
    }

    internal VPoint ToVirtual(Point dip) => new(
        Monitor.Bounds.X + (int)Math.Round(dip.X * Scale),
        Monitor.Bounds.Y + (int)Math.Round(dip.Y * Scale));

    /// <summary>Refreshes visuals after any session state change.</summary>
    internal void OnSessionChanged()
    {
        ApplyCursorShape();
        UpdateToolbar();
        // Only repaint this monitor's renderer when something it actually draws changed. During a
        // drag the cursor/selection changes on one or two monitors; the rest can skip the frame.
        if (_renderer.StateChanged()) _renderer.InvalidateVisual();
    }

    private void PlaceWindow(bool activate)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        uint flags = NativeMethods.SWP_SHOWWINDOW | (activate ? 0u : NativeMethods.SWP_NOACTIVATE);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, Monitor.Bounds.X, Monitor.Bounds.Y,
            Monitor.Bounds.Width, Monitor.Bounds.Height, flags);
        long ex = NativeMethods.GetExStyle(hwnd);
        long add = NativeMethods.WS_EX_TOOLWINDOW | (activate ? 0 : NativeMethods.WS_EX_NOACTIVATE);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, (IntPtr)(ex | add));
        if (activate) NativeMethods.SetForegroundWindow(hwnd);
    }

    // ---- input ----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_textOverlay is not null && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return; // typing
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.Z:
                    e.Handled = true;
                    if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _session.DoRedo();
                    else _session.DoUndo();
                    return;
                case Key.Y:
                    e.Handled = true;
                    _session.DoRedo();
                    return;
            }
        }
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_session.OnKey(e.Key, shift)) e.Handled = true;
    }

    private void OnPreviewLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_textOverlay is not null)
        {
            CloseTextOverlay(commit: true);
            if (IsOverToolbar(e)) return; // let the toolbar button receive the click
            e.Handled = true;             // the click elsewhere only finishes the text
            return;
        }
        if (IsOverToolbar(e)) return;
        var p = ToVirtual(e.GetPosition(_renderer));
        if (e.ClickCount >= 2) _session.OnDoubleClick(this, p);
        else
        {
            _session.OnLeftDown(this, p);
            CaptureMouse();
        }
    }

    private void OnPreviewLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (IsOverToolbar(e)) return;
        _session.OnLeftUp(this, ToVirtual(e.GetPosition(_renderer)));
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        // Coalesce to one session update per rendered frame (see _movePending). During an active
        // drag the mouse is captured by THIS window, so all move events land here; recording the
        // latest position and flushing on the render tick keeps the selection glued to the cursor
        // without the multi-event-per-frame thrash.
        _pendingMove = ToVirtual(e.GetPosition(_renderer));
        _movePending = true;
    }

    private void OnRenderingFrame(object? sender, EventArgs e)
    {
        if (!_movePending) return;
        _movePending = false;
        _session.OnMove(this, _pendingMove);
    }

    private bool IsOverToolbar(MouseButtonEventArgs e) =>
        _toolbarHost.Visibility == Visibility.Visible
        && e.OriginalSource is DependencyObject d
        && _toolbarHost.IsAncestorOf(d);

    private void ApplyCursorShape()
    {
        var cur = _session.Cursor;
        if (_session.Tool is not null) { Cursor = Cursors.Cross; return; }
        if (_session.CursorValid && !Monitor.Bounds.Contains(cur.X, cur.Y))
        {
            Cursor = Cursors.Cross;
            return;
        }
        if (_session.State != OverlayState.Selected || _session.Selection is not { } sel)
        {
            Cursor = Cursors.Cross;
            return;
        }
        Cursor = OverlaySession.ComputeHandle(sel, cur) switch
        {
            HandleDir.N or HandleDir.S => Cursors.SizeNS,
            HandleDir.E or HandleDir.W => Cursors.SizeWE,
            HandleDir.NW or HandleDir.SE => Cursors.SizeNWSE,
            HandleDir.NE or HandleDir.SW => Cursors.SizeNESW,
            HandleDir.Body => Cursors.SizeAll,
            _ => Cursors.Cross,
        };
    }

    // ---- toolbar ----

    private static readonly Brush ActiveToolBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE6, 0x0A, 0x84, 0xFF)));
    private static readonly Brush HoverBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush FillHintBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush SwatchBorderBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF)));
    private readonly Dictionary<EditorTool, Button> _toolButtons = [];
    private Button _undoButton = null!, _redoButton = null!;
    private TextBox? _textOverlay;

    // Options bar: every control is built once with the toolbar and only its Visibility/IsEnabled/
    // background is touched afterwards — this runs on the overlay hot path (per rendered frame).
    private WrapPanel _optionsRow = null!;
    private StackPanel _thicknessOptions = null!, _fontOptions = null!, _stepOptions = null!,
        _mosaicModeOptions = null!, _mosaicStrengthOptions = null!;
    private Button _fillToggle = null!, _dashedToggle = null!, _pixelateToggle = null!, _blurToggle = null!;
    private WrapPanel _palettePanel = null!;
    private readonly List<Button> _swatches = [];
    private readonly List<(Button Button, double Value)> _thicknessDots = [];
    private readonly List<(Button Button, double Value)> _fontButtons = [];
    private readonly List<(Button Button, double Value)> _stepDots = [];
    private readonly List<(Button Button, int Value)> _mosaicDots = [];
    private (bool Thickness, bool Fill, bool Dashed, bool Font, bool Step, bool Mosaic, bool Palette) _optionsSig;

    /// <summary>Snipaste-style icon strip: annotation tools, undo/redo, then the output actions, with a
    /// second row for the active tool's options. Each row is a WrapPanel + the host's MaxWidth, so a
    /// narrow window wraps rows onto further lines instead of clipping.</summary>
    private Border BuildToolbar()
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        void Sep() => panel.Children.Add(new Separator { Margin = new Thickness(2, 7, 2, 7) });

        AddToolButton(panel, EditorTool.Select, SelectIcon());
        AddToolButton(panel, EditorTool.Rect, ShapeIcon(new RectangleGeometry(new Rect(2.5, 4.5, 13, 9))));
        AddToolButton(panel, EditorTool.Ellipse, ShapeIcon(new EllipseGeometry(new Point(9, 9), 7, 5.5)));
        AddToolButton(panel, EditorTool.Line, ShapeIcon(Geo(g => { g.BeginFigure(new Point(3, 14), false, false); g.LineTo(new Point(16, 2), true, false); })));
        AddToolButton(panel, EditorTool.Arrow, ShapeIcon(ArrowGeometry()));
        AddToolButton(panel, EditorTool.Pen, GlyphIcon("\uE70F"));
        AddToolButton(panel, EditorTool.Highlighter, GlyphIcon("\uE7E6"));
        AddToolButton(panel, EditorTool.Text, TextIcon("T"));
        AddToolButton(panel, EditorTool.Step, TextIcon("\u2460"));
        AddToolButton(panel, EditorTool.Mosaic, MosaicIcon());
        AddToolButton(panel, EditorTool.Eraser, GlyphIcon("\uE74D"));
        Sep();
        _undoButton = MakeIconButton("\uE7A7", "Action.Undo", (_, _) => _session.DoUndo());
        _redoButton = MakeIconButton("\uE7A6", "Action.Redo", (_, _) => _session.DoRedo());
        panel.Children.Add(_undoButton);
        panel.Children.Add(_redoButton);
        Sep();
        panel.Children.Add(MakeIconButton("\uE8C8", "Action.Copy", (_, _) => _session.Confirm(OverlayIntent.Copy)));
        panel.Children.Add(MakeIconButton("\uE74E", "Action.Save", (_, _) => _session.Confirm(OverlayIntent.Save)));
        panel.Children.Add(MakeIconButton("\uE718", "Action.Pin", (_, _) => _session.Confirm(OverlayIntent.Pin)));
        panel.Children.Add(MakeIconButton("\uE73E", "Action.Edit", (_, _) => _session.Confirm(OverlayIntent.Edit)));
        panel.Children.Add(MakeIconButton("\uE711", "Action.Cancel", (_, _) => _session.Cancel()));

        _optionsRow = BuildOptionsBar();
        var rows = new StackPanel { Orientation = Orientation.Vertical };
        rows.Children.Add(panel);
        rows.Children.Add(_optionsRow);

        return new Border
        {
            Child = rows,
            Background = new SolidColorBrush(Color.FromArgb(0xEA, 0x1C, 0x1C, 0x1E)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(5, 4, 5, 4),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Focusable = false,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Direction = 270, Opacity = 0.45 },
        };
    }

    private Button AddToolButton(WrapPanel panel, EditorTool tool, FrameworkElement icon)
    {
        var button = MakeIconButton(null, $"Tool.{tool}", (_, _) =>
            _session.Tool = _session.Tool == tool ? null : tool);
        button.Content = icon;
        _toolButtons[tool] = button;
        panel.Children.Add(button);
        return button;
    }

    // ---- options bar (second toolbar row; one row per active tool) ----

    /// <summary>Builds every tool's option controls once. Values live in the shared
    /// <see cref="EditorSettings"/> instance, so strokes created afterwards pick them up and they
    /// survive into the editor; <see cref="OverlaySession.MarkOptionsChanged"/> triggers persistence
    /// when the session is confirmed.</summary>
    private WrapPanel BuildOptionsBar()
    {
        var row = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 3, 2, 1),
            Visibility = Visibility.Collapsed,
        };

        // Stroke thickness presets (2/4/8/14 px) shared by shape/stroke tools; the dot size hints the width.
        _thicknessOptions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (value, dot) in new[] { (2.0, 4.0), (4.0, 7.0), (8.0, 10.0), (14.0, 14.0) })
        {
            var button = MakeOptionButton(DotIcon(dot), "Prop.Thickness", (_, _) => SetOption(e => e.StrokeThickness = value));
            _thicknessDots.Add((button, value));
            _thicknessOptions.Children.Add(button);
        }
        row.Children.Add(_thicknessOptions);

        _fillToggle = MakeOptionButton(FillIcon(), "Prop.Fill", (_, _) => SetOption(e => e.FillShape = !e.FillShape));
        row.Children.Add(_fillToggle);

        _dashedToggle = MakeOptionButton(DashedIcon(), "Prop.Dashed", (_, _) => SetOption(e => e.DashedLine = !e.DashedLine));
        row.Children.Add(_dashedToggle);

        // Text font size presets (14/20/28/40), label glyphs scaled so the numbers read at a glance.
        _fontOptions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (value, glyph) in new[] { (14.0, 10.0), (20.0, 12.0), (28.0, 14.0), (40.0, 16.0) })
        {
            var button = MakeOptionButton(SizeLabel(value.ToString("0"), glyph), "Prop.FontSize", (_, _) => SetOption(e => e.FontSize = value));
            _fontButtons.Add((button, value));
            _fontOptions.Children.Add(button);
        }
        row.Children.Add(_fontOptions);

        // Step marker radius presets.
        _stepOptions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (value, dot) in new[] { (10.0, 8.0), (16.0, 11.0), (24.0, 14.0) })
        {
            var button = MakeOptionButton(CircleIcon(dot), "Prop.Radius", (_, _) => SetOption(e => e.StepRadius = value));
            _stepDots.Add((button, value));
            _stepOptions.Children.Add(button);
        }
        row.Children.Add(_stepOptions);

        // Mosaic mode + strength presets.
        _pixelateToggle = MakeOptionButton(MosaicIcon(), "Prop.Pixelate", (_, _) => SetOption(e => e.MosaicPixelate = true));
        _blurToggle = MakeOptionButton(BlurIcon(), "Prop.BlurMode", (_, _) => SetOption(e => e.MosaicPixelate = false));
        _mosaicModeOptions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _mosaicModeOptions.Children.Add(_pixelateToggle);
        _mosaicModeOptions.Children.Add(_blurToggle);
        row.Children.Add(_mosaicModeOptions);

        _mosaicStrengthOptions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        foreach (var (value, dot) in new[] { (4, 5.0), (8, 8.0), (12, 11.0), (24, 14.0) })
        {
            var button = MakeOptionButton(DotIcon(dot), "Prop.Strength", (_, _) => SetOption(e => e.MosaicCellSize = value));
            _mosaicDots.Add((button, value));
            _mosaicStrengthOptions.Children.Add(button);
        }
        row.Children.Add(_mosaicStrengthOptions);

        // Palette from the persisted settings; the swatch matching the current color gets a thicker border.
        _palettePanel = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(3, 0, 0, 0) };
        foreach (var hex in _session.Editor.Palette)
        {
            var sw = new Button
            {
                Width = 18,
                Height = 18,
                MinWidth = 0,
                Padding = new Thickness(0),
                Margin = new Thickness(0, 0, 3, 0),
                Background = AnnotationRenderer.BrushFor(hex),
                BorderBrush = SwatchBorderBrush,
                BorderThickness = new Thickness(1),
                Tag = hex,
                Cursor = Cursors.Hand,
                Focusable = false,
                ToolTip = hex,
            };
            sw.Click += (_, _) => SetOption(e => e.StrokeColor = hex);
            _swatches.Add(sw);
            _palettePanel.Children.Add(sw);
        }
        row.Children.Add(_palettePanel);
        return row;
    }

    /// <summary>Applies one options-bar change to the shared EditorSettings instance.</summary>
    private void SetOption(Action<ModernScreenShot.Core.Settings.EditorSettings> apply)
    {
        apply(_session.Editor);
        _session.MarkOptionsChanged();
        RefreshToolbarState();
    }

    /// <summary>Shows the option groups of the active tool and syncs their highlights. Select and
    /// Eraser have no options, so the whole row collapses.</summary>
    private void UpdateOptionsRow()
    {
        var tool = _session.Tool;
        bool thickness = tool is EditorTool.Rect or EditorTool.Ellipse or EditorTool.Line or EditorTool.Arrow
            or EditorTool.Pen or EditorTool.Highlighter;
        bool fill = tool is EditorTool.Rect or EditorTool.Ellipse;
        bool dashed = tool is EditorTool.Line or EditorTool.Arrow;
        bool font = tool == EditorTool.Text;
        bool step = tool == EditorTool.Step;
        bool mosaic = tool == EditorTool.Mosaic;
        bool palette = thickness || font || step;
        var sig = (thickness, fill, dashed, font, step, mosaic, palette);

        SetVisible(_thicknessOptions, thickness);
        SetVisible(_fillToggle, fill);
        SetVisible(_dashedToggle, dashed);
        SetVisible(_fontOptions, font);
        SetVisible(_stepOptions, step);
        SetVisible(_mosaicModeOptions, mosaic);
        SetVisible(_mosaicStrengthOptions, mosaic);
        SetVisible(_palettePanel, palette);
        if (sig != _optionsSig)
        {
            _optionsSig = sig;
            _optionsRow.Visibility = sig == default ? Visibility.Collapsed : Visibility.Visible;
            _toolbarSize = default; // the toolbar's size changed: force a re-measure before positioning
        }
        if (sig != default) SyncOptionHighlights();
    }

    private void SyncOptionHighlights()
    {
        var ed = _session.Editor;
        foreach (var (b, v) in _thicknessDots) b.Background = EqD(v, ed.StrokeThickness) ? ActiveToolBrush : Brushes.Transparent;
        _fillToggle.Background = ed.FillShape ? ActiveToolBrush : Brushes.Transparent;
        _dashedToggle.Background = ed.DashedLine ? ActiveToolBrush : Brushes.Transparent;
        foreach (var (b, v) in _fontButtons) b.Background = EqD(v, ed.FontSize) ? ActiveToolBrush : Brushes.Transparent;
        foreach (var (b, v) in _stepDots) b.Background = EqD(v, ed.StepRadius) ? ActiveToolBrush : Brushes.Transparent;
        _pixelateToggle.Background = ed.MosaicPixelate ? ActiveToolBrush : Brushes.Transparent;
        _blurToggle.Background = ed.MosaicPixelate ? Brushes.Transparent : ActiveToolBrush;
        foreach (var (b, v) in _mosaicDots) b.Background = v == ed.MosaicCellSize ? ActiveToolBrush : Brushes.Transparent;
        foreach (var sw in _swatches)
            sw.BorderThickness = string.Equals((string)sw.Tag, ed.StrokeColor, StringComparison.OrdinalIgnoreCase)
                ? new Thickness(2)
                : new Thickness(1);
    }

    private static bool EqD(double a, double b) => Math.Abs(a - b) < 0.01;

    private static void SetVisible(UIElement el, bool visible) => el.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    private Button MakeOptionButton(FrameworkElement content, string tooltipKey, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Width = 24,
            MinWidth = 0,               // the WPF-UI style inflates buttons; keep the strip compact
            Padding = new Thickness(0),
            Height = 24,
            Margin = new Thickness(1, 0, 1, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            Focusable = false,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = L.Get(tooltipKey),
        };
        button.Content = content;
        button.Click += onClick;
        return button;
    }

    private static FrameworkElement DotIcon(double diameter) => new System.Windows.Shapes.Ellipse
    {
        Width = diameter,
        Height = diameter,
        Fill = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static FrameworkElement CircleIcon(double diameter) => new System.Windows.Shapes.Ellipse
    {
        Width = diameter,
        Height = diameter,
        Stroke = Brushes.White,
        StrokeThickness = 1.6,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static FrameworkElement SizeLabel(string text, double size) => new TextBlock
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = size,
        FontWeight = FontWeights.SemiBold,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Canvas FillIcon()
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        canvas.Children.Add(new System.Windows.Shapes.Rectangle
        {
            Width = 12,
            Height = 12,
            Margin = new Thickness(3),
            RadiusX = 1,
            RadiusY = 1,
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            Fill = FillHintBrush,
        });
        return canvas;
    }

    private static Canvas DashedIcon()
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geo(g =>
            {
                g.BeginFigure(new Point(2.5, 13.5), false, false);
                g.LineTo(new Point(15.5, 4.5), true, false);
            }),
            Stroke = Brushes.White,
            StrokeThickness = 1.8,
            StrokeDashArray = new DoubleCollection { 2.6, 2.2 },
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
        };
        return new Canvas { Width = 18, Height = 18, Children = { path } };
    }

    private static Canvas BlurIcon()
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        for (int i = 0; i < 3; i++)
        {
            double size = 14 - i * 4.5;
            canvas.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                Margin = new Thickness((18 - size) / 2, (18 - size) / 2, 0, 0),
                Fill = Freeze(new SolidColorBrush(Color.FromArgb((byte)(0x48 + i * 0x46), 0xFF, 0xFF, 0xFF))),
            });
        }
        return canvas;
    }

    private Button MakeIconButton(string? glyph, string tooltipKey, RoutedEventHandler onClick)
    {
        var button = new Button
        {
            Width = 30,
            MinWidth = 0,               // the WPF-UI style inflates buttons; keep the strip compact
            Padding = new Thickness(0), // so it never gets clipped on narrow windows
            Height = 28,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = L.Get(tooltipKey),
        };
        if (glyph is not null)
        {
            button.Content = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }
        button.Click += onClick;
        button.MouseEnter += (_, _) => { if (!ReferenceEquals(ActiveToolButton(), button)) button.Background = HoverBrush; };
        button.MouseLeave += (_, _) => { if (!ReferenceEquals(ActiveToolButton(), button)) button.Background = Brushes.Transparent; };
        return button;
    }

    private Button? ActiveToolButton() =>
        _session.Tool is { } tool && _toolButtons.TryGetValue(tool, out var b) ? b : null;

    /// <summary>Syncs tool highlight, undo/redo enablement and the options row with the session state.</summary>
    private void RefreshToolbarState()
    {
        var active = ActiveToolButton();
        foreach (var (_, button) in _toolButtons)
            button.Background = ReferenceEquals(button, active) ? ActiveToolBrush : Brushes.Transparent;
        _undoButton.IsEnabled = _session.Undo.CanUndo;
        _redoButton.IsEnabled = _session.Undo.CanRedo;
        UpdateOptionsRow();
    }

    // icon factories — plain shapes/text so no exotic glyphs are needed

    private static TextBlock GlyphIcon(string glyph) => new()
    {
        Text = glyph,
        FontFamily = new FontFamily("Segoe MDL2 Assets"),
        FontSize = 15,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(0, 0, 0, 0),
    };

    private static TextBlock TextIcon(string text) => new()
    {
        Text = text,
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 15,
        FontWeight = FontWeights.Bold,
        Foreground = Brushes.White,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Canvas ShapeIcon(Geometry geo)
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = geo,
            Stroke = Brushes.White,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        return new Canvas { Width = 18, Height = 18, Children = { path } };
    }

    private static Geometry Geo(Action<StreamGeometryContext> build)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open()) build(ctx);
        return geo;
    }

    private static Geometry ArrowGeometry() => Geo(g =>
    {
        g.BeginFigure(new Point(2, 14), false, false);
        g.LineTo(new Point(13, 3), true, false);
        g.LineTo(new Point(13, 8), true, false);
        g.LineTo(new Point(16, 2), true, false);
        g.LineTo(new Point(10, 3), true, false);
    });

    private static Canvas SelectIcon()
    {
        var path = new System.Windows.Shapes.Path
        {
            Data = Geo(g =>
            {
                g.BeginFigure(new Point(4, 2), false, false);
                g.LineTo(new Point(4, 15), true, false);
                g.LineTo(new Point(7.5, 11.5), true, false);
                g.LineTo(new Point(10, 16), true, false);
                g.LineTo(new Point(12, 15), true, false);
                g.LineTo(new Point(9.5, 10.5), true, false);
                g.LineTo(new Point(14, 10), true, false);
            }),
            Stroke = Brushes.White,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
        };
        return new Canvas { Width = 18, Height = 18, Children = { path } };
    }

    private static Canvas MosaicIcon()
    {
        var canvas = new Canvas { Width = 18, Height = 18 };
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 3; x++)
            {
                canvas.Children.Add(new System.Windows.Shapes.Rectangle
                {
                    Width = 4.4,
                    Height = 4.4,
                    Margin = new Thickness(2 + x * 5, 2 + y * 5, 0, 0),
                    Fill = x == 1 && y == 1 ? Brushes.White : new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
                });
            }
        }
        return canvas;
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    /// <summary>Positions the toolbar relative to the selection; only the monitor holding the
    /// selection's bottom-right corner shows it. Hidden entirely when the session auto-confirms
    /// (scrolling capture selects a region only).</summary>
    internal void UpdateToolbar()
    {
        if (_session.AutoConfirmOnSelect
            || _session.Selection is not { } sel
            || _session.State != OverlayState.Selected
            || _session.Mode != CaptureMode.Region
            || !Monitor.Bounds.Contains(sel.Right - 1, sel.Bottom - 1))
        {
            if (_toolbarShown)
            {
                _toolbarHost.Visibility = Visibility.Collapsed;
                _toolbarShown = false;
            }
            return;
        }
        if (!_toolbarShown)
        {
            _toolbarHost.Visibility = Visibility.Visible;
            _toolbarShown = true;
        }
        // Refresh first: toggling the options row can invalidate the cached size, which must then be
        // re-measured below in the same pass so the toolbar is never positioned with a stale size.
        RefreshToolbarState();
        // Measure once per size; the toolbar only changes size when the options row appears/disappears
        // (see _optionsSig) or the window is resized. MaxWidth forces each WrapPanel row to wrap when
        // the window is too narrow — otherwise the right-hand buttons would be clipped off-window.
        if (_toolbarSize.Width <= 0)
        {
            _toolbarHost.MaxWidth = Math.Max(120, ActualWidth - 8);
            _toolbarHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _toolbarSize = _toolbarHost.DesiredSize;
        }
        var size = _toolbarSize;
        var local = ToLocalDip(sel);
        double x = local.Right - size.Width - 2;
        double y = local.Bottom + 8;
        if (y + size.Height > ActualHeight - 4) y = local.Top - size.Height - 8;
        x = Math.Clamp(x, 4, Math.Max(4, ActualWidth - size.Width - 4));
        y = Math.Clamp(y, 4, Math.Max(4, ActualHeight - size.Height - 4));
        _toolbarOffset.X = x;
        _toolbarOffset.Y = y;
    }

    // ---- inline annotation UI ----

    private Core.Annotation.PointD _textEditPosition;

    private void OnAnnotationChanged(object? sender, EventArgs e) => RefreshToolbarState();

    private void OnTextEditRequested(object? sender, TextEditRequestEventArgs e)
    {
        CloseTextOverlay(commit: false);
        _textEditPosition = e.Position;
        double scale = Scale;
        var sel = _session.Selection!.Value;
        _textOverlay = new TextBox
        {
            AcceptsReturn = true,
            FontSize = _session.EditorFontSize / scale,
            FontFamily = new FontFamily(_session.EditorFontFamily),
            FontWeight = _session.EditorFontBold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = AnnotationRenderer.BrushFor(_session.EditorStrokeColor),
            Background = new SolidColorBrush(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x84, 0xFF)),
            BorderThickness = new Thickness(1.5),
            MinWidth = 120,
            Padding = new Thickness(2, 0, 2, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _textOverlay.KeyDown += (_, args) =>
        {
            if (args.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
            {
                args.Handled = true;
                CloseTextOverlay(commit: true);
            }
            else if (args.Key == Key.Escape)
            {
                args.Handled = true;
                CloseTextOverlay(commit: false);
            }
        };
        _textOverlay.LostFocus += (_, _) => CloseTextOverlay(commit: true);
        _toolbarLayer.Children.Add(_textOverlay);
        var local = ToLocalDip(new PixelRect(sel.X + (int)e.Position.X, sel.Y + (int)e.Position.Y, 0, 0));
        Canvas.SetLeft(_textOverlay, Math.Clamp(local.X, 2, Math.Max(2, ActualWidth - 140)));
        Canvas.SetTop(_textOverlay, Math.Clamp(local.Y, 2, Math.Max(2, ActualHeight - 60)));
        _textOverlay.Focus();
    }

    private void CloseTextOverlay(bool commit)
    {
        if (_textOverlay is null) return;
        var tb = _textOverlay;
        var pos = _textEditPosition;
        _textOverlay = null;
        _toolbarLayer.Children.Remove(tb);
        if (commit) _session.CommitText(pos, tb.Text);
        _session.InvalidateAll();
    }
}
