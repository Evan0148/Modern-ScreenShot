using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Localization;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.History;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Settings;
using CaptureMode = ModernScreenShot.Core.Settings.CaptureMode;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Output;

/// <summary>
/// Thumbnail grid of past captures. Double-click reopens an item in the editor (original.png +
/// doc.json), context menu copies or deletes, toolbar opens the folder / clears everything.
/// </summary>
public sealed class HistoryWindow : Window
{
    private readonly HistoryStore _store;
    private readonly SettingsStore _settings;
    private readonly ClipboardService _clipboard;
    private readonly Action<CaptureResult, AnnotationDocument?> _openInEditor;
    private readonly WrapPanel _grid = new() { Margin = new Thickness(8) };
    private readonly TextBlock _countLabel = new() { Foreground = Brushes.Gray, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };

    public HistoryWindow(HistoryStore store, SettingsStore settings, ClipboardService clipboard,
        Action<CaptureResult, AnnotationDocument?> openInEditor)
    {
        _store = store;
        _settings = settings;
        _clipboard = clipboard;
        _openInEditor = openInEditor;

        Title = L.Get("History.Title");
        Width = 960;
        Height = 640;
        MinWidth = 620;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var toolbar = new DockPanel();
        var left = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8) };
        left.Children.Add(MakeButton(L.Get("Action.OpenFolder"), (_, _) => OpenFolder(null)));
        left.Children.Add(MakeButton(L.Get("History.ClearAll"), (_, _) => ClearAll()));
        left.Children.Add(_countLabel);
        toolbar.Children.Add(left);
        var close = MakeButton(L.Get("Action.Close"), (_, _) => Close());
        DockPanel.SetDock(close, Dock.Right);
        toolbar.Children.Add(close);

        var root = new DockPanel();
        root.Children.Add(toolbar);
        DockPanel.SetDock(toolbar, Dock.Top);
        var scroll = new ScrollViewer { Content = _grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);
        Content = root;

        store.Changed += (_, _) => Dispatcher.BeginInvoke(Refresh);
        Loaded += (_, _) => Refresh();
    }

    private Button MakeButton(string text, RoutedEventHandler onClick)
    {
        var b = new Button { Content = text, Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand };
        b.Click += onClick;
        return b;
    }

    private void Refresh()
    {
        _grid.Children.Clear();
        var entries = _store.List();
        _countLabel.Text = L.Get("History.Count", entries.Count);
        if (entries.Count == 0)
        {
            _grid.Children.Add(new TextBlock { Text = L.Get("History.Empty"), Foreground = Brushes.Gray, Margin = new Thickness(8) });
            return;
        }
        foreach (var entry in entries) _grid.Children.Add(MakeItem(entry));
    }

    private FrameworkElement MakeItem(HistoryEntry entry)
    {
        var image = new Image { Width = 168, Stretch = Stretch.Uniform, Margin = new Thickness(4) };
        try
        {
            image.Source = BitmapInterop.LoadPng(entry.ThumbnailPath).ToBitmapSource();
        }
        catch (Exception ex)
        {
            Log.Warn($"Thumbnail missing for {entry.Id}: {ex.Message}");
        }

        var text = new TextBlock
        {
            Text = entry.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss") + (string.IsNullOrEmpty(entry.WindowTitle) ? "" : $"\n{entry.WindowTitle}"),
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 11,
            Foreground = Brushes.Gray,
            Margin = new Thickness(2),
        };

        var card = new Border
        {
            Child = new StackPanel { Children = { image, text } },
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x48)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Margin = new Thickness(4),
            Width = 176,
            Cursor = Cursors.Hand,
        };

        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(L.Get("Action.Open"), (_, _) => OpenInEditor(entry)));
        menu.Items.Add(MenuItem(L.Get("Action.Copy"), (_, _) => CopyEntry(entry)));
        menu.Items.Add(MenuItem(L.Get("Action.Delete"), (_, _) => _store.Delete(entry)));
        menu.Items.Add(MenuItem(L.Get("Action.OpenFolder"), (_, _) => OpenFolder(entry)));
        card.ContextMenu = menu;
        card.ToolTip = $"{entry.Width}×{entry.Height}";

        card.MouseLeftButtonDown += (_, e) => { if (e.ClickCount >= 2) OpenInEditor(entry); };
        return card;
    }

    private MenuItem MenuItem(string header, RoutedEventHandler onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += onClick;
        return item;
    }

    private void OpenInEditor(HistoryEntry entry)
    {
        try
        {
            var image = BitmapInterop.LoadPng(entry.OriginalPath);
            var doc = _store.LoadDocument(entry) ?? new AnnotationDocument
            {
                ImageWidth = image.Width,
                ImageHeight = image.Height,
                WindowTitle = entry.WindowTitle,
                CaptureMode = entry.Mode,
            };
            var result = new CaptureResult
            {
                Image = image,
                Mode = Enum.TryParse<CaptureMode>(entry.Mode, out var mode) ? mode : CaptureMode.Region,
                WindowTitle = entry.WindowTitle,
                SourceRect = new PixelRect(0, 0, image.Width, image.Height),
            };
            _openInEditor(result, doc);
        }
        catch (Exception ex)
        {
            Log.Error($"Opening history item {entry.Id} failed", ex);
        }
    }

    private void CopyEntry(HistoryEntry entry)
    {
        try
        {
            if (_clipboard.TryPutImage(BitmapInterop.LoadPng(entry.OriginalPath)))
                Log.Info($"History item {entry.Id} copied.");
        }
        catch (Exception ex)
        {
            Log.Error($"Copying history item {entry.Id} failed", ex);
        }
    }

    private void OpenFolder(HistoryEntry? entry)
    {
        string target = entry?.Directory ?? AppPaths.HistoryDir;
        if (!Directory.Exists(target)) Directory.CreateDirectory(target);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
    }

    private void ClearAll()
    {
        var answer = MessageBox.Show(this, L.Get("History.ConfirmClear"), L.Get("History.ClearAll"),
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer == MessageBoxResult.Yes) _store.Clear();
    }
}
