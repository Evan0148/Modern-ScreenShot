using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using ModernScreenShot.App.Controls;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Localization;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Settings;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Shell;

/// <summary>
/// First-run welcome (OOBE). The entrance animation dramatises this app's core action: a mouse cursor
/// drags out a blue selection rectangle — the same look as the real region-capture overlay (dim mask
/// outside, #0A84FF accent border, white corner handles, a size read-out label) — then the captured
/// box transitions into the gradient word "ScreenShot", and finally the plain word "Modern" slides in
/// from the left, forming "Modern ScreenShot". Below it a tagline and three tips stagger in, with
/// Skip / Get started actions.
///
/// Notes:
///  - The selection visuals reuse the exact colours/metrics of Overlay/OverlayRenderer (AccentPen
///    #0A84FF/2px, dim #59000000, translucent fill #260A84FF, 10px white handles) so the intro reads
///    as "you are taking a screenshot".
///  - All motion is GPU-composited (TranslateTransform / ScaleTransform / opacity); nothing animates a
///    layout property, so the intro stays smooth. Static brushes/pens are frozen and shared.
///  - Honours <see cref="UiMotion.Suppress"/> (diagnostic --render paths snap to the final frame) and
///    reduced motion (System.ClientAreaAnimation) by skipping straight to the settled state. A 5s
///    watchdog force-settles a stalled chain so the window can never stay blank.
/// </summary>
public sealed class OobeWindow : Window
{
    private const string TargetWord = "ScreenShot"; // the word the selection box becomes

    // Selection-overlay palette (mirrors Overlay/OverlayRenderer so the box looks like a real capture).
    private static readonly Color AccentColor = Color.FromRgb(0x0A, 0x84, 0xFF);
    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(AccentColor));
    private static readonly Brush SelectionFill = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0x0A, 0x84, 0xFF)));
    private static readonly Brush HandleFill = Frozen(Brushes.White);
    private static readonly Brush LabelBg = Frozen(new SolidColorBrush(Color.FromArgb(0xE0, 0x1C, 0x1C, 0x1E)));
    private static readonly Color GradA = Color.FromRgb(0x00, 0xCC, 0xFF); // #00CCFF
    private static readonly Color GradB = Color.FromRgb(0x00, 0x7F, 0xFF); // #007FFF
    private static readonly Brush GradientBrush = Frozen(new LinearGradientBrush
    {
        StartPoint = new Point(0, 0),
        EndPoint = new Point(1, 1),
        GradientStops = { new GradientStop(GradA, 0), new GradientStop(GradB, 1) },
    });

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        if (freezable.CanFreeze) freezable.Freeze();
        return freezable;
    }

    private readonly Action _onCompleted;
    private readonly bool _persistSettings; // false = debug preview: show the flow, change nothing
    private bool _finished;
    private bool _animationStarted;
    private readonly List<DispatcherTimer> _timers = [];

    // ---- stage geometry (in _stage-local DIPs) ----
    // The selection box is dragged from its top-left corner to its bottom-right; these are the final
    // box bounds, centred in the stage band.
    private const double StageW = 560;
    private const double StageH = 150;
    private const double BoxW = 300;
    private const double BoxH = 96;
    private static readonly double BoxLeft = (StageW - BoxW) / 2d;
    private static readonly double BoxTop = (StageH - BoxH) / 2d;

    // Intro visual parts.
    private Grid _host = null!;              // the content area below the title bar
    private Grid _stage = null!;             // the selection-scene band, centred over the whole host
    private Canvas _selectionScene = null!;  // box + handles + label + cursor
    private Grid _wordsRow = null!;          // the two words, compact row at the top of the column
    private TranslateTransform _wordsRowTranslate = null!; // carries the rise: word born at the box, glides up into its slot
    private Rectangle _boxFill = null!;      // translucent blue interior
    private Rectangle _boxBorder = null!;    // accent border
    private readonly List<Rectangle> _handles = [];
    private Border _sizeLabel = null!;
    private TextBlock _sizeLabelText = null!;
    private Path _cursor = null!;
    private TranslateTransform _cursorTranslate = null!;

    private TextBlock _word = null!;         // "ScreenShot", gradient — grows out of the box
    private ScaleTransform _wordScale = null!;
    private TextBlock _slideWord = null!;    // "Modern", plain — slides in from the left
    private TranslateTransform _slideTranslate = null!;
    private ScaleTransform _slideScale = null!;

    private StackPanel _chrome = null!;      // everything below the words: intro section + config panel
    private StackPanel _introSection = null!; // tagline + tips + Skip/Get started, hidden after the intro
    private StackPanel _configPanel = null!;  // the preference rows, revealed by Get started
    private Button _getStartedButton = null!;

    public OobeWindow(Action onCompleted, bool persistSettings = true)
    {
        _onCompleted = onCompleted;
        _persistSettings = persistSettings;

        Title = L.Get("Oobe.Welcome");
        Width = 720;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = true;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");

        Content = BuildContent();
        AppTitleBar.Attach(this, resizeBorderThickness: 0); // custom title bar replaces the OS caption

        // Start trigger: IsVisibleChanged fires reliably when the HWND becomes visible; Loaded is kept
        // as a parallel path. Both funnel into StartAnimationOnce, which is idempotent.
        IsVisibleChanged += OnVisibleChanged;
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => StopTimers();
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Finish(); };
    }

    // ---- layout ----

    private UIElement BuildContent()
    {
        var titleBar = new AppTitleBar { Text = L.Get("Oobe.Welcome"), ShowMin = false, ShowMax = false, ShowClose = true };

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var host = new Grid { Margin = new Thickness(48, 8, 48, 40) };
        Grid.SetRow(host, 1);
        root.Children.Add(host);
        _host = host;

        // Two independent layers:
        //  - the selection-scene band, centred over the WHOLE host, so the dragged box plays at the true
        //    window centre regardless of the text column;
        //  - a compact centred column (words row + tagline/tips/actions) — the final text block, centred
        //    as a unit with an even rhythm (no dead band inflating the gap under the wordmark).
        // The morph crossfades between them and glides the words up from the box into their slot.
        host.Children.Add(BuildStage());
        host.Children.Add(BuildWordsColumn());
        return root;
    }

    private UIElement BuildStage()
    {
        _stage = new Grid
        {
            Width = StageW,
            Height = StageH,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _stage.Children.Add(BuildSelectionScene());
        return _stage;
    }

    private UIElement BuildWordsColumn()
    {
        var column = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        _wordsRowTranslate = new TranslateTransform();
        _wordsRow = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            RenderTransform = _wordsRowTranslate,
        };

        _wordScale = new ScaleTransform(0.6, 0.6);
        _word = new TextBlock
        {
            Text = TargetWord,
            FontSize = 40,
            FontWeight = FontWeights.SemiBold,
            Foreground = GradientBrush,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _wordScale,
        };

        _slideTranslate = new TranslateTransform();
        _slideScale = new ScaleTransform(0.965, 0.965);
        var slideGroup = new TransformGroup();
        slideGroup.Children.Add(_slideScale);
        slideGroup.Children.Add(_slideTranslate);
        _slideWord = new TextBlock
        {
            Text = "Modern",
            FontSize = 40,
            FontWeight = FontWeights.SemiBold,
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = slideGroup,
        };
        _slideWord.SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        _wordsRow.Children.Add(_word);
        _wordsRow.Children.Add(_slideWord);
        column.Children.Add(_wordsRow);
        column.Children.Add(BuildChrome());
        return column;
    }

    private UIElement BuildSelectionScene()
    {
        _selectionScene = new Canvas { Width = StageW, Height = StageH, Opacity = 0 };

        // The selection box draws directly on the window background (no dim mask): just the translucent
        // blue fill and the accent border, so the animation reads as a box being drawn in place rather
        // than the full-screen "dim everything else" capture overlay. Both rects are PRE-SIZED to the
        // full box and revealed with a top-left-anchored ScaleTransform — the drag is then a pure
        // GPU-composited scale (never a per-frame Width/Height layout animation).
        _boxFill = new Rectangle
        {
            Fill = SelectionFill,
            Width = BoxW,
            Height = BoxH,
            RenderTransformOrigin = new Point(0, 0),
            RenderTransform = new ScaleTransform(0, 0),
        };
        _boxBorder = new Rectangle
        {
            Stroke = AccentBrush,
            StrokeThickness = 2,
            Width = BoxW,
            Height = BoxH,
            RenderTransformOrigin = new Point(0, 0),
            RenderTransform = new ScaleTransform(0, 0),
        };
        // Both rects sit at the box's canvas slot — the scale then grows them from their top-left
        // corner, exactly like a drag from that point. (Missing this placed the box at the canvas
        // origin while the cursor ran at the real slot — the regression the user screenshotted.)
        Canvas.SetLeft(_boxFill, BoxLeft);
        Canvas.SetTop(_boxFill, BoxTop);
        Canvas.SetLeft(_boxBorder, BoxLeft);
        Canvas.SetTop(_boxBorder, BoxTop);
        _selectionScene.Children.Add(_boxFill);
        _selectionScene.Children.Add(_boxBorder);

        // Four white corner handles (10px), drawn once the drag settles.
        for (int i = 0; i < 4; i++)
        {
            var handle = new Rectangle
            {
                Width = 10,
                Height = 10,
                Fill = HandleFill,
                Stroke = AccentBrush,
                StrokeThickness = 1.5,
                Opacity = 0,
            };
            _handles.Add(handle);
            _selectionScene.Children.Add(handle);
        }

        // Size read-out label (like the overlay's WxH badge), pinned above the box.
        _sizeLabelText = new TextBlock { Foreground = Brushes.White, FontSize = 12 };
        _sizeLabel = new Border
        {
            Background = LabelBg,
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 3),
            Opacity = 0,
            Child = _sizeLabelText,
        };
        _selectionScene.Children.Add(_sizeLabel);

        // Mouse cursor (a simple arrow) that drags the box out.
        _cursorTranslate = new TranslateTransform();
        _cursor = new Path
        {
            Data = Geometry.Parse("M0,0 L0,16 L4.5,12 L7,17.5 L9.5,16.5 L7,11 L13,11 Z"),
            Fill = Brushes.White,
            Stroke = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)),
            StrokeThickness = 1,
            Opacity = 0,
            RenderTransform = _cursorTranslate,
            IsHitTestVisible = false,
        };
        _selectionScene.Children.Add(_cursor);

        return _selectionScene;
    }

    private UIElement BuildChrome()
    {
        _chrome = new StackPanel
        {
            Opacity = 0,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 28, 0, 0),
            MaxWidth = 520,
        };

        // Intro section: tagline + tips + the Skip / Get started row. Collapses when the user moves on
        // to the preferences panel.
        _introSection = new StackPanel { RenderTransform = new TranslateTransform() };

        _introSection.Children.Add(new TextBlock
        {
            FontSize = 15,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.85,
            Margin = new Thickness(0, 0, 0, 24),
        });
        ((TextBlock)_introSection.Children[^1]).SetResourceReference(TextBlock.TextProperty, "Oobe.Tagline");

        AddTip("\uE7B3", "Oobe.TipRegion");
        AddTip("\uE700", "Oobe.TipTray");
        AddTip("\uE70F", "Oobe.TipEditor");

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 28, 0, 0),
            RenderTransform = new TranslateTransform(),
        };
        var skip = new Wpf.Ui.Controls.Button
        {
            MinWidth = 110,
            Margin = new Thickness(0, 0, 12, 0),
            Appearance = Wpf.Ui.Controls.ControlAppearance.Secondary,
        };
        skip.SetResourceReference(ContentControl.ContentProperty, "Oobe.Skip");
        skip.Click += (_, _) => Finish();
        _getStartedButton = new Wpf.Ui.Controls.Button
        {
            MinWidth = 150,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
        };
        _getStartedButton.SetResourceReference(ContentControl.ContentProperty, "Oobe.GetStarted");
        _getStartedButton.Click += (_, _) => ShowConfig();
        actions.Children.Add(skip);
        actions.Children.Add(_getStartedButton);
        _introSection.Children.Add(actions);
        _chrome.Children.Add(_introSection);

        // Preferences section: hidden until "Get started". Real first-run persists every choice;
        // the debug preview shows the same UI but changes nothing.
        _configPanel = BuildConfigPanel();
        _configPanel.Visibility = Visibility.Collapsed;
        _chrome.Children.Add(_configPanel);

        return _chrome;
    }

    private void AddTip(string glyph, string textKey)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 0, 12),
            RenderTransform = new TranslateTransform(),
        };
        row.Children.Add(new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = 18,
            Width = 30,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = AccentBrush,
        });
        var text = new TextBlock
        {
            FontSize = 14,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.9,
        };
        text.SetResourceReference(TextBlock.TextProperty, textKey); // DynamicResource → live language switch
        row.Children.Add(text);
        _introSection!.Children.Add(row);
    }

    // ---- preferences panel (OOBE step 2) ----

    private System.Windows.Controls.ComboBox? _langCombo;
    private System.Windows.Controls.ComboBox? _themeCombo;
    private System.Windows.Controls.ComboBox? _afterCombo;
    private Wpf.Ui.Controls.ToggleSwitch? _autostartToggle;
    private Wpf.Ui.Controls.Button? _doneButton;
    private bool _configLoading = true;
    private AfterCaptureAction _afterAction; // initialised from current settings (never a hardcoded default)

    /// <summary>Builds the preference rows: language, theme, start-with-Windows and the default
    /// after-capture action. Labels are resource-referenced so a live language switch updates them.</summary>
    private StackPanel BuildConfigPanel()
    {
        var panel = new StackPanel { RenderTransform = new TranslateTransform() };

        var title = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4), TextAlignment = TextAlignment.Center };
        title.SetResourceReference(TextBlock.TextProperty, "Oobe.ConfigTitle");
        panel.Children.Add(title);

        var sub = new TextBlock { FontSize = 13, Opacity = 0.75, Margin = new Thickness(0, 0, 0, 18), TextAlignment = TextAlignment.Center };
        sub.SetResourceReference(TextBlock.TextProperty, "Oobe.ConfigSub");
        panel.Children.Add(sub);

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 6, 18, 6),
            BorderThickness = new Thickness(1),
        };
        card.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");

        var rows = new StackPanel();
        card.Child = rows;

        _langCombo = MakeCombo("Settings.LangSystem", "Tray.LangChinese", "Tray.LangEnglish");
        _langCombo.SelectedIndex = CurrentLanguageIndex();
        _langCombo.SelectionChanged += OnOobeLanguageChanged;
        rows.Children.Add(MakeConfigRow("Oobe.LangLabel", _langCombo));

        _themeCombo = MakeCombo("Settings.ThemeSystem", "Settings.ThemeLight", "Settings.ThemeDark");
        _themeCombo.SelectedIndex = CurrentThemeIndex();
        _themeCombo.SelectionChanged += OnOobeThemeChanged;
        rows.Children.Add(MakeConfigRow("Oobe.ThemeLabel", _themeCombo));

        _autostartToggle = new Wpf.Ui.Controls.ToggleSwitch();
        rows.Children.Add(MakeConfigRow("Settings.StartWithWindows", _autostartToggle, rightAlignControl: true));
        _autostartToggle.IsChecked = TryReadCurrent(s => s.StartWithWindows);
        _autostartToggle.Checked += OnOobeAutostartChanged;
        _autostartToggle.Unchecked += OnOobeAutostartChanged;

        _afterCombo = MakeCombo(
            "Settings.After.ShowToolbar", "Settings.After.OpenEditor", "Settings.After.CopyOnly",
            "Settings.After.SaveOnly", "Settings.After.Pin", "Settings.After.Thumbnail");
        _afterAction = TryReadCurrent(s => s.Output.AfterRegionCapture); // never overwrite the user's choice with a default
        _afterCombo.SelectedIndex = Math.Clamp((int)_afterAction, 0, 5);
        _afterCombo.SelectionChanged += OnOobeAfterChanged;
        rows.Children.Add(MakeConfigRow("Oobe.AfterLabel", _afterCombo));

        panel.Children.Add(card);

        var done = new Wpf.Ui.Controls.Button
        {
            MinWidth = 150,
            Margin = new Thickness(0, 18, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Center,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Primary,
        };
        done.SetResourceReference(ContentControl.ContentProperty, "Oobe.Done");
        done.Click += (_, _) => CompleteOobe();
        panel.Children.Add(done);
        _doneButton = done;

        _configLoading = false;
        return panel;
    }

    /// <summary>Control-column width shared by every row so all controls' right edges align (each row
    /// is its own Grid; an Auto column would size per-row and let the toggle drift out of line).</summary>
    private const double ConfigControlWidth = 190;

    /// <summary>One preference row: resource-referenced label on the left, control in a fixed-width
    /// column on the right so every row's control edge lines up. Stretching controls (combos) fill the
    /// column; right-aligned controls (the toggle) keep their NATURAL width — forcing MinWidth on a
    /// ToggleSwitch only widens its layout slot while the template keeps drawing the track at natural
    /// width on the left, which is exactly the "toggle stuck to the label" bug.</summary>
    private UIElement MakeConfigRow(string labelKey, FrameworkElement control, bool rightAlignControl = false)
    {
        var grid = new Grid { Margin = new Thickness(0, 10, 0, 10) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(ConfigControlWidth) });
        var label = new TextBlock { FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        label.SetResourceReference(TextBlock.TextProperty, labelKey);
        if (rightAlignControl)
        {
            control.HorizontalAlignment = HorizontalAlignment.Right;
            control.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            control.MinWidth = ConfigControlWidth; // combos stretch to the full column
        }
        Grid.SetColumn(label, 0);
        Grid.SetColumn(control, 1);
        grid.Children.Add(label);
        grid.Children.Add(control);
        return grid;
    }

    private System.Windows.Controls.ComboBox MakeCombo(params string[] itemKeys)
    {
        var combo = new System.Windows.Controls.ComboBox { MinWidth = 190 };
        foreach (var key in itemKeys)
        {
            var item = new System.Windows.Controls.ComboBoxItem();
            item.SetResourceReference(ContentControl.ContentProperty, key);
            combo.Items.Add(item);
        }
        return combo;
    }

    private int CurrentLanguageIndex() => TryReadCurrent(s => s.Language) switch
    {
        LocalizationService.Chinese => 1,
        LocalizationService.English => 2,
        _ => 0,
    };

    private int CurrentThemeIndex() => TryReadCurrent(s => s.Theme) switch
    {
        "Light" => 1,
        "Dark" => 2,
        _ => 0,
    };

    private T TryReadCurrent<T>(Func<AppSettings, T> pick) where T : notnull
    {
        try
        {
            var store = App.Services.GetRequiredService<SettingsStore>();
            return pick(store.Current);
        }
        catch
        {
            // Services unavailable (shouldn't happen in this app's lifetime): fall back to defaults.
            return pick(new AppSettings());
        }
    }

    private void OnOobeLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_configLoading || _langCombo is null) return;
        string language = _langCombo.SelectedIndex switch
        {
            1 => LocalizationService.Chinese,
            2 => LocalizationService.English,
            _ => "",
        };
        if (!_persistSettings) return; // preview: show, don't touch
        try
        {
            var store = App.Services.GetRequiredService<SettingsStore>();
            store.Current.Language = language;
            App.Services.GetRequiredService<LocalizationService>().Apply(language); // resource refs update live
        }
        catch (Exception ex) { Log.Error("Applying the OOBE language choice failed", ex); }
    }

    private void OnOobeThemeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_configLoading || _themeCombo is null) return;
        string theme = _themeCombo.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };
        if (!_persistSettings) return;
        try
        {
            var store = App.Services.GetRequiredService<SettingsStore>();
            store.Current.Theme = theme;
            App.ApplyTheme(theme); // instant preview of the choice
        }
        catch (Exception ex) { Log.Error("Applying the OOBE theme choice failed", ex); }
    }

    private void OnOobeAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (_configLoading || _autostartToggle is null || !_persistSettings) return;
        bool enable = _autostartToggle.IsChecked == true;
        try
        {
            const string keyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
            const string valueName = "Modern-ScreenShot";
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            if (enable)
            {
                string exe = Environment.ProcessPath ?? "";
                if (exe.Length == 0) throw new InvalidOperationException("Process path unavailable.");
                key.SetValue(valueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(valueName, throwOnMissingValue: false);
            }
            App.Services.GetRequiredService<SettingsStore>().Current.StartWithWindows = enable;
            Log.Info($"OOBE: start with Windows {(enable ? "enabled" : "disabled")}.");
        }
        catch (Exception ex)
        {
            Log.Error("Applying the OOBE start-with-Windows choice failed", ex);
            _autostartToggle.IsChecked = TryReadCurrent(s => s.StartWithWindows);
        }
    }

    private void OnOobeAfterChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_configLoading || _afterCombo is null) return;
        if (Enum.IsDefined((AfterCaptureAction)_afterCombo.SelectedIndex))
            _afterAction = (AfterCaptureAction)_afterCombo.SelectedIndex;
    }

    /// <summary>Commits the panel's choices (real first-run only). Called from Done and from every
    /// close path, so a user who changed something and dismissed via Esc / the title-bar X still keeps
    /// ALL of it — language/theme/autostart applied live, and the after-capture choice is committed
    /// here (it had no live effect). Requires the panel to have actually been shown: dismissing on the
    /// intro (Skip, or the smoke test's construct-and-close) must never touch the settings file.
    /// Idempotent.</summary>
    private void PersistChoices()
    {
        if (!_persistSettings || !_configShown || _configLoading) return;
        try
        {
            var store = App.Services.GetRequiredService<SettingsStore>();
            store.Current.Output.AfterRegionCapture = _afterAction;
            store.Save(); // language/theme/autostart were already applied live on change
            Log.Info("OOBE preferences saved.");
        }
        catch (Exception ex) { Log.Error("Saving the OOBE preferences failed", ex); }
    }

    /// <summary>"Done": commit the choices, then finish exactly like Skip does.</summary>
    private void CompleteOobe()
    {
        PersistChoices();
        Finish();
    }

    /// <summary>Get started → swap the intro section for the preferences panel. The two sections have
    /// different heights, so the centred column would make the wordmark jump; a FLIP seed (record the
    /// word row's window position before the swap, translate it by the delta after, glide to 0) turns
    /// the layout change into one smooth vertical slide.</summary>
    private void ShowConfig()
    {
        if (_introSection is null || _configPanel is null || _configShown) return;
        _configShown = true;

        // FLIP "first": where the wordmark sits right now.
        Point before = _wordsRow.TransformToVisual(this).Transform(new Point(0, 0));

        UiMotion.FadeOut(_introSection, 150, () =>
        {
            if (_finished) return;
            _introSection.Visibility = Visibility.Collapsed;
            _configPanel.Visibility = Visibility.Visible;

            // FLIP "last" + "invert": seed the row at the visual delta, then glide to 0.
            _wordsRow.UpdateLayout();
            Point after = _wordsRow.TransformToVisual(this).Transform(new Point(0, 0));
            double delta = before.Y - after.Y;
            if (Math.Abs(delta) > 0.5)
            {
                _wordsRowTranslate.Y = delta;
                StartAnim(_wordsRowTranslate, TranslateTransform.YProperty, delta, 0, 260, 0, ExpOut(4));
            }

            UiMotion.FadeSlideIn(_configPanel, 0, 10, 220);
            _doneButton?.Focus();
        });
    }

    private bool _configShown;

    // ---- animation driver ----

    private void StartAnim(IAnimatable target, DependencyProperty prop,
        double from, double to, double durationMs, double beginMs, IEasingFunction? easing)
    {
        var anim = new DoubleAnimation
        {
            From = from,
            To = to,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd,
        };
        // BeginTime must stay default (Zero) for an immediate start: a NULL BeginTime makes a WPF
        // timeline NEVER begin. Only set it when there is a real delay.
        if (beginMs > 0) anim.BeginTime = TimeSpan.FromMilliseconds(beginMs);
        anim.Completed += (_, _) =>
        {
            if (_finished) return;
            target.BeginAnimation(prop, null);
            ((DependencyObject)target).SetValue(prop, to);
        };
        target.BeginAnimation(prop, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private static CubicEase CubicOut() => new() { EasingMode = EasingMode.EaseOut };
    private static CubicEase CubicInOut() => new() { EasingMode = EasingMode.EaseInOut };
    private static ExponentialEase ExpOut(double exp = 5) => new() { EasingMode = EasingMode.EaseOut, Exponent = exp };
    private static BackEase BackOut() => new() { EasingMode = EasingMode.EaseOut, Amplitude = 0.5 };

    private DispatcherTimer After(double ms, Action action)
    {
        var t = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(Math.Max(0, ms)) };
        t.Tick += (_, _) => { t.Stop(); _timers.Remove(t); action(); };
        _timers.Add(t);
        t.Start();
        return t;
    }

    private void StopTimers()
    {
        foreach (var t in _timers) t.Stop();
        _timers.Clear();
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => StartAnimationOnce("loaded");

    private void OnVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if ((bool)e.NewValue) StartAnimationOnce("visible");
    }

    private void StartAnimationOnce(string via)
    {
        if (_animationStarted) return;
        _animationStarted = true;

        bool reduced = SystemParameters.ClientAreaAnimation == false;
        Log.Info($"OOBE window start via {via} (suppress={UiMotion.Suppress}, reducedMotion={reduced}).");

        After(WatchdogMs, EnsureSettled); // last-resort settle so the window can never stay blank
        After(PresentCheckMs, EnsurePresented); // never let a healthy visual tree show as a black surface

        if (UiMotion.Suppress || reduced)
        {
            SnapToSettled();
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            if (!_finished) PlayDrag();
        }));
    }

    // Timing (ms).
    private const double DragMs = 900;        // cursor drags the box from corner to corner
    private const double SettleMs = 200;      // handles + label pop after the drag
    private const double HoldMs = 260;        // brief "captured" beat
    private const double MorphMs = 380;       // box → word crossfade
    private const double WatchdogMs = 5000;

    private void PlayDrag()
    {
        Log.Info("OOBE intro: drag phase.");

        // Reveal the selection scene, seed the drag start pose (both rects pre-sized, scale 0).
        _selectionScene.Opacity = 1;

        // Cursor starts at the box's top-left and fades in.
        _cursorTranslate.X = BoxLeft;
        _cursorTranslate.Y = BoxTop;
        StartAnim(_cursor, OpacityProperty, 0, 1, 160, 0, CubicOut());

        // The whole drag is GPU-composited: fill + border scale 0→1 anchored at the box's top-left
        // corner (the drag origin), and the cursor rides the bottom-right corner with the same ease
        // and duration — no per-frame Width/Height layout animation at all.
        StartAnim(_boxFill.RenderTransform, ScaleTransform.ScaleXProperty, 0, 1, DragMs, 0, CubicOut());
        StartAnim(_boxFill.RenderTransform, ScaleTransform.ScaleYProperty, 0, 1, DragMs, 0, CubicOut());
        StartAnim(_boxBorder.RenderTransform, ScaleTransform.ScaleXProperty, 0, 1, DragMs, 0, CubicOut());
        StartAnim(_boxBorder.RenderTransform, ScaleTransform.ScaleYProperty, 0, 1, DragMs, 0, CubicOut());
        StartAnim(_cursorTranslate, TranslateTransform.XProperty, BoxLeft, BoxLeft + BoxW, DragMs, 0, CubicOut());
        StartAnim(_cursorTranslate, TranslateTransform.YProperty, BoxTop, BoxTop + BoxH, DragMs, 0, CubicOut());

        // Size read-out: a low-frequency timer (not a render-frame handler) refreshes the label from the
        // scale's current ANIMATED value — a text resize is a small layout pass, so it runs ~10×/sec
        // instead of 60×/sec, and stops as soon as the drag settles.
        _labelTimer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(100) };
        _labelTimer.Tick += (_, _) =>
        {
            if (_finished) { _labelTimer.Stop(); return; }
            double k = ((ScaleTransform)_boxBorder.RenderTransform).ScaleX; // animated current value
            _sizeLabelText.Text = $"{(int)Math.Round(BoxW * k * 4)} × {(int)Math.Round(BoxH * k * 4)}";
        };
        _timers.Add(_labelTimer);
        _labelTimer.Start();

        After(DragMs + 60, OnDragComplete);
    }

    private DispatcherTimer? _labelTimer;

    private void OnDragComplete()
    {
        Log.Info("OOBE intro: box settled.");
        _labelTimer?.Stop(); // the box has settled; stop refreshing the read-out
        // Snap the cursor away, pop the four corner handles and the size label.
        StartAnim(_cursor, OpacityProperty, 1, 0, 140, 0, CubicOut());
        PlaceHandlesAndLabel();
        foreach (var h in _handles)
        {
            h.RenderTransformOrigin = new Point(0.5, 0.5);
            var scale = new ScaleTransform(0.3, 0.3);
            h.RenderTransform = scale;
            StartAnim(scale, ScaleTransform.ScaleXProperty, 0.3, 1, SettleMs, 0, BackOut());
            StartAnim(scale, ScaleTransform.ScaleYProperty, 0.3, 1, SettleMs, 0, BackOut());
            StartAnim(h, OpacityProperty, 0, 1, SettleMs, 0, CubicOut());
        }
        StartAnim(_sizeLabel, OpacityProperty, 0, 1, SettleMs, 0, CubicOut());

        After(SettleMs + HoldMs, MorphToWord);
    }

    private void PlaceHandlesAndLabel()
    {
        double l = BoxLeft, t = BoxTop, r = BoxLeft + BoxW, b = BoxTop + BoxH;
        (double X, double Y)[] pts = [(l, t), (r, t), (l, b), (r, b)];
        for (int i = 0; i < 4; i++)
        {
            Canvas.SetLeft(_handles[i], pts[i].X - 5);
            Canvas.SetTop(_handles[i], pts[i].Y - 5);
        }
        _sizeLabelText.Text = $"{(int)(BoxW * 4)} × {(int)(BoxH * 4)}";
        _sizeLabel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(_sizeLabel, BoxLeft);
        Canvas.SetTop(_sizeLabel, Math.Max(0, BoxTop - _sizeLabel.DesiredSize.Height - 6));
    }

    private void MorphToWord()
    {
        Log.Info("OOBE intro: morph to word.");
        var (wordTargetX, modernTargetX) = ComputeWordTargets();
        var wordTranslate = EnsureWordTranslate();

        // "ScreenShot" is born at the box centre, then glides to its final (centred-composition) slot
        // over the whole morph — one continuous positional move, not a pop-then-shift. The box fades
        // and shrinks under it as the word grows (ease-out: it is an exiting element).
        StartAnim(_selectionScene, OpacityProperty, 1, 0, MorphMs, 0, CubicOut());
        var sceneScale = new ScaleTransform(1, 1) { CenterX = StageW / 2, CenterY = StageH / 2 };
        _selectionScene.RenderTransform = sceneScale;
        StartAnim(sceneScale, ScaleTransform.ScaleXProperty, 1, 0.85, MorphMs, 0, CubicOut());
        StartAnim(sceneScale, ScaleTransform.ScaleYProperty, 1, 0.85, MorphMs, 0, CubicOut());

        const double glideMs = 460; // one smooth positional glide shared by both words
        // Rise: the words row starts translated down at the box's centre and glides up into its slot —
        // the words visibly rise out of the captured box while the chrome fades in beneath them.
        double rise = ComputeRiseSeed();
        _wordsRowTranslate.Y = rise;
        StartAnim(_wordsRowTranslate, TranslateTransform.YProperty, rise, 0, glideMs, 40, ExpOut(4));
        StartAnim(_word, OpacityProperty, 0, 1, MorphMs, 40, CubicOut());
        StartAnim(_wordScale, ScaleTransform.ScaleXProperty, 0.6, 1, glideMs, 40, ExpOut(4));
        StartAnim(_wordScale, ScaleTransform.ScaleYProperty, 0.6, 1, glideMs, 40, ExpOut(4));
        // Glide from box-centre (0) to the right slot.
        StartAnim(wordTranslate, TranslateTransform.XProperty, 0, wordTargetX, glideMs, 40, ExpOut(4));

        // "Modern" slides in from the left in the same beat, so the pair settles to centre together.
        _slideTranslate.X = modernTargetX - 22;
        StartAnim(_slideTranslate, TranslateTransform.XProperty, modernTargetX - 22, modernTargetX, glideMs, 140, ExpOut(4));
        StartAnim(_slideWord, OpacityProperty, 0, 1, 260, 160, CubicOut());
        StartAnim(_slideScale, ScaleTransform.ScaleXProperty, 0.965, 1, glideMs, 140, ExpOut(4));
        StartAnim(_slideScale, ScaleTransform.ScaleYProperty, 0.965, 1, glideMs, 140, ExpOut(4));

        After(glideMs + 160, RevealChrome);
    }

    private TranslateTransform EnsureWordTranslate()
    {
        // _word's RenderTransform is a ScaleTransform (the grow); wrap it in a group with a translate.
        if (_word.RenderTransform is TransformGroup g && g.Children.Count > 0 && g.Children[^1] is TranslateTransform existing)
            return existing;
        var translate = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(_wordScale);
        group.Children.Add(translate);
        _word.RenderTransform = group;
        return translate;
    }

    private void RevealChrome()
    {
        Log.Info("OOBE intro: chrome phase.");
        StartAnim(_chrome, OpacityProperty, 0, 1, 300, 0, CubicOut());
        int step = 0;
        foreach (var child in _introSection.Children)
        {
            if (child is FrameworkElement fe && fe.RenderTransform is TranslateTransform tt)
            {
                tt.Y = 12;
                StartAnim(tt, TranslateTransform.YProperty, 12, 0, 300, 40 * step, CubicOut());
                step++;
            }
        }
        After(220, () => _getStartedButton.Focus());
    }

    /// <summary>Side-by-side targets in the word-row's coordinate space: "Modern" + gap + "ScreenShot"
    /// centred. Widths measured with FormattedText so they are correct even before layout completes.</summary>
    private (double WordTargetX, double ModernTargetX) ComputeWordTargets()
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double wordW = MeasureTextWidth(_word, dpi);
        double slideW = MeasureTextWidth(_slideWord, dpi);
        const double gap = 12;
        // Both words are centred in the same overlapping cell; move each half the composition apart.
        double wordTargetX = (slideW + gap) / 2d;      // ScreenShot shifts right
        double modernTargetX = -((wordW + gap) / 2d);  // Modern shifts left
        return (wordTargetX, modernTargetX);
    }

    /// <summary>
    /// Vertical distance the words must travel to be born at the captured box: the box centre minus
    /// the words-row centre, both in host coordinates. The morph seeds the words row with this offset
    /// and glides it to 0, so the text rises out of the box into its compact slot. Falls back to the
    /// band geometry when transforms are not yet available.
    /// </summary>
    private double ComputeRiseSeed()
    {
        try
        {
            Point box = _selectionScene.TransformToVisual(_host).Transform(new Point(BoxLeft + BoxW / 2d, BoxTop + BoxH / 2d));
            Point row = _wordsRow.TransformToVisual(_host).Transform(new Point(0, _wordsRow.ActualHeight / 2d));
            if (!double.IsNaN(box.Y) && !double.IsNaN(row.Y)) return box.Y - row.Y;
        }
        catch { /* transforms not ready (pre-layout) */ }
        return (StageH - BoxH) / 2d + BoxH / 2d; // band fallback: box centre measured from the band top
    }

    private double MeasureTextWidth(TextBlock tb, double dpi)
    {
        var typeface = new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch);
        var ft = new FormattedText(tb.Text, System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight, typeface, tb.FontSize, Brushes.Black, dpi);
        return ft.WidthIncludingTrailingWhitespace;
    }

    /// <summary>Diagnostic / reduced-motion final frame: the two words side by side, no selection scene.</summary>
    private void SnapToSettled()
    {
        SetFinal(_selectionScene, OpacityProperty, 0);
        SetFinal(_word, OpacityProperty, 1);
        SetFinal(_wordScale, ScaleTransform.ScaleXProperty, 1);
        SetFinal(_wordScale, ScaleTransform.ScaleYProperty, 1);
        SetFinal(_slideWord, OpacityProperty, 1);
        SetFinal(_slideScale, ScaleTransform.ScaleXProperty, 1);
        SetFinal(_slideScale, ScaleTransform.ScaleYProperty, 1);
        SetFinal(_wordsRowTranslate, TranslateTransform.YProperty, 0); // natural slot (the rise has glided away)
        _stage.UpdateLayout();
        var (wordTargetX, modernTargetX) = ComputeWordTargets();
        SetFinal(EnsureWordTranslate(), TranslateTransform.XProperty, wordTargetX);
        SetFinal(_slideTranslate, TranslateTransform.XProperty, modernTargetX);
        SetFinal(_chrome, OpacityProperty, 1);
        foreach (var child in _introSection.Children)
            if (child is FrameworkElement fe && fe.RenderTransform is TranslateTransform tt) SetFinal(tt, TranslateTransform.YProperty, 0);

        // Render-probe hook (MSS_RENDER_OOBE_CONFIG=1 with --render-oobe): show the preferences panel
        // in the settled frame so its layout is tree-dump verifiable — the live flow only reveals it
        // after "Get started", which the offscreen render path can never click.
        if (Environment.GetEnvironmentVariable("MSS_RENDER_OOBE_CONFIG") == "1")
        {
            _introSection.Visibility = Visibility.Collapsed;
            _configPanel.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Watchdog: force the settled pose if the chain stalled; idempotent once settled.</summary>
    private void EnsureSettled()
    {
        if (_finished) return;
        if (_word.Opacity > 0.99 && _chrome.Opacity > 0.99 && _slideWord.Opacity > 0.99) return;
        Log.Warn($"OOBE intro stalled (word={_word.Opacity:F2} chrome={_chrome.Opacity:F2} modern={_slideWord.Opacity:F2}); forcing the settled pose.");
        SnapToSettled();
    }

    /// <summary>Final-value commit for the settle path: a stalled-but-attached HoldEnd clock keeps
    /// overriding plain SetValue, so the animation must be detached before the value is set.</summary>
    private static void SetFinal(System.Windows.Media.Animation.IAnimatable o, DependencyProperty p, double v)
    {
        o.BeginAnimation(p, null);
        ((DependencyObject)o).SetValue(p, v);
    }

    // ---- presentation self-check (the "black welcome window" guard) ----
    // The visual tree can be fully revealed — every opacity/geometry probe green — while DWM still
    // presents a stale, fully black surface (observed live: a 720x560 welcome window whose title bar
    // sampled RGB(0,0,0) instead of #202020; it only repainted when an external WM_PRINT forced one).
    // The intro has a blue gradient word, white text and accent icons over a #202020 background, so a
    // healthy presentation samples ~0% near-black. Reading the window's own on-screen pixels detects
    // the stall, and a presentation kick (frame change → 1px size round-trip → hide/show) forces the
    // surface to be reallocated and re-presented.

    private const double PresentCheckMs = 3600; // after the natural chain end (~2.6s), before the 5s watchdog
    private const double BlackPresentThreshold = 0.985;
    private bool _presentFixed;
    private int _presentAttempts;

    private void EnsurePresented()
    {
        if (_finished || _presentFixed || _presentAttempts >= 3) return;
        if (Opacity < 0.99) return; // mid fade-in/out: the sample would read the desktop behind
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd) || NativeMethods.IsIconic(hwnd)) return;
        // Only trust the sample while nothing covers the window; an occluded sample reads the cover.
        if (NativeMethods.GetForegroundWindow() != hwnd) return;

        if (!TrySampleOwnScreenPixels(hwnd, out double black)) return;
        if (black < BlackPresentThreshold)
        {
            if (_presentAttempts > 0) Log.Info($"OOBE presentation recovered after {_presentAttempts} kick(s) (black {black:P1}).");
            _presentFixed = true;
            return;
        }
        _presentAttempts++;
        Log.Warn($"OOBE window presents black on screen (black {black:P1}, revealed={DiagnosticFullyRevealed}, attempt {_presentAttempts}); kicking the presentation pipeline.");
        KickPresentation(hwnd);
        After(700, EnsurePresented);
    }

    /// <summary>Escalating repaint kicks: frame change, a 1-DIP size round-trip (forces a new
    /// redirection surface), then a hide/show cycle as the last resort.</summary>
    private void KickPresentation(IntPtr hwnd)
    {
        try
        {
            switch (_presentAttempts)
            {
                case 1:
                    NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER |
                        NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_FRAMECHANGED);
                    InvalidateVisual();
                    break;
                case 2:
                    double w = Width, h = Height;
                    Width = w + 1; Height = h + 1;
                    Width = w; Height = h;
                    InvalidateVisual();
                    break;
                default:
                    Hide();
                    Show();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"OOBE presentation kick failed: {ex.Message}");
        }
    }

    /// <summary>Samples the window's own on-screen pixels (GDI BitBlt of its screen rect — the same
    /// read path as a real capture) and returns the near-black fraction. False when unreadable.
    /// Internal so the visible probe (MSS_PROBE_VISIBLE) can run the same end-to-end check.</summary>
    internal bool TrySampleOwnScreenPixels(IntPtr hwnd, out double blackFraction)
    {
        blackFraction = 1;
        try
        {
            if (!NativeMethods.GetWindowRect(hwnd, out var r)) return false;
            int w = r.Right - r.Left, h = r.Bottom - r.Top;
            if (w <= 0 || h <= 0) return false;
            using var dib = new DibSection(w, h);
            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return false;
            try
            {
                if (!NativeMethods.BitBlt(dib.Dc, 0, 0, w, h, screenDc, r.Left, r.Top,
                        NativeMethods.SRCCOPY | NativeMethods.CAPTUREBLT)) return false;
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
            var pixelBuffer = dib.ToPixelBuffer(forceOpaque: false);
            blackFraction = BlackFrame.NearBlackFraction(pixelBuffer);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"OOBE presentation self-sample failed: {ex.Message}");
            return false;
        }
    }

    // ---- diagnostics (--probe-oobe) ----

    /// <summary>Live-path assertion: the words and chrome reached full opacity through the real
    /// animation pipeline (checked before the watchdog can mask a stall).</summary>
    internal bool DiagnosticFullyRevealed =>
        _word.Opacity > 0.99 && _chrome.Opacity > 0.99 && _slideWord.Opacity > 0.99;

    /// <summary>Live-path geometry assertion: "Modern"'s right edge stays left of "ScreenShot", the
    /// composed wordmark is centred in the host, and nothing clips at the host edges. A value-only
    /// no-overlap check once let a live-path regression ship where the whole intro sat far off-centre
    /// and clipped at the window edge — opacities were 1.0 and every probe was green.</summary>
    internal bool DiagnosticLayoutValid
    {
        get
        {
            try
            {
                Point modern = _slideWord.TransformToVisual(_host).Transform(new Point(0, 0));
                Point word = _word.TransformToVisual(_host).Transform(new Point(0, 0));
                bool noOverlap = modern.X + _slideWord.ActualWidth <= word.X + 2;
                double left = Math.Min(modern.X, word.X);
                double right = Math.Max(modern.X + _slideWord.ActualWidth, word.X + _word.ActualWidth);
                double centre = (left + right) / 2d;
                bool centred = Math.Abs(centre - _host.ActualWidth / 2d) <= 8;
                bool contained = left >= -1 && right <= _host.ActualWidth + 1;
                return noOverlap && centred && contained;
            }
            catch { return false; }
        }
    }

    /// <summary>Human-readable word geometry for probe failure logs (host-relative DIPs).</summary>
    internal string DiagnosticLayoutDescription
    {
        get
        {
            try
            {
                Point modern = _slideWord.TransformToVisual(_host).Transform(new Point(0, 0));
                Point word = _word.TransformToVisual(_host).Transform(new Point(0, 0));
                double left = Math.Min(modern.X, word.X);
                double right = Math.Max(modern.X + _slideWord.ActualWidth, word.X + _word.ActualWidth);
                return $"modern=[{modern.X:F0}..{modern.X + _slideWord.ActualWidth:F0}] word=[{word.X:F0}..{word.X + _word.ActualWidth:F0}] " +
                       $"compositionCentre={(left + right) / 2d:F0} hostCentre={_host.ActualWidth / 2d:F0} hostW={_host.ActualWidth:F0}";
            }
            catch (Exception ex)
            {
                return $"unavailable ({ex.Message})";
            }
        }
    }

    /// <summary>Live-path assertion for the drag phase (checked at ~1s, when the box is fully grown):
    /// the selection box's visual centre sits at the host centre. Catches a misplaced box — its canvas
    /// slot and the cursor's animated endpoints are set independently, so a missing Canvas.Set* makes
    /// them diverge while every opacity check still passes.</summary>
    internal bool DiagnosticBoxCentered
    {
        get
        {
            try
            {
                Point c = _boxBorder.TransformToVisual(_host).Transform(new Point(BoxW / 2d, BoxH / 2d));
                return Math.Abs(c.X - _host.ActualWidth / 2d) < 2 && Math.Abs(c.Y - _host.ActualHeight / 2d) < 2;
            }
            catch { return false; }
        }
    }

    private bool _completedFired;

    private void FireCompleted()
    {
        if (_completedFired) return;
        _completedFired = true;
        try { _onCompleted(); }
        catch (Exception ex) { Log.Warn($"OOBE completion callback failed: {ex.Message}"); }
    }

    /// <summary>Skip / Get started / Esc: soft exit fade, then close.</summary>
    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        StopTimers();
        PersistChoices(); // keep choices made before a mid-config dismissal
        FireCompleted();
        UiMotion.FadeOut(this, 140, Close);
    }

    /// <summary>The title-bar close button calls Window.Close() directly; record completion there too.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _finished = true;
        StopTimers();
        PersistChoices();
        FireCompleted();
    }
}
