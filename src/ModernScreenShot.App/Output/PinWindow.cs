using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Imaging;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Output;

/// <summary>
/// Borderless topmost floating image ("pin to screen"). Drag to move, wheel zoom,
/// Ctrl+wheel opacity, double-click close, right-click menu with copy / save / edit / close.
/// </summary>
public sealed class PinWindow : Window
{
    private readonly PixelBuffer _image;
    private readonly Action<PixelBuffer>? _editCallback;
    private readonly Func<string>? _openFolderCallback;
    private readonly ImageExporter _exporter;
    private readonly ClipboardService _clipboard;
    private double _zoom = 1;

    public PinWindow(PixelBuffer image, ImageExporter exporter, ClipboardService clipboard,
        Action<PixelBuffer>? editCallback, Func<string>? openFolderCallback)
    {
        _image = image;
        _exporter = exporter;
        _clipboard = clipboard;
        _editCallback = editCallback;
        _openFolderCallback = openFolderCallback;

        Title = L.Get("Action.Pin");
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;

        var workArea = SystemParameters.WorkArea;
        double dipW = image.Width, dipH = image.Height;
        double fit = Math.Min(1.0, Math.Min(workArea.Width * 0.9 / dipW, workArea.Height * 0.9 / dipH));
        _zoom = fit;
        Width = dipW * _zoom;
        Height = dipH * _zoom;
        Left = workArea.Left + (workArea.Width - Width) / 2;
        Top = workArea.Top + (workArea.Height - Height) / 2;

        var preview = new Image
        {
            Source = image.ToBitmapSource(),
            Stretch = Stretch.Fill,
        };
        RenderOptions.SetBitmapScalingMode(preview, BitmapScalingMode.Fant);
        var host = new Border
        {
            Child = preview,
            CornerRadius = new CornerRadius(6),
            ClipToBounds = true,
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18,
                ShadowDepth = 2,
                Opacity = 0.45,
            },
        };
        Content = host;
        BuildContextMenu();
        Cursor = Cursors.SizeAll;
        MouseLeftButtonDown += (_, e) => { if (e.ClickCount >= 2) Close(); else DragMove(); };
        MouseWheel += OnWheel;
    }

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(L.Get("Action.Copy"), (_, _) =>
        {
            if (_clipboard.TryPutImage(_image)) ShowToast(L.Get("Toast.Copied"));
        }));
        menu.Items.Add(MenuItem(L.Get("Action.Save"), (_, _) => SaveDialog()));
        if (_editCallback is not null)
            menu.Items.Add(MenuItem(L.Get("Action.Edit"), (_, _) => _editCallback(_image)));
        if (_openFolderCallback is not null)
            menu.Items.Add(MenuItem(L.Get("Action.OpenFolder"), (_, _) => _openFolderCallback()));
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

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Opacity = Math.Clamp(Opacity + (e.Delta > 0 ? 0.1 : -0.1), 0.15, 1.0);
            return;
        }
        double factor = e.Delta > 0 ? 1.1 : 1 / 1.1;
        double newZoom = Math.Clamp(_zoom * factor, 0.1, 4);
        if (Math.Abs(newZoom - _zoom) < 0.0001) return;
        _zoom = newZoom;
        Width = _image.Width * _zoom;
        Height = _image.Height * _zoom;
        e.Handled = true;
    }

    private void SaveDialog()
    {
        try
        {
            var path = _exporter.SaveAs(_image, FileName());
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
        var toast = new Window
        {
            Content = new TextBlock
            {
                Text = message,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E)),
                Padding = new Thickness(14, 8, 14, 8),
            },
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            ShowInTaskbar = false,
            ShowActivated = false,
            Topmost = true,
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.Manual,
        };
        toast.Loaded += (_, _) =>
        {
            var area = SystemParameters.WorkArea;
            toast.Left = area.Right - toast.ActualWidth - 24;
            toast.Top = area.Bottom - toast.ActualHeight - 24;
        };
        toast.Show();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        timer.Tick += (_, _) => { timer.Stop(); toast.Close(); };
        timer.Start();
    }
}
