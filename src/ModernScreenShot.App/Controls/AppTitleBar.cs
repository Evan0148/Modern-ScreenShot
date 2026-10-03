using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shell;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Localization;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Controls;

/// <summary>
/// Unified custom title bar (height from <see cref="TitleBarMetrics.CaptionHeight"/>) that replaces
/// the OS caption on the app's top-level windows. Layout: app title on the left (title type ramp,
/// TextFillColorPrimaryBrush; TextFillColorTertiaryBrush while the window is inactive) and three
/// <see cref="CaptionButton"/> instances (min / max-restore / close) flush on the right whose visual
/// states (rest / hover / pressed / disabled / inactive) live entirely in the button template — there
/// are no ad-hoc pointer handlers here writing Background/Foreground. No bottom separator (native has
/// none). All colors are DynamicResource references so runtime theme switches follow.
///
/// Wiring per window:
///  - place the bar as the topmost docked/first child of the window's root panel;
///  - call <see cref="Attach"/> to install the standard <see cref="WindowChrome"/> (CaptionHeight
///    from <see cref="TitleBarMetrics.CaptionHeight"/>, 6px resize border, no glass frame, no Aero
///    caption buttons) — that is what preserves Win11 rounded corners, snap and the system shadow
///    while the caption area becomes client surface;
///  - the bar hooks its containing window on Loaded: minimize / maximize-restore / close button
///    clicks, the maximize/restore glyph swap on <see cref="Window.StateChanged"/>, the window
///    activation/inactivation state pushed into the caption buttons and the title ink, a
///    localization refresh on language change (the three automation names re-resolved and the title
///    text re-applied, addressing the KNOWN_ISSUES.md "language switch does not refresh" gap), and —
///    only on windows without chrome (the transparent hotkey dialog) — a manual DragMove fallback,
///    always wrapped in try/catch (same fast-click InvalidOperationException trap as PinWindow).
/// </summary>
public sealed class AppTitleBar : Border
{
    private const int DwmwaWindowCornerPreference = 33; // DWMWA_WINDOW_CORNER_PREFERENCE
    private const int DwmwcpRound = 2; // DWMWCP_ROUND

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // --- Maximize geometry: WPF's WindowChrome does not constrain WM_GETMINMAXINFO. Because
    // WindowChrome (GlassFrameThickness=0) makes the whole window client surface, a maximized window
    // whose requested rect is exactly the work area is still inflated by Windows' invisible resize
    // frame (SM_CXSIZEFRAME + SM_CXPADDEDBORDER) so the *client* fills the work area — but here the
    // client IS the window, so the client right edge lands past the monitor edge (the task-3 audit
    // measured +11px at 150% and the top-right caption buttons get clipped). This hook pins the
    // maximized OUTER window to the work area. ---
    private const int WmGetMinMaxInfo = 0x0024;
    private const uint MonitorDefaultToNearest = 0x00000002;

    // Windows only takes the frame-inflation path when the requested maximized rect is *identical*
    // to the default work-area rect. A physical-pixel sweep on this machine showed an exact
    // work-area request came back inflated (work+22px at 150%), while a request 1px smaller stayed
    // literal. Trimming 1px therefore escapes that path; the origin is shifted by the same pixel so
    // the far edges — right/bottom, where the caption buttons live — stay flush with the work area.
    // Physical pixels, so the 1px escape holds at every DPI.
    private const int MaximizeTrim = 1;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    internal const string GlyphMinimize = "\uE921";
    internal const string GlyphMaximize = "\uE922"; // ChromeMaximize — single square
    internal const string GlyphRestore = "\uE923"; // ChromeRestore — overlapping squares
    internal const string GlyphClose = "\uE8BB";

    /// <summary>WinUI caption type ramp. Task 1 measured caption geometry and state colors but not
    /// the title font weight, so this follows the WinUI caption ramp (12 DIP Regular) rather than a
    /// re-measured value.</summary>
    private const string TitleFontFamily = "Segoe UI Variable Text, Segoe UI";

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(AppTitleBar),
        new PropertyMetadata(string.Empty,
            (d, e) => ((AppTitleBar)d)._titleText.Text = e.NewValue as string ?? string.Empty));

    public static readonly DependencyProperty ShowMinProperty = DependencyProperty.Register(
        nameof(ShowMin), typeof(bool), typeof(AppTitleBar),
        new PropertyMetadata(false, (d, _) => ((AppTitleBar)d).UpdateButtons()));

    public static readonly DependencyProperty ShowMaxProperty = DependencyProperty.Register(
        nameof(ShowMax), typeof(bool), typeof(AppTitleBar),
        new PropertyMetadata(false, (d, _) => ((AppTitleBar)d).UpdateButtons()));

    public static readonly DependencyProperty ShowCloseProperty = DependencyProperty.Register(
        nameof(ShowClose), typeof(bool), typeof(AppTitleBar),
        new PropertyMetadata(false, (d, _) => ((AppTitleBar)d).UpdateButtons()));

    private readonly TextBlock _titleText = new()
    {
        FontSize = TitleBarMetrics.TitleFontSize,
        FontFamily = new FontFamily(TitleFontFamily),
        FontWeight = FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = TitleBarMetrics.TitleMargin,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };
    private readonly StackPanel _buttonPanel = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly CaptionButton _minButton = new(GlyphMinimize, isClose: false);
    private readonly CaptionButton _maxButton = new(GlyphMaximize, isClose: false);
    private readonly CaptionButton _closeButton = new(GlyphClose, isClose: true);
    private Window? _window;
    private LocalizationService? _localization;
    private TitleBarSnapHook? _snapHook;

    public AppTitleBar()
    {
        Height = TitleBarMetrics.CaptionHeight;
        Background = Brushes.Transparent; // hit-test surface so the caption band stays draggable

        _titleText.SetResourceReference(TextBlock.ForegroundProperty, TitleBarMetrics.TextFillColorPrimaryBrushKey);

        // CaptionButton already opts into the chrome hit test by default; set it explicitly here so
        // the contract with the WindowChrome caption band is visible at the composition site.
        _minButton.IsHitTestVisibleInChrome = true;
        _minButton.SetAutomationName(L.Get("Action.Minimize"));
        _minButton.Click += (_, _) =>
        {
            if (_window is { } w && w.ResizeMode is not ResizeMode.NoResize)
                w.WindowState = WindowState.Minimized;
        };

        _maxButton.IsHitTestVisibleInChrome = true;
        _maxButton.SetAutomationName(L.Get("Action.Maximize"));
        _maxButton.Click += (_, _) => ToggleMaximize();

        _closeButton.IsHitTestVisibleInChrome = true;
        _closeButton.SetAutomationName(L.Get("Action.Close"));
        _closeButton.Click += (_, _) => _window?.Close();

        _buttonPanel.Children.Add(_minButton);
        _buttonPanel.Children.Add(_maxButton);
        _buttonPanel.Children.Add(_closeButton);

        var root = new Grid();
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_titleText, 0);
        Grid.SetColumn(_buttonPanel, 1);
        root.Children.Add(_titleText);
        root.Children.Add(_buttonPanel);
        Child = root;

        MouseLeftButtonDown += OnCaptionMouseLeftButtonDown;
        Loaded += (_, _) => HookWindow();
        Unloaded += (_, _) => UnhookWindow();
        UpdateButtons();
    }

    /// <summary>Window title shown on the left. Bind with DynamicResource so language switches follow.</summary>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    /// <summary>Show the minimize button (skip for fixed-size windows).</summary>
    public bool ShowMin
    {
        get => (bool)GetValue(ShowMinProperty);
        set => SetValue(ShowMinProperty, value);
    }

    /// <summary>Show the maximize/restore button; also enables double-click maximize.</summary>
    public bool ShowMax
    {
        get => (bool)GetValue(ShowMaxProperty);
        set => SetValue(ShowMaxProperty, value);
    }

    /// <summary>Show the close button.</summary>
    public bool ShowClose
    {
        get => (bool)GetValue(ShowCloseProperty);
        set => SetValue(ShowCloseProperty, value);
    }

    /// <summary>
    /// Installs the app-standard <see cref="WindowChrome"/> on <paramref name="window"/> so the top
    /// <see cref="TitleBarMetrics.CaptionHeight"/> DIPs act as the caption: native drag, double-click
    /// maximize, Aero snap, Win11 rounded corners and the system shadow are preserved while the OS
    /// caption buttons are suppressed (each <see cref="CaptionButton"/> carries
    /// <c>WindowChrome.IsHitTestVisibleInChrome=true</c>). <paramref name="resizeBorderThickness"/> is
    /// 6 for resizable windows; pass 0 for fixed-size ones. Returns the chrome for chaining.
    /// </summary>
    public static WindowChrome Attach(Window window, double captionHeight = TitleBarMetrics.CaptionHeight,
        double resizeBorderThickness = 6)
    {
        var chrome = new WindowChrome
        {
            CaptionHeight = captionHeight,
            ResizeBorderThickness = new Thickness(resizeBorderThickness),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        };
        WindowChrome.SetWindowChrome(window, chrome);
        // WindowChrome alone does NOT keep Win11 rounded corners once the standard caption is
        // replaced — DWM falls back to square. Ask for rounding explicitly (no-op pre-Win11).
        // It also does not constrain the maximized size, so hook WM_GETMINMAXINFO to pin it to
        // the work area (otherwise maximized content overflows and the caption buttons clip).
        window.SourceInitialized += (_, _) =>
        {
            ApplyRoundedCorners(window);
            // Only maximizable windows need the clamp. On a NoResize/CanMinimize window (the
            // SizeToContent hotkey dialog) the ptMaxTrackSize clamp would cap its auto height and
            // truncate the bottom on small/high-DPI screens, so skip the hook there.
            if (window.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize) return;
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            System.Windows.Interop.HwndSource.FromHwnd(hwnd)?.AddHook(MaximizeHook);
            // Win11 Snap Layouts: let the OS see the maximize button as HTMAXBUTTON so its hover
            // flyout appears (no-op on Win10). The bar may not be in the tree yet for code-built
            // windows, so retry once on Loaded if the first lookup misses.
            if (FindTitleBar(window) is { } bar) bar.InstallSnapHook(window);
            else window.Loaded += (_, _) => FindTitleBar(window)?.InstallSnapHook(window);
        };
        if (window.IsLoaded) ApplyRoundedCorners(window); // Attach ran after SourceInitialized
        return chrome;
    }

    private static IntPtr MaximizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;
        var mon = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (mon == IntPtr.Zero) return IntPtr.Zero;
        var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi)) return IntPtr.Zero;

        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MINMAXINFO>(lParam);
        RECT work = mi.rcWork, full = mi.rcMonitor;
        // Trim 1px off the work area (and shift the origin by it) so Windows takes the literal path
        // instead of re-inflating the maximized window by the resize frame; the far edges stay flush.
        // Coordinates are physical pixels relative to the monitor origin (mixed-DPI safe, matching
        // the project's SetWindowPos convention rather than unreliable WPF DIP Left/Top).
        mmi.ptMaxPosition = new POINT { X = work.Left - full.Left + MaximizeTrim, Y = work.Top - full.Top + MaximizeTrim };
        mmi.ptMaxSize = new POINT { X = work.Right - work.Left - MaximizeTrim, Y = work.Bottom - work.Top - MaximizeTrim };
        mmi.ptMaxTrackSize = mmi.ptMaxSize;
        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, true);
        handled = true;
        return IntPtr.Zero;
    }

    private static void ApplyRoundedCorners(Window window)
    {
        try
        {
            if (window.AllowsTransparency) return; // layered window: DWM ignores its NC area; corners come from the drawn card
            if (Environment.OSVersion.Version.Build < 22000) return; // pre-Win11: square is native
            var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;
            int preference = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch
        {
            // DWM unavailable: square corners are acceptable, never worth crashing a window for.
        }
    }

    /// <summary>Installs the Win11 Snap Layouts bridge for <see cref="_maxButton"/> on
    /// <paramref name="window"/> (idempotent; a no-op on Windows 10 / when the handle is not ready).
    /// While installed, the max button's hover/pressed visuals follow the non-client state because
    /// the OS routes its input as HTMAXBUTTON surface.</summary>
    internal void InstallSnapHook(Window window)
    {
        if (_snapHook is not null) return;
        _snapHook = TitleBarSnapHook.Install(window, _maxButton);
        if (_snapHook is not null)
        {
            // The OS now owns the button's pointer surface: WPF's stale IsMouseOver must not paint
            // a second, independent hover. Only the NC state drives it from here on.
            _maxButton.IsNcMode = true;
            _snapHook.StateChanged += OnSnapStateChanged;
        }
    }

    private void OnSnapStateChanged(object? sender, EventArgs e)
    {
        if (_snapHook is null) return;
        _maxButton.IsNcHover = _snapHook.IsOverMaxButton;
        _maxButton.IsNcPressed = _snapHook.IsMaxButtonPressed;
    }

    /// <summary>Depth-first logical-tree search for the bar installed on <paramref name="root"/>.
    /// Used by <see cref="Attach"/> to reach the instance (and its private max button) without
    /// changing the static public signature.</summary>
    private static AppTitleBar? FindTitleBar(DependencyObject root)
    {
        if (root is AppTitleBar bar) return bar;
        foreach (object child in LogicalTreeHelper.GetChildren(root))
            if (child is DependencyObject node && FindTitleBar(node) is { } found) return found;
        return null;
    }

    private void UpdateButtons()
    {
        // Keep visibility in step with the window's ResizeMode so a shown-but-inert button can
        // never appear (min needs a resizable/maximizable window; max needs full resize). Before
        // HookWindow runs _window is null → fall back to the ShowXxx flags only.
        var mode = _window?.ResizeMode;
        bool canMin = mode is null or not ResizeMode.NoResize;
        bool canMax = mode is null or not (ResizeMode.NoResize or ResizeMode.CanMinimize);
        _minButton.Visibility = ShowMin && canMin ? Visibility.Visible : Visibility.Collapsed;
        _maxButton.Visibility = ShowMax && canMax ? Visibility.Visible : Visibility.Collapsed;
        _closeButton.Visibility = ShowClose ? Visibility.Visible : Visibility.Collapsed;
    }

    private void HookWindow()
    {
        var win = Window.GetWindow(this);
        if (win is null) return;
        if (!ReferenceEquals(win, _window))
        {
            DetachWindowEvents();
            _window = win;
            win.StateChanged += OnWindowStateChanged;
            win.Activated += OnWindowActivated;
            win.Deactivated += OnWindowDeactivated;
        }
        SubscribeLocalization();
        OnWindowStateChanged(win, EventArgs.Empty); // sync the glyph even if already maximized
        SetWindowActive(win.IsActive); // sync title ink / button dim if activation changed pre-Loaded
        RefreshLocalization(); // re-apply title + automation names on Loaded
        UpdateButtons(); // now that _window (and its ResizeMode) is known, re-evaluate visibility
    }

    private void UnhookWindow()
    {
        DetachWindowEvents();
        _window = null;
        if (_localization is not null)
        {
            _localization.LanguageChanged -= OnLanguageChanged;
            _localization = null;
        }
    }

    private void DetachWindowEvents()
    {
        if (_window is null) return;
        _window.StateChanged -= OnWindowStateChanged;
        _window.Activated -= OnWindowActivated;
        _window.Deactivated -= OnWindowDeactivated;
    }

    private void SubscribeLocalization()
    {
        if (_localization is not null) return;
        // Resolve lazily through the app's service provider (same pattern as the windows); a bar
        // instantiated outside a running app (harness/smoke fixtures) simply never subscribes.
        _localization = App.Services?.GetService(typeof(LocalizationService)) as LocalizationService;
        if (_localization is not null) _localization.LanguageChanged += OnLanguageChanged;
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (_window is null) return;
        bool max = _window.WindowState == WindowState.Maximized;
        _maxButton.Glyph = max ? GlyphRestore : GlyphMaximize;
        _maxButton.SetAutomationName(L.Get(max ? "Action.Restore" : "Action.Maximize"));
    }

    private void OnWindowActivated(object? sender, EventArgs e) => SetWindowActive(true);

    private void OnWindowDeactivated(object? sender, EventArgs e) => SetWindowActive(false);

    private void SetWindowActive(bool active)
    {
        _minButton.IsWindowActive = active;
        _maxButton.IsWindowActive = active;
        _closeButton.IsWindowActive = active;
        _titleText.SetResourceReference(TextBlock.ForegroundProperty,
            active ? TitleBarMetrics.TextFillColorPrimaryBrushKey : TitleBarMetrics.TextFillColorTertiaryBrushKey);
    }

    /// <summary>Re-applies the title text from the current <see cref="Text"/> value (DynamicResource
    /// owners already re-resolve it through the DP) and re-resolves the three automation names.</summary>
    private void OnLanguageChanged(object? sender, EventArgs e) => RefreshLocalization();

    private void RefreshLocalization()
    {
        _titleText.Text = Text ?? string.Empty;
        _minButton.SetAutomationName(L.Get("Action.Minimize"));
        _closeButton.SetAutomationName(L.Get("Action.Close"));
        OnWindowStateChanged(_window, EventArgs.Empty); // maximize/restore name follows the state
    }

    private void ToggleMaximize()
    {
        if (_window is null || _window.ResizeMode is ResizeMode.NoResize or ResizeMode.CanMinimize) return;
        _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    /// <summary>Left-press on the bar's blank area drags the window; double-click toggles maximize
    /// (only when ShowMax). Caption buttons handle their own presses (ButtonBase marks them
    /// handled), so they never land here. With WindowChrome attached the caption band is native
    /// non-client surface and this handler stays idle; it only drives chrome-less windows such as
    /// the transparent hotkey dialog.</summary>
    private void OnCaptionMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _window is null) return;
        if (e.ClickCount >= 2 && ShowMax)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }
        try
        {
            _window.DragMove();
        }
        catch (InvalidOperationException)
        {
            // Button already released (fast click) — nothing to drag. Same trap as PinWindow.
        }
    }
}
