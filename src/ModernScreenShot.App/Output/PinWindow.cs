using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Settings;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Output;

/// <summary>
/// Borderless topmost floating image ("pin to screen"), Snipaste-style: a soft drop shadow around the
/// pinned image (window is larger than the image by a margin so the blur has room to render), drag to
/// move, wheel zoom, Ctrl+wheel opacity, double-click close, and a full right-click menu — copy / save
/// as / window-shadow toggle / zoom ▸ / background mode ▸ / image processing ▸ / paste / replace with
/// file / close, plus a live size read-out with copy-size. The shadow choice persists in PinShadow.
/// </summary>
public sealed class PinWindow : Window
{
    /// <summary>Invisible margin around the image that hosts the drop shadow blur.</summary>
    private const double ShadowMargin = 20.0;

    private PixelBuffer _image;
    private readonly Action<PixelBuffer>? _editCallback;
    private readonly Func<string>? _openFolderCallback;
    private readonly ImageExporter _exporter;
    private readonly ClipboardService _clipboard;
    private readonly SettingsStore? _store;
    private double _zoom = 1;
    private double _rotDeg;     // 0 / 90 / 180 / 270
    private bool _flipH;
    private bool _shadowOn;
    private Window? _toast;
    private TextBlock? _toastText;
    private System.Windows.Threading.DispatcherTimer? _toastTimer;
    private readonly Rect _toastWorkArea;

    private Image _preview = null!;
    private Border _host = null!;
    private readonly DropShadowEffect _shadowEffect = new()
    {
        BlurRadius = 24,
        ShadowDepth = 0,
        Opacity = 0.55,
        Color = Colors.Black,
    };
    private MenuItem? _sizeItem;
    private MenuItem? _shadowItem;
    private Rect? _centerOnWorkArea; // primary fallback: centered after SizeWindow yields real Width/Height

    public PinWindow(PixelBuffer image, ImageExporter exporter, ClipboardService clipboard,
        Action<PixelBuffer>? editCallback, Func<string>? openFolderCallback,
        MonitorService? monitors = null, PixelRect? sourceRect = null, SettingsStore? store = null)
    {
        _image = image;
        _exporter = exporter;
        _clipboard = clipboard;
        _editCallback = editCallback;
        _openFolderCallback = openFolderCallback;
        _store = store;
        _shadowOn = store?.Current.PinShadow ?? true;

        Title = L.Get("Action.Pin");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        // Manual sizing: WidthAndHeight would let the content dictate the window size and defeat
        // both the fit-to-work-area computation below and the wheel zoom in OnWheel.
        SizeToContent = SizeToContent.Manual;

        // Size and place on the capture's monitor (same scheme as the editor): fit-to-work-area
        // against the primary screen overflows a smaller secondary one, and the pin should appear
        // where the user is looking, not always on the primary. No monitor info → primary fallback.
        MonitorInfo? target = null;
        var workArea = SystemParameters.WorkArea;
        if (monitors is { } svc)
        {
            target = sourceRect is { } sr && !sr.IsEmpty
                ? svc.FromPoint(sr.X + sr.Width / 2, sr.Y + sr.Height / 2)
                : svc.GetCursorMonitor();
            double s = target.DpiX > 0 ? target.DpiX / 96.0 : 1.0;
            workArea = new Rect(target.WorkArea.X / s, target.WorkArea.Y / s,
                target.WorkArea.Width / s, target.WorkArea.Height / s);
        }
        // The toast must follow the pin's monitor, not always the primary — feedback belongs
        // where the user is looking (spatial consistency).
        _toastWorkArea = workArea;
        _zoom = FitZoom(workArea);
        if (target is not null)
        {
            // CenterScreen would land on the primary; SetWindowPos places physically (WPF Left/Top
            // DIP mapping is unreliable across mixed-DPI monitors).
            WindowStartupLocation = WindowStartupLocation.Manual;
            SourceInitialized += (_, _) => MoveToMonitor(target);
        }
        else
        {
            // CenterScreen would land on the primary's center regardless of Left/Top; Manual lets
            // the work-area centering below (after SizeWindow has real Width/Height) take effect.
            WindowStartupLocation = WindowStartupLocation.Manual;
            _centerOnWorkArea = workArea;
        }

        _preview = new Image
        {
            Source = _image.ToBitmapSource(),
            Stretch = Stretch.Fill,
        };
        ApplyOrientationTransform();
        RenderOptions.SetBitmapScalingMode(_preview, BitmapScalingMode.Fant);
        _host = new Border
        {
            Child = _preview,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
        };
        Content = _host;

        SizeWindow();
        if (_centerOnWorkArea is { } wa)
        {
            // Width/Height are NaN before SizeWindow, so this must stay after it.
            Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
            Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
        }
        BuildContextMenu();
        Cursor = Cursors.SizeAll;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount >= 2) { Close(); return; }
            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // Button already released (fast click) — nothing to drag.
            }
        };
        MouseWheel += OnWheel;

        // Black-presentation guard: the exact class of failure that once showed a fully black
        // welcome window — DWM never composes the surface while the visual tree is healthy. The
        // content's own black fraction disarms the guard when the user genuinely pinned black.
        PresentationGuard.Arm(this, () => PresentationGuard.BlackFraction(_image));
    }

    /// <summary>Displayed (orientation-applied) dimensions — rotation swaps width/height.</summary>
    private double DispW => _rotDeg % 180 == 0 ? _image.Width : _image.Height;
    private double DispH => _rotDeg % 180 == 0 ? _image.Height : _image.Width;
    private double ShadowPad => _shadowOn ? ShadowMargin * 2 : 0;

    /// <summary>Largest zoom ≤1 that fits the given work area, margins included.</summary>
    private double FitZoom(Rect workArea)
    {
        double w = DispW + ShadowPad, h = DispH + ShadowPad;
        return Math.Min(1.0, Math.Min(workArea.Width * 0.9 / w, workArea.Height * 0.9 / h));
    }

    /// <summary>Sizes the window from the displayed dimensions × zoom plus the shadow margins, and
    /// keeps the shadow chrome (margin + blur effect) in step with the toggle.</summary>
    private void SizeWindow()
    {
        Width = DispW * _zoom + ShadowPad;
        Height = DispH * _zoom + ShadowPad;
        _host.Margin = _shadowOn ? new Thickness(ShadowMargin) : new Thickness(0);
        _host.Effect = _shadowOn ? _shadowEffect : null;
        if (_sizeItem is not null) _sizeItem.Header = $"{(int)DispW} × {(int)DispH}";
    }

    /// <summary>Rotation + flip as a LayoutTransform so the Image re-flows to the rotated aspect.</summary>
    private void ApplyOrientationTransform()
    {
        var group = new TransformGroup();
        group.Children.Add(new RotateTransform(_rotDeg));
        if (_flipH) group.Children.Add(new ScaleTransform(-1, 1));
        _preview.LayoutTransform = group;
    }

    /// <summary>Physically centers the pin on the target monitor (virtual-screen physical pixels;
    /// the DIP size was derived from that monitor's own work area and DPI).</summary>
    private void MoveToMonitor(MonitorInfo m)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        double scale = m.DpiX > 0 ? m.DpiX / 96.0 : 1.0;
        int w = (int)Math.Round(Width * scale);
        int h = (int)Math.Round(Height * scale);
        int x = m.WorkArea.X + Math.Max(0, (m.WorkArea.Width - w) / 2);
        int y = m.WorkArea.Y + Math.Max(0, (m.WorkArea.Height - h) / 2);
        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    // ---- context menu (Snipaste-style) ----

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        menu.Items.Add(MenuItem(L.Get("Pin.CopyImage"), (_, _) =>
        {
            if (_clipboard.TryPutImage(_image)) ShowToast(L.Get("Toast.Copied"));
        }));
        menu.Items.Add(MenuItem(L.Get("Pin.SaveAs"), (_, _) => SaveDialog()));
        menu.Items.Add(new Separator());

        _shadowItem = new MenuItem { Header = L.Get("Pin.Shadow"), IsCheckable = true, IsChecked = _shadowOn };
        _shadowItem.Click += (_, _) =>
        {
            _shadowOn = _shadowItem.IsChecked;
            if (_store is { } store)
            {
                try
                {
                    store.Current.PinShadow = _shadowOn;
                    store.Save();
                }
                catch (Exception ex) { Log.Error("Saving the pin shadow preference failed", ex); }
            }
    
            SizeWindow();
        };
        menu.Items.Add(_shadowItem);

        var zoom = new MenuItem { Header = L.Get("Pin.Zoom") };
        zoom.Items.Add(MenuItem(L.Get("Pin.ZoomIn"), (_, _) => ApplyZoom(1.25)));
        zoom.Items.Add(MenuItem(L.Get("Pin.ZoomOut"), (_, _) => ApplyZoom(1 / 1.25)));
        zoom.Items.Add(MenuItem(L.Get("Pin.Zoom100"), (_, _) => { _zoom = 1; SizeWindow(); }));
        zoom.Items.Add(MenuItem(L.Get("Pin.ZoomFit"), (_, _) => { _zoom = FitZoom(_toastWorkArea); SizeWindow(); }));
        menu.Items.Add(zoom);

        var background = new MenuItem { Header = L.Get("Pin.Background") };
        AddBackgroundOption(background, "Pin.BgOriginal", null);
        AddBackgroundOption(background, "Pin.BgWhite", Brushes.White);
        AddBackgroundOption(background, "Pin.BgGray", new SolidColorBrush(Color.FromRgb(0x80, 0x80, 0x80)));
        AddBackgroundOption(background, "Pin.BgBlack", Brushes.Black);
        menu.Items.Add(background);

        var ops = new MenuItem { Header = L.Get("Pin.ImageOps") };
        ops.Items.Add(MenuItem(L.Get("Pin.Rotate"), (_, _) =>
        {
            _rotDeg = (_rotDeg + 90) % 360;
            ApplyOrientationTransform();
            SizeWindow();
        }));
        ops.Items.Add(MenuItem(L.Get("Pin.FlipH"), (_, _) =>
        {
            _flipH = !_flipH;
            ApplyOrientationTransform();
        }));
        ops.Items.Add(MenuItem(L.Get("Pin.Reset"), (_, _) =>
        {
            _rotDeg = 0;
            _flipH = false;
            ApplyOrientationTransform();
            SizeWindow();
        }));
        menu.Items.Add(ops);

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(L.Get("Pin.Paste"), (_, _) => PasteClipboard()));
        menu.Items.Add(MenuItem(L.Get("Pin.ReplaceFile"), (_, _) => ReplaceFromFile()));
        if (_openFolderCallback is not null)
            menu.Items.Add(MenuItem(L.Get("Action.OpenFolder"), (_, _) => _openFolderCallback()));
        menu.Items.Add(new Separator());

        _sizeItem = new MenuItem { Header = $"{(int)DispW} × {(int)DispH}", IsEnabled = false };
        var copySize = MenuItem(L.Get("Pin.CopySize"), (_, _) =>
        {
            try
            {
                Clipboard.SetText($"{(int)DispW} × {(int)DispH}");
                ShowToast(L.Get("Toast.Copied"));
            }
            catch (Exception ex) { Log.Error("Copying the pin size failed", ex); }
        });
        _sizeItem.Items.Add(copySize);
        menu.Items.Add(_sizeItem);

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem(L.Get("Action.Close"), (_, _) => Close()));
        ContextMenu = menu;
    }

    private static MenuItem MenuItem(string header, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += onClick;
        return item;
    }

    private void AddBackgroundOption(MenuItem parent, string headerKey, Brush? background)
    {
        var item = new MenuItem { Header = L.Get(headerKey), IsCheckable = true };
        item.Click += (_, _) =>
        {
            _host.Background = background;
            foreach (var sibling in parent.Items)
                if (sibling is MenuItem mi) mi.IsChecked = ReferenceEquals(mi, item);
        };
        parent.Items.Add(item);
    }

    private void ApplyZoom(double factor)
    {
        double newZoom = Math.Clamp(_zoom * factor, 0.1, 4);
        if (Math.Abs(newZoom - _zoom) < 0.0001) return;
        _zoom = newZoom;
        SizeWindow();
    }

    // ---- image replacement (paste / replace-with-file / processing reset) ----

    private void PasteClipboard()
    {
        try
        {
            if (!Clipboard.ContainsImage()) { ShowToast(L.Get("Pin.PasteEmpty")); return; }
            var src = Clipboard.GetImage();
            var buf = src is null ? null : ToPixelBuffer(src);
            if (buf is null) { ShowToast(L.Get("Pin.PasteEmpty")); return; }
            ReplaceImage(buf);
        }
        catch (Exception ex)
        {
            Log.Error("Pin paste failed", ex);
            ShowToast(L.Get("Toast.SaveFailed", ex.Message));
        }
    }

    private void ReplaceFromFile()
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = L.Get("Pin.ReplaceFile"),
                Filter = "Images|*.png;*.jpg;*.jpeg;*.webp;*.bmp|PNG|*.png|JPEG|*.jpg;*.jpeg|WebP|*.webp|BMP|*.bmp",
            };
            if (dialog.ShowDialog(this) != true) return;
            var src = new BitmapImage();
            src.BeginInit();
            src.CacheOption = BitmapCacheOption.OnLoad;
            // StreamSource instead of UriSource: Uri treats '#' as a fragment, so any path
            // containing one (e.g. "shot#1.png") would resolve to a non-existent file.
            src.StreamSource = System.IO.File.OpenRead(dialog.FileName);
            src.EndInit();
            var buf = ToPixelBuffer(src);
            if (buf is null) { ShowToast(L.Get("Pin.PasteEmpty")); return; }
            ReplaceImage(buf);
        }
        catch (Exception ex)
        {
            Log.Error("Pin replace-from-file failed", ex);
            ShowToast(L.Get("Toast.SaveFailed", ex.Message));
        }
    }

    /// <summary>Swaps the pinned image: resets orientation, refits the zoom, refreshes pixels.</summary>
    private void ReplaceImage(PixelBuffer image)
    {
        _image = image;
        _rotDeg = 0;
        _flipH = false;
        ApplyOrientationTransform();
        _preview.Source = _image.ToBitmapSource();
        _zoom = FitZoom(_toastWorkArea);
        SizeWindow();
    }

    private static PixelBuffer? ToPixelBuffer(BitmapSource source)
    {
        try
        {
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            if (w <= 0 || h <= 0) return null;
            var buf = new PixelBuffer(w, h);
            bgra.CopyPixels(buf.Data, w * 4, 0);
            return buf;
        }
        catch (Exception ex)
        {
            Log.Error("Converting clipboard/file image to PixelBuffer failed", ex);
            return null;
        }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.15, 1.0);
            return;
        }
        ApplyZoom(e.Delta > 0 ? 1.1 : 1 / 1.1);
        e.Handled = true;
    }

    private void SaveDialog()
    {
        try
        {
            var path = _exporter.SaveAs(_image, FileName(), this); // owner: the topmost pin must not cover the dialog
            if (path is not null) ShowToast(L.Get("Toast.Saved", path));
        }
        catch (Exception ex)
        {
            Log.Error("Pin save failed", ex);
            ShowToast(L.Get("Toast.SaveFailed", ex.Message));
        }
    }

    private static string FileName() => $"Pin_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}";

    /// <summary>Tiny self-hosted feedback label so pinning works before the global toast (T8).</summary>
    private void ShowToast(string message)
    {
        // A second toast within the 2s window replaces the first in place (state indication):
        // same position, text swapped, timer restarted — no entrance replay, no ghost overlap.
        // Once the old toast has ticked into its fade-out the fields are already null, so a
        // fresh toast is created alongside the fading one instead of restyling a dying window.
        if (_toast is { IsLoaded: true } && _toastText is not null && _toastTimer is not null)
        {
            _toastTimer.Stop();
            _toastText.Text = message;
            _toastTimer.Start();
            return;
        }
        // The visual root is the TextBlock — the window layer itself is fully transparent, so the
        // motion (audit #6) targets the root only; animating the window would double-apply the fade.
        var content = new TextBlock
        {
            Text = message,
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E)),
            Padding = new Thickness(14, 8, 14, 8),
        };
        var toast = new Window
        {
            Content = content,
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        void PlaceToast()
        {
            var area = _toastWorkArea;
            toast.Left = Math.Max(area.Left + 8, area.Right - toast.ActualWidth - 24);
            toast.Top = Math.Max(area.Top + 8, area.Bottom - toast.ActualHeight - 24);
        }
        toast.Loaded += (_, _) => PlaceToast();
        // SizeToContent re-sizes anchor top-left: a longer replacement message would grow
        // right/bottom off the corner — re-anchor on every content-driven size change
        // (fires inside the layout pass, before paint).
        toast.SizeChanged += (_, _) => PlaceToast();
        // Entrance: slide+fade the root before Show() so the first painted frame already has the
        // start pose. Fresh elements every call, so the animation always replays from the start.
        UiMotion.FadeSlideIn(content, 0, 8, 150);
        toast.Show();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        // Exit: the 2s auto-timeout path fades first and closes from the completion callback;
        // user-initiated closes (double-click / context menu on the pin itself) stay instant.
        // Fields are released at Tick (not at Close) so a toast arriving mid-fade-out creates a
        // fresh window instead of reusing one that is already on its way out.
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _toast = null;
            _toastText = null;
            _toastTimer = null;
            UiMotion.FadeOut(content, 120, onCompleted: () => toast.Close());
        };
        timer.Start();
        _toast = toast;
        _toastText = content;
        _toastTimer = timer;
    }
}
