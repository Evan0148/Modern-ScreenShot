using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Output;

/// <summary>
/// macOS-style post-capture floating thumbnail. A small rounded card slides/fades in at the
/// bottom-right of the cursor's monitor. Click opens the editor; a right-click menu offers
/// copy / save / pin / close; a small hover-only close button dismisses it. When ignored it
/// disappears after a few seconds and runs the caller's configured "landing" action.
///
/// Follows PinWindow / CountdownWindow conventions (borderless, topmost, no-activate, no
/// taskbar entry), but deliberately does NOT create any services itself: every terminal
/// action is an injected callback so this window stays a pure view.
/// </summary>
public sealed class FloatingThumbnailWindow : Window
{
    // Card geometry in DIP. The image long edge is capped at ThumbLongEdge inside the padding.
    private const double ThumbLongEdge = 240;
    private const double CardPadding = 8;
    private const double EdgeMargin = 24;         // gap from work-area edges
    private const int AutoDismissSeconds = 6;

    private readonly Action? _onEdit;
    private readonly Action? _onCopy;
    private readonly Action? _onSave;
    private readonly Action? _onPin;
    private readonly Action? _onLand; // default landing action when ignored / auto-dismissed

    private readonly DispatcherTimer _timer;
    private bool _landed;             // guards against double-invoking a terminal action
    private bool _hovering;
    private Button? _closeButton;

    /// <param name="thumbnail">Already-flattened bitmap to preview (safe to reuse the export bitmap).</param>
    /// <param name="onEdit">Open the editor (also the click-through action).</param>
    /// <param name="onCopy">Copy to clipboard.</param>
    /// <param name="onSave">Quick-save to disk.</param>
    /// <param name="onPin">Pin to screen.</param>
    /// <param name="onLand">Default landing action run when the card is ignored (auto-dismiss).</param>
    public FloatingThumbnailWindow(BitmapSource thumbnail, MonitorService monitors,
        Action? onEdit, Action? onCopy, Action? onSave, Action? onPin, Action? onLand)
    {
        _onEdit = onEdit;
        _onCopy = onCopy;
        _onSave = onSave;
        _onPin = onPin;
        _onLand = onLand;

        Title = L.Get("Settings.After.Thumbnail");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false; // macOS thumbnail never steals focus
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.Manual;
        UseLayoutRounding = true;

        // Fit the image long edge to ThumbLongEdge, preserving aspect ratio.
        double iw = Math.Max(1, thumbnail.PixelWidth);
        double ih = Math.Max(1, thumbnail.PixelHeight);
        double scale = Math.Min(1.0, ThumbLongEdge / Math.Max(iw, ih));
        double thumbW = Math.Max(1, Math.Round(iw * scale));
        double thumbH = Math.Max(1, Math.Round(ih * scale));

        Width = thumbW + CardPadding * 2;
        Height = thumbH + CardPadding * 2;

        var preview = new Image
        {
            Source = thumbnail,
            Stretch = Stretch.Fill,
            Width = thumbW,
            Height = thumbH,
        };
        RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.HighQuality);

        var imageBorder = new Border
        {
            Child = preview,
            CornerRadius = new CornerRadius(8),
            ClipToBounds = true,
            Background = CardFill,
        };

        _closeButton = BuildCloseButton();

        var grid = new Grid();
        grid.Children.Add(imageBorder);
        grid.Children.Add(_closeButton);

        var card = new Border
        {
            Child = grid,
            Padding = new Thickness(CardPadding),
            CornerRadius = new CornerRadius(12),
            Background = CardFill,
            BorderBrush = CardStroke,
            BorderThickness = new Thickness(1),
            Effect = new DropShadowEffect
            {
                BlurRadius = 24,
                ShadowDepth = 3,
                Direction = 270,
                Opacity = 0.5,
            },
        };
        Content = card;

        BuildContextMenu();

        MouseEnter += OnMouseEnterCard;
        MouseLeave += OnMouseLeaveCard;
        // Left click (single) on the card body opens the editor. Ctrl/drag is intentionally not
        // supported for repositioning here to keep behaviour simple; DragMove is available.
        MouseLeftButtonDown += OnLeftButtonDown;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(AutoDismissSeconds) };
        _timer.Tick += OnTimerTick;

        SourceInitialized += (_, _) => PlaceBottomRight(monitors);
        Loaded += (_, _) => PlayEntranceAnimation();
        Closed += OnClosedCleanup;
    }

    // ---- static frozen brushes ----
    private static readonly SolidColorBrush CardFill = Frozen(Color.FromArgb(0xF2, 0x2A, 0x2A, 0x2E));
    private static readonly SolidColorBrush CardStroke = Frozen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush CloseGlyph = Frozen(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush CloseBack = Frozen(Color.FromArgb(0xB0, 0x1C, 0x1C, 0x1E));

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    private Button BuildCloseButton()
    {
        var glyph = new TextBlock
        {
            Text = "\u2715", // ✕
            FontSize = 11,
            Foreground = CloseGlyph,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Button
        {
            Content = glyph,
            Width = 20,
            Height = 20,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Background = CloseBack,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            Opacity = 0, // hover-only
            ToolTip = L.Get("Action.Close"),
        };
        // Round the button via a template-free approach: clip with a Border-shaped background.
        btn.Template = RoundButtonTemplate();
        btn.Click += (_, e) =>
        {
            e.Handled = true;
            Close(); // explicit close = dismiss WITHOUT the landing action
        };
        // keep clicks on the button from bubbling to the card (which would open the editor)
        btn.PreviewMouseLeftButtonDown += (_, e) => e.Handled = false;
        return btn;
    }

    private static ControlTemplate RoundButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);
        template.VisualTree = border;
        return template;
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();
        if (_onCopy is not null)
            menu.Items.Add(MenuItem(L.Get("Action.Copy"), () => Land(_onCopy)));
        if (_onSave is not null)
            menu.Items.Add(MenuItem(L.Get("Action.Save"), () => Land(_onSave)));
        if (_onPin is not null)
            menu.Items.Add(MenuItem(L.Get("Action.Pin"), () => Land(_onPin)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(L.Get("Action.Close"), Close));
        // The menu lives in its own popup HWND: the card gets MouseLeave the moment it opens.
        // Treat an open menu as interaction — the countdown resumes only after it closes.
        menu.Closed += (_, _) =>
        {
            if (!_landed && !_hovering) _timer.Start();
        };
        ContextMenu = menu;
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    private void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Clicks originating on the close button are handled there.
        if (e.OriginalSource is DependencyObject d && IsWithinCloseButton(d)) return;
        if (e.ClickCount >= 2)
        {
            // double-click also opens the editor
            Land(_onEdit);
            return;
        }
        // Single click opens the editor; a press-and-drag repositions instead. DragMove blocks
        // until the button is released, so compare the window origin before/after to tell a click
        // (open editor) from a drag (leave the card where the user dropped it).
        double beforeLeft = Left, beforeTop = Top;
        try
        {
            DragMove();
        }
        catch
        {
            // DragMove throws if the button was already released; ignore.
        }
        double moved = Math.Abs(Left - beforeLeft) + Math.Abs(Top - beforeTop);
        if (moved < 4)
            Land(_onEdit); // treated as a plain click → open editor
    }

    private bool IsWithinCloseButton(DependencyObject node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, _closeButton)) return true;
            node = VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    private void OnMouseEnterCard(object sender, MouseEventArgs e)
    {
        _hovering = true;
        _timer.Stop(); // pause auto-dismiss while hovering (macOS behaviour)
        if (_closeButton is not null) _closeButton.Opacity = 1;
    }

    private void OnMouseLeaveCard(object sender, MouseEventArgs e)
    {
        _hovering = false;
        if (_closeButton is not null) _closeButton.Opacity = 0;
        if (!_landed && ContextMenu?.IsOpen != true) _timer.Start(); // resume countdown (an open menu pauses it)
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        _timer.Stop();
        if (_hovering) return; // safety: don't dismiss while hovered
        if (ContextMenu?.IsOpen == true) { _timer.Start(); return; } // don't land under an open menu
        Land(_onLand); // ignored → run configured landing action
    }

    /// <summary>Runs a terminal action once, then closes. Null action just closes.</summary>
    private void Land(Action? action)
    {
        if (_landed) return;
        _landed = true;
        _timer.Stop();
        try
        {
            action?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("Floating thumbnail action failed", ex);
        }
        Close();
    }

    private void PlayEntranceAnimation()
    {
        // Slide up + fade in from the bottom-right.
        var transform = new TranslateTransform(0, 24);
        RenderTransform = transform;
        Opacity = 0;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
        var slide = new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease };
        BeginAnimation(OpacityProperty, fade);
        transform.BeginAnimation(TranslateTransform.YProperty, slide);
        _timer.Start(); // start auto-dismiss once shown
    }

    /// <summary>Physical-pixel placement at the bottom-right of the cursor monitor's work area.</summary>
    private void PlaceBottomRight(MonitorService monitors)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var monitor = monitors.GetCursorMonitor();
        double s = monitor.DpiX / 96.0;
        int w = (int)Math.Round(Width * s);
        int h = (int)Math.Round(Height * s);
        var work = monitor.WorkArea;
        int marginPx = (int)Math.Round(EdgeMargin * s);
        int x = work.Right - w - marginPx;
        int y = work.Bottom - h - marginPx;
        x = Math.Clamp(x, work.X, Math.Max(work.X, work.Right - w));
        y = Math.Clamp(y, work.Y, Math.Max(work.Y, work.Bottom - h));
        // Position before the window is visible to avoid a flash at the default location.
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, x, y, w, h,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        long exStyle = NativeMethods.GetExStyle(hwnd);
        NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE,
            (IntPtr)(exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW));
    }

    private void OnClosedCleanup(object? sender, EventArgs e)
    {
        // Stop the timer and unbind every handler — leaked singleton subscriptions have burned
        // this project before, and although these are instance handlers we detach them anyway.
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        MouseEnter -= OnMouseEnterCard;
        MouseLeave -= OnMouseLeaveCard;
        MouseLeftButtonDown -= OnLeftButtonDown;
        Closed -= OnClosedCleanup;
        _closeButton = null;
    }
}
