using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
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
            // Snipaste-style: the shortcut card is visible from the start on the monitor the user
            // is looking at (the activated one); F1 re-toggles it, the first click dismisses it.
            // Scrolling capture (auto-confirm) has no selection interactions to advertise.
            if (activate)
            {
                Activate();
                _renderer.Focus();
                if (!_session.AutoConfirmOnSelect) ShowHelpPanel();
            }
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
        PreviewMouseRightButtonUp += (_, e) =>
        {
            e.Handled = true;
            // Right-click over the open flyout (or the strip) dismisses it instead of the session.
            if (_geometryMenu is not null && (IsOverGeometryMenu(e) || IsOverToolbar(e)))
            {
                CloseGeometryMenu();
                return;
            }
            _session.Cancel();
        };
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

        // High capture priority (elevated only): re-assert HWND_TOPMOST and force foreground so this
        // overlay sits above other always-on-top windows. Within the topmost band z-order is "last
        // asserted wins", so a second SetWindowPos after the initial placement lifts us over peers.
        // UIPI still blocks rising over *higher*-integrity windows, which is why the toggle requires
        // elevation (CaptureService only sets CapturePriority when the process is elevated).
        if (_session.CapturePriority)
        {
            NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, Monitor.Bounds.X, Monitor.Bounds.Y,
                Monitor.Bounds.Width, Monitor.Bounds.Height,
                NativeMethods.SWP_SHOWWINDOW | NativeMethods.SWP_NOACTIVATE);
            if (activate) NativeMethods.SetForegroundWindow(hwnd);
        }
    }

    // ---- input ----

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_textOverlay is not null && Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return; // typing
        // Esc with the geometry flyout open only dismisses the flyout; a second Esc cancels the session.
        if (e.Key == Key.Escape && _geometryMenu is not null)
        {
            e.Handled = true;
            CloseGeometryMenu();
            return;
        }
        // F1 toggles this window's help card — only the monitor window that received the key shows it.
        if (e.Key == Key.F1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            e.Handled = true;
            ToggleHelpPanel();
            return;
        }
        // While the help card is visible, Esc still cancels the session outright (Snipaste
        // semantics): the card is passive scenery, and swallowing Esc for it would force users to
        // press Esc twice to abort a capture. The card disappears with the session; F1 or any
        // click dismisses it earlier.
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
                case Key.A when _session.Mode == CaptureMode.Region:
                    e.Handled = true;
                    _session.SelectFullMonitor(Monitor.Bounds); // the monitor this window covers
                    return;
            }
        }
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_session.OnKey(e.Key, shift)) e.Handled = true;
    }

    private void OnPreviewLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // A click anywhere closes the F1 help card — including one on another monitor's window —
        // and then proceeds with its normal meaning (snap, tool press, ...).
        _session.HideHelpPanels();
        if (_textOverlay is not null)
        {
            // Clicks inside the text box must reach it (caret move / text selection) — only a
            // click outside commits and closes.
            if (e.OriginalSource is DependencyObject src && _textOverlay.IsAncestorOf(src)) return;
            CloseTextOverlay(commit: true);
            if (IsOverToolbar(e)) return; // let the toolbar button receive the click
            e.Handled = true;             // the click elsewhere only finishes the text
            return;
        }
        if (IsOverToolbar(e)) return;
        if (_geometryMenu is not null)
        {
            if (IsOverGeometryMenu(e)) return; // the flyout rows handle their own clicks
            CloseGeometryMenu();               // the first click outside only dismisses the flyout
            e.Handled = true;
            return;
        }
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
        // While a drag is in flight this window holds mouse capture, and the release can land over
        // the visible toolbar (the natural end of a bottom-right resize drag). Swallowing it would
        // leave the selection glued to the cursor with no button held — finish the drag instead;
        // the toolbar early-return only applies to plain uncaptured clicks.
        if (!IsMouseCaptured && (IsOverToolbar(e) || IsOverGeometryMenu(e))) return;
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
        if (_movePending)
        {
            _movePending = false;
            _session.OnMove(this, _pendingMove);
        }
        // Keep repainting while the hover box is gliding to its new element, even after the mouse
        // stops moving — otherwise the animation would freeze mid-transition.
        else if (_renderer.HoverAnimating)
        {
            _renderer.InvalidateVisual();
        }
    }

    private bool IsOverToolbar(MouseButtonEventArgs e) =>
        _toolbarHost.Visibility == Visibility.Visible
        && e.OriginalSource is DependencyObject d
        && _toolbarHost.IsAncestorOf(d);

    private void ApplyCursorShape()
    {
        // Over the toolbar the pointer is a plain arrow: the crosshair/resize shapes only make
        // sense on the selection surface, and the buttons already switch to Hand over themselves.
        if (_toolbarHost.Visibility == Visibility.Visible && _toolbarHost.IsMouseOver)
        {
            Cursor = Cursors.Arrow;
            return;
        }
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

    // Merged "几何" entry: one button for the four geometry tools. A plain click activates the
    // last-used shape, a ~400ms press opens the shape/variant flyout (see GeometryShapeMenu).
    private Button _geometryButton = null!;
    private EditorTool _lastGeometry = EditorTool.Rect;
    private GeometryShapeMenu? _geometryMenu;
    private readonly DispatcherTimer _geometryHoldTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _geometryHoldFired;

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
        AddGeometryButton(panel);
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
        panel.Children.Add(MakeElementIconButton(EditorIcons.Ocr(), "Action.Ocr", (_, _) => _session.Confirm(OverlayIntent.Ocr)));
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

    /// <summary>The merged geometry entry: all four shape tools map to this one button (so the
    /// active-tool highlight and hotkey sync work unchanged), the icon shows the last-used shape,
    /// a plain click toggles it, and holding ~400ms opens the shape/variant flyout.</summary>
    private void AddGeometryButton(WrapPanel panel)
    {
        _lastGeometry = GeometryTools.Parse(_session.Editor.GeometryTool);
        _geometryButton = MakeIconButton(null, "Tool.Geometry", (_, _) => { }); // clicks are handled in Preview* below
        _geometryButton.Content = EditorIcons.For(_lastGeometry);
        _geometryButton.PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_geometryMenu is not null)
            {
                CloseGeometryMenu(); // a press on the button while open just dismisses the flyout
                e.Handled = true;
                return;
            }
            _geometryHoldFired = false;
            _geometryHoldTimer.Stop();
            _geometryHoldTimer.Start();
        };
        _geometryButton.PreviewMouseLeftButtonUp += (_, e) =>
        {
            _geometryHoldTimer.Stop();
            bool fired = _geometryHoldFired;
            _geometryHoldFired = false;
            _geometryButton.ReleaseMouseCapture();
            e.Handled = true; // Click is never used; release-after-hold only swallows the press
            if (fired) return;
            _session.Tool = _session.Tool == _lastGeometry ? null : _lastGeometry;
        };
        _geometryButton.MouseLeave += (_, _) => { if (!_geometryHoldFired) _geometryHoldTimer.Stop(); };
        _geometryHoldTimer.Tick += (_, _) =>
        {
            _geometryHoldTimer.Stop();
            _geometryHoldFired = true;
            _geometryButton.ReleaseMouseCapture(); // let the release land on a flyout row
            OpenGeometryMenu();
        };
        panel.Children.Add(_geometryButton);
        foreach (var tool in GeometryTools.All) _toolButtons[tool] = _geometryButton;
    }

    private void OpenGeometryMenu()
    {
        if (_geometryMenu is not null) return;
        _geometryMenu = new GeometryShapeMenu(
            () => _session.Tool is { } tool && GeometryTools.IsGeometry(tool) ? tool : _lastGeometry,
            tool => GeometryTools.UsesDash(tool) ? _session.Editor.DashedLine : _session.Editor.FillShape,
            tool =>
            {
                RememberGeometryTool(tool);
                _session.Tool = tool;
            },
            (tool, value) =>
            {
                RememberGeometryTool(tool);
                if (GeometryTools.UsesDash(tool)) SetOption(e => e.DashedLine = value);
                else SetOption(e => e.FillShape = value);
                _session.Tool = tool;
            });
        _geometryMenu.CloseRequested += (_, _) => CloseGeometryMenu();
        _toolbarLayer.Children.Add(_geometryMenu);
        UiMotion.FadeIn(_geometryMenu, ms: 120);
        PositionGeometryMenu();
    }

    private void CloseGeometryMenu()
    {
        if (_geometryMenu is null) return;
        _toolbarLayer.Children.Remove(_geometryMenu);
        _geometryMenu = null;
    }

    /// <summary>Anchors the flyout to the geometry button, above the toolbar strip when there is room.</summary>
    private void PositionGeometryMenu()
    {
        if (_geometryMenu is null) return;
        _geometryMenu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _geometryMenu.DesiredSize;
        var origin = _geometryButton.TransformToVisual(_toolbarLayer).Transform(new Point(0, 0));
        double x = Math.Clamp(origin.X, 4, Math.Max(4, ActualWidth - size.Width - 4));
        double y = origin.Y - size.Height - 6;
        if (y < 4) y = origin.Y + _geometryButton.ActualHeight + 6;
        y = Math.Clamp(y, 4, Math.Max(4, ActualHeight - size.Height - 4));
        Canvas.SetLeft(_geometryMenu, x);
        Canvas.SetTop(_geometryMenu, y);
    }

    private void RememberGeometryTool(EditorTool tool)
    {
        if (tool == _lastGeometry) return;
        _lastGeometry = tool;
        _session.Editor.GeometryTool = tool.ToString();
        _session.MarkOptionsChanged();
        _geometryButton.Content = EditorIcons.For(tool);
    }

    private bool IsOverGeometryMenu(MouseButtonEventArgs e) =>
        _geometryMenu is { } menu && e.OriginalSource is DependencyObject d && menu.IsAncestorOf(d);

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

    /// <summary>Same button, but hosting a shaped icon (the editor's icon vocabulary) instead of a
    /// Segoe glyph — for actions that have no matching glyph, so both toolbars stay visually aligned.</summary>
    private Button MakeElementIconButton(FrameworkElement icon, string tooltipKey, RoutedEventHandler onClick)
    {
        var button = MakeIconButton(null, tooltipKey, onClick);
        button.Content = icon;
        return button;
    }

    private Button? ActiveToolButton() =>
        _session.Tool is { } tool && _toolButtons.TryGetValue(tool, out var b) ? b : null;

    /// <summary>Syncs tool highlight, undo/redo enablement and the options row with the session state.</summary>
    private void RefreshToolbarState()
    {
        // A geometry tool picked through a hotkey (R/E/L/A) also becomes the last-used shape: the
        // merged button's icon follows it and the choice is persisted with the other options.
        if (_session.Tool is { } tool && GeometryTools.IsGeometry(tool) && tool != _lastGeometry)
        {
            _lastGeometry = tool;
            _session.Editor.GeometryTool = tool.ToString();
            _session.MarkOptionsChanged();
            _geometryButton.Content = EditorIcons.For(tool);
        }
        if (_geometryMenu is not null) _geometryMenu.Refresh();
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

    private static Geometry Geo(Action<StreamGeometryContext> build)
    {
        var geo = new StreamGeometry();
        using (var ctx = geo.Open()) build(ctx);
        return geo;
    }

    private static Canvas SelectIcon()
    {
        // Filled Snipaste-style cursor. The old open polyline was missing its closing edge (the
        // top diagonal back to the tip), so the glyph read as a broken vertical stroke + wing.
        var path = new System.Windows.Shapes.Path
        {
            Data = Geo(g =>
            {
                g.BeginFigure(new Point(5.5, 1.5), true, false);
                g.LineTo(new Point(5.5, 14.5), true, false);
                g.LineTo(new Point(8.4, 11.6), true, false);
                g.LineTo(new Point(10.4, 16.2), true, false);
                g.LineTo(new Point(12.4, 15.3), true, false);
                g.LineTo(new Point(10.4, 10.8), true, false);
                g.LineTo(new Point(14.8, 10.3), true, false);
                g.LineTo(new Point(5.5, 1.5), true, false);
            }),
            Fill = Brushes.White,
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
                CloseGeometryMenu(); // the toolbar went away; the flyout must not linger
            }
            return;
        }
        if (!_toolbarShown)
        {
            // Entrance (motion audit #1): fired only on this hidden→shown transition — UpdateToolbar
            // itself runs per session change, but this branch is transition-gated, so no per-frame
            // animation allocations. Opacity leg on the host: clear any stale animation, seed 0
            // (a fresh per-session window never inherits a residual 1), then fade. Slide leg on the
            // inner panel's own transform — the positioning _toolbarOffset is never animated.
            // Reduced motion: SlideIn drops the slide, FadeIn keeps this pure fade.
            _toolbarHost.BeginAnimation(UIElement.OpacityProperty, null);
            _toolbarHost.Opacity = 0;
            UiMotion.FadeIn(_toolbarHost, ms: 150);
            if (_toolbarHost.Child is StackPanel content) UiMotion.SlideIn(content, 0, 6, 150);
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
        if (_geometryMenu is not null) PositionGeometryMenu(); // the strip moved; keep the flyout anchored
        // The auto-shown help card must keep clear of the toolbar's new rect (a selection near the
        // bottom-left would otherwise park the strip on top of the card).
        if (_helpPanel is not null) PositionHelpPanel();
    }

    // ---- inline annotation UI ----

    private Core.Annotation.PointD _textEditPosition;

    private void OnAnnotationChanged(object? sender, EventArgs e) => RefreshToolbarState();

    private void OnTextEditRequested(object? sender, TextEditRequestEventArgs e)
    {
        var selection = _session.Selection;
        if (selection is null) return;
        // The session fans this event out to every overlay window of a multi-monitor capture;
        // only the window whose monitor contains the click may host the text box, otherwise a
        // phantom box on the other monitor would steal keyboard focus and commit at a wrong spot.
        if (!Monitor.Bounds.Contains(selection.Value.X + (int)e.Position.X, selection.Value.Y + (int)e.Position.Y)) return;
        CloseTextOverlay(commit: false);
        _textEditPosition = e.Position;
        double scale = Scale;
        var sel = selection.Value;
        _textOverlay = new TextBox
        {
            AcceptsReturn = true,
            FontSize = _session.EditorFontSize / scale,
            FontFamily = new FontFamily(_session.EditorFontFamily),
            FontWeight = _session.EditorFontBold ? FontWeights.Bold : FontWeights.Normal,
            Foreground = AnnotationRenderer.BrushFor(_session.EditorStrokeColor),
            // Checkerboard instead of the old white 75% panel: a user-chosen white stroke was
            // invisible on white. Any stroke color reads on the checker, and it carries the same
            // "transparent surface" semantics as the editor canvas.
            Background = AnnotationRenderer.CheckerboardBrush(),
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
        double left = Math.Clamp(local.X, 2, Math.Max(2, ActualWidth - 140));
        double top = Math.Clamp(local.Y, 2, Math.Max(2, ActualHeight - 60));
        Canvas.SetLeft(_textOverlay, left);
        Canvas.SetTop(_textOverlay, top);
        // The text commits where the visible box sits: apply the same clamp offset in image px.
        _textEditPosition = new Core.Annotation.PointD(
            e.Position.X + (left - local.X) * scale,
            e.Position.Y + (top - local.Y) * scale);
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

    // ---- F1 help card (操作提示) ----
    //
    // Dark rounded card matching the toolbar's visual vocabulary (frozen brushes, no theme
    // resources — the overlay tree is deliberately hand-rolled). Built lazily on the first F1,
    // shown in the window that received the key, positioned bottom-center, clamped to the monitor
    // and moved above the toolbar when the two would overlap. No entrance animation: the card is
    // add/remove-only, which keeps diagnostic render paths deterministic (UiMotion.Suppress-safe).

    private static readonly Brush HelpCardBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E)));
    private static readonly Brush HelpCardStrokeBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush HelpChipFillBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush HelpChipStrokeBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush HelpTextBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush HelpSecondaryBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)));
    private Border? _helpPanel;

    private void ToggleHelpPanel()
    {
        if (_helpPanel is not null) HideHelpPanel();
        else ShowHelpPanel();
    }

    private void ShowHelpPanel()
    {
        _helpPanel ??= BuildHelpPanel();
        if (!_toolbarLayer.Children.Contains(_helpPanel)) _toolbarLayer.Children.Add(_helpPanel);
        PositionHelpPanel();
    }

    /// <summary>Removes the card from the tree (session also calls this cross-window on clicks).</summary>
    internal void HideHelpPanel()
    {
        if (_helpPanel is null) return;
        _toolbarLayer.Children.Remove(_helpPanel);
    }

    /// <summary>The session swapped the frozen frame (F5 / ` / !): rebuild this monitor's slice.</summary>
    internal void OnFrozenFrameSwapped() => _renderer.Reslice(_session.FrozenSource);

    /// <summary>Bottom-left corner within this monitor window (Snipaste-style, where the card is
    /// part of the session's static chrome); never overlaps the toolbar — the card is parked right
    /// above it when the rects would intersect.</summary>
    private void PositionHelpPanel()
    {
        if (_helpPanel is null) return;
        _helpPanel.MaxWidth = Math.Max(240, ActualWidth - 16);
        _helpPanel.Measure(new Size(_helpPanel.MaxWidth, double.PositiveInfinity));
        var size = _helpPanel.DesiredSize;
        double x = 16;
        double y = ActualHeight - size.Height - 12;
        if (_toolbarShown && _toolbarSize.Width > 0)
        {
            var card = new Rect(x, y, size.Width, size.Height);
            var toolbar = new Rect(_toolbarOffset.X, _toolbarOffset.Y, _toolbarSize.Width, _toolbarSize.Height);
            if (card.IntersectsWith(toolbar)) y = toolbar.Top - size.Height - 10;
        }
        y = Math.Clamp(y, 4, Math.Max(4, ActualHeight - size.Height - 4));
        Canvas.SetLeft(_helpPanel, x);
        Canvas.SetTop(_helpPanel, y);
    }

    /// <summary>Builds the shortcut card once. Rows reflect what is actually wired for THIS session
    /// (mode, magnifier setting, wired refresh delegate, stored last region) so the card never
    /// advertises a key that would do nothing.</summary>
    private Border BuildHelpPanel()
    {
        bool region = _session.Mode == CaptureMode.Region;
        bool annotate = region && !_session.AutoConfirmOnSelect;
        bool refresh = _session.CanRefreshFrozen;
        bool magnifier = _session.MagnifierEnabled;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        void AddRow(string[] chips, string descKey)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 10, 3) };
            foreach (var chip in chips) keys.Children.Add(HelpKeyChip(chip));
            Grid.SetRow(keys, row);
            Grid.SetColumn(keys, 0);
            grid.Children.Add(keys);
            var desc = new TextBlock
            {
                Text = L.Get(descKey),
                FontSize = 12,
                Foreground = HelpTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 0, 3),
                TextWrapping = TextWrapping.Wrap,
            };
            Grid.SetRow(desc, row);
            Grid.SetColumn(desc, 1);
            grid.Children.Add(desc);
            row++;
        }

        AddRow([L.Get("Overlay.HelpKeyDrag")], "Overlay.HelpDrag");
        if (region)
        {
            AddRow(["Enter", L.Get("Overlay.HelpKeyDoubleClick")], "Overlay.HelpConfirm");
        }
        else
        {
            AddRow([L.Get("Overlay.HelpKeyClick")], "Overlay.HelpPickWindow");
        }
        AddRow(["Esc"], "Overlay.HelpCancel");
        if (region)
        {
            AddRow([L.Get("Overlay.HelpKeyArrows"), L.Get("Overlay.HelpKeyArrowsShift")], "Overlay.HelpNudge");
            AddRow(["Ctrl", "A"], "Overlay.HelpFullMonitor");
            if (_session.HasLastRegion) AddRow(["Shift", "R"], "Overlay.HelpLastRegion");
        }
        AddRow(["W", "A", "S", "D"], "Overlay.HelpMoveCursor");
        if (region) AddRow(["Tab"], "Overlay.HelpToggleDetect");
        AddRow(["1", "2"], "Overlay.HelpElementLevel");
        if (refresh) AddRow(["F5"], "Overlay.HelpRefresh");
        if (refresh) AddRow(["`", "!"], "Overlay.HelpCursorToggle");
        if (magnifier) AddRow(["C"], "Overlay.HelpCopyColor");
        if (annotate) AddRow([], "Overlay.HelpTools");

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(new TextBlock
        {
            Text = L.Get("Overlay.HelpTitle"),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = HelpTextBrush,
            Margin = new Thickness(0, 0, 0, 6),
        });
        // A 1px Border for the rule — a Separator collapses to a dot inside this hand-rolled tree.
        stack.Children.Add(new Border { Height = 1, Background = HelpChipStrokeBrush, Margin = new Thickness(0, 0, 0, 2) });
        stack.Children.Add(grid);
        stack.Children.Add(new TextBlock
        {
            Text = L.Get("Overlay.HelpClose"),
            FontSize = 11,
            Foreground = HelpSecondaryBrush,
            Margin = new Thickness(0, 6, 0, 0),
        });

        return new Border
        {
            Child = stack,
            Background = HelpCardBrush,
            BorderBrush = HelpCardStrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Focusable = false,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Direction = 270, Opacity = 0.45 },
        };
    }

    /// <summary>Outlined key cap, same visual idea as Shell/HotkeyChips but with the overlay's own
    /// frozen dark brushes (HotkeyChips binds theme resources for the settings windows, which do
    /// not fit the hand-rolled overlay tree).</summary>
    private static Border HelpKeyChip(string text)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(4),
            Background = HelpChipFillBrush,
            BorderBrush = HelpChipStrokeBrush,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = text,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = HelpTextBrush,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }
}
