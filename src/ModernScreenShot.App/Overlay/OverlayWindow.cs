using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ModernScreenShot.App.Capture;
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
    private double _dpiCache = -1;

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
        var grid = new Grid();
        grid.Children.Add(_renderer);
        grid.Children.Add(_toolbarHost);
        Content = grid;

        SourceInitialized += (_, _) => PlaceWindow(activate);
        Loaded += (_, _) => { _dpiCache = -1; if (activate) { Activate(); _renderer.Focus(); } };
        DpiChanged += (_, _) => _dpiCache = -1;
        Closed += (_, _) => _session.OnWindowClosed(this);

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
        _renderer.InvalidateVisual();
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
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (_session.OnKey(e.Key, shift)) e.Handled = true;
    }

    private void OnPreviewLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
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

    private void OnPreviewMouseMove(object sender, MouseEventArgs e) =>
        _session.OnMove(this, ToVirtual(e.GetPosition(_renderer)));

    private bool IsOverToolbar(MouseButtonEventArgs e) =>
        _toolbarHost.Visibility == Visibility.Visible
        && e.OriginalSource is DependencyObject d
        && _toolbarHost.IsAncestorOf(d);

    private void ApplyCursorShape()
    {
        var cur = _session.Cursor;
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

    private Border BuildToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        AddToolButton(panel, "\uE70F", "Action.Edit", OverlayIntent.Edit);
        AddToolButton(panel, "\uE8C8", "Action.Copy", OverlayIntent.Copy);
        AddToolButton(panel, "\uE74E", "Action.Save", OverlayIntent.Save);
        AddToolButton(panel, "\uE718", "Action.Pin", OverlayIntent.Pin);
        AddToolButton(panel, "\uE711", "Action.Cancel", null);
        return new Border
        {
            Child = panel,
            Background = new SolidColorBrush(Color.FromArgb(0xEA, 0x1C, 0x1C, 0x1E)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(6, 5, 6, 5),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
            Focusable = false,
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Direction = 270, Opacity = 0.45 },
        };
    }

    private void AddToolButton(StackPanel panel, string glyph, string textKey, OverlayIntent? intent)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 5, 0),
        });
        content.Children.Add(new TextBlock
        {
            Text = L.Get(textKey),
            FontSize = 12,
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
        });
        var button = new Button
        {
            Content = content,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Brushes.White,
            Padding = new Thickness(9, 5, 9, 5),
            Cursor = Cursors.Hand,
            Focusable = false,
        };
        var hover = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        button.MouseEnter += (_, _) => button.Background = hover;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.Click += (_, _) => { if (intent is { } i) _session.Confirm(i); else _session.Cancel(); };
        panel.Children.Add(button);
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
            _toolbarHost.Visibility = Visibility.Collapsed;
            return;
        }
        _toolbarHost.Visibility = Visibility.Visible;
        _toolbarHost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _toolbarHost.DesiredSize;
        var local = ToLocalDip(sel);
        double x = local.Right - size.Width - 2;
        double y = local.Bottom + 8;
        if (y + size.Height > ActualHeight - 4) y = local.Top - size.Height - 8;
        x = Math.Clamp(x, 4, Math.Max(4, ActualWidth - size.Width - 4));
        y = Math.Clamp(y, 4, Math.Max(4, ActualHeight - size.Height - 4));
        _toolbarHost.Margin = new Thickness(x, y, 0, 0);
    }
}
