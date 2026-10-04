using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ModernScreenShot.App.Controls;
using ModernScreenShot.App.Localization;
using ModernScreenShot.App.Services;
using ModernScreenShot.App.Shell;
using ModernScreenShot.Core.Ocr;
using ModernScreenShot.Core.Settings;
using ModernScreenShot.Core.Translation;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Settings;

/// <summary>
/// Settings window with a left navigation rail and card-based content pages. Control values are
/// written back to the settings store when the window closes (single save point); language / theme /
/// autostart take effect immediately on change. All labels are DynamicResource strings so a language
/// switch updates the open window live; code-built parts (hotkey rows, about texts) refresh via
/// <see cref="LocalizationService.LanguageChanged"/>.
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsStore _settings;
    private readonly LocalizationService _localization;
    private readonly HotkeyService? _hotkeys;
    private readonly Action<string>? _notifyConflict;
    private readonly Action? _onRestartAsAdmin;
    private readonly Action? _onPreviewOobe;
    private readonly Dictionary<string, HotkeyBinding> _editedBindings;
    /// <summary>Clicks accumulated on the About nav item toward unlocking the hidden Debug category.</summary>
    private int _debugUnlockClicks;
    private const int DebugUnlockClickCount = 5;
    private readonly Dictionary<string, TextBlock> _conflictMarks = [];
    private readonly Dictionary<string, TextBlock> _actionLabels = [];
    private readonly Dictionary<string, ContentControl> _chipHosts = [];
    private bool _loading = true;
    private bool _wasShown;
    private bool _syncingLanguage;
    private bool _revertingAutostart;

    public SettingsWindow(SettingsStore settings, LocalizationService localization,
        HotkeyService? hotkeys, Action<string>? notifyConflict, Action? onRestartAsAdmin,
        Action? onPreviewOobe = null, TranslationService? translation = null)
    {
        InitializeComponent();
        AppTitleBar.Attach(this); // custom title bar (Controls/AppTitleBar) replaces the OS caption
        _settings = settings;
        _localization = localization;
        _hotkeys = hotkeys;
        _notifyConflict = notifyConflict;
        _onRestartAsAdmin = onRestartAsAdmin;
        _onPreviewOobe = onPreviewOobe;
        _translation = translation;
        _editedBindings = settings.Current.Hotkeys.Bindings.ToDictionary(kv => kv.Key, kv => kv.Value.Clone());

        LoadGeneralTab();
        LoadHotkeyTab();
        LoadCaptureTab();
        LoadOutputTab();
        LoadEffectsTab();
        LoadOcrTab();
        LoadTranslateTab();
        RefreshAboutTexts();
        _loading = false;

        _localization.LanguageChanged += OnLanguageChanged;
        Loaded += (_, _) => _wasShown = true;
        Closed += OnClosed;
    }

    // ---- navigation ----

    /// <summary>Switches the visible content page to match the selected left-rail item. Kept as simple
    /// visibility toggling (rather than frame navigation) so the diagnostic --render-settings snapshot
    /// path stays deterministic.</summary>
    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Pages is null) return; // fires once during InitializeComponent before the tree is ready
        var pages = new[] { PageGeneral, PageHotkeys, PageCapture, PageOutput, PageEffects, PageAbout, PageDebug };
        int index = Math.Clamp(NavList.SelectedIndex, 0, pages.Length - 1);
        for (int i = 0; i < pages.Length; i++)
            pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        // Entrance (motion audit #4, upgraded after user feedback — a bare 180ms fade was
        // imperceptible on the dark panel): slide (10px up) + fade the newly revealed page over
        // 220ms; the exit stays instant. Only a live user click animates: the
        // InitializeComponent-time fire bails at the Pages guard above, IsLoaded stays false until
        // the window is shown, and the diagnostic --render-settings path (SelectPage) runs under
        // UiMotion.Suppress, which snaps to the final value. FadeSlideIn forces opacity from 0 on
        // every call so repeated switches replay; SnapshotAndReplace makes rapid nav clicks
        // freely interruptible and no branch can strand the page at 0.
        if (IsLoaded)
        {
            UiMotion.FadeSlideIn(pages[index], 0, 10, 220);
            // Rail feedback for the switch itself: the selected item's accent bar grows up from
            // its bottom edge. Suppress/reduced-motion snap it to full height (GrowY).
            if (NavList.ItemContainerGenerator.ContainerFromIndex(index) is ListBoxItem item &&
                item.Template.FindName("Bar", item) is Border bar)
                UiMotion.GrowY(bar, 160);
        }
    }

    /// <summary>Selects a content page by index (used by the diagnostic renderer, replacing the old
    /// TabControl selection).</summary>
    public void SelectPage(int index)
    {
        if (index >= 0 && index < NavList.Items.Count) NavList.SelectedIndex = index;
    }

    // ---- tab loading ----

    private void LoadGeneralTab()
    {
        LanguageBox.SelectedIndex = _settings.Current.Language switch
        {
            LocalizationService.Chinese => 1,
            LocalizationService.English => 2,
            _ => 0,
        };
        ThemeBox.SelectedIndex = _settings.Current.Theme switch
        {
            "Light" => 1,
            "Dark" => 2,
            _ => 0,
        };
        StartWithWindowsBox.IsChecked = _settings.Current.StartWithWindows;
    }

    private void LoadHotkeyTab()
    {
        // The hotkey page's rows are standalone card rows, exactly like every other page's cards:
        // a ui:CardControl with the same margin/padding the XAML "Card" style gives them, the
        // action name as the header and the chips + pencil control on the right. The wrapper
        // Border around HotkeyPanel in the XAML predates this layout and is reduced to a plain
        // transparent container here, so the rows don't draw card chrome inside card chrome.
        if (HotkeyPanel.Parent is Border wrapper)
        {
            wrapper.Background = Brushes.Transparent;
            wrapper.BorderThickness = new Thickness(0);
            wrapper.Padding = new Thickness(0);
        }

        foreach (var action in HotkeyActions.All)
        {
            // Header: the action name (RowTitle, like all other card headers) with a small
            // row-level conflict glyph next to it.
            var label = new TextBlock { Text = L.Get($"Mode.{action}") };
            if (TryFindResource("RowTitle") is Style titleStyle) label.Style = titleStyle;
            var mark = new TextBlock
            {
                Text = "\uE7BA", // warning glyph (row-level exclamation mark)
                FontFamily = HotkeyChips.GlyphFont,
                FontSize = 12,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
                ToolTip = L.Get("Settings.Conflict"),
            };
            mark.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
            var titleLine = new StackPanel { Orientation = Orientation.Horizontal };
            titleLine.Children.Add(label);
            titleLine.Children.Add(mark);

            var chipsHost = new ContentControl { VerticalAlignment = VerticalAlignment.Center };
            var editButton = MakeEditButton(chipsHost);
            editButton.Click += (_, _) => OpenHotkeyDialog(action);

            var row = new Wpf.Ui.Controls.CardControl
            {
                Margin = new Thickness(0, 0, 0, 8), // "Card" style values in the XAML
                Padding = new Thickness(16, 12, 16, 12),
                Header = titleLine,
                Content = editButton,
            };
            HotkeyPanel.Children.Add(row);

            _actionLabels[action] = label;
            _conflictMarks[action] = mark;
            _chipHosts[action] = chipsHost;
            RefreshHotkeyRow(action);
        }
        RefreshConflicts();
    }

    /// <summary>Subtle button (transparent until hover — the WPF-UI Transparent appearance hovers
    /// to SubtleFillColorSecondary) hosting the chips display or the unbound placeholder plus the
    /// pencil glyph in the secondary text color. Clicking anywhere in it opens the edit dialog.
    /// A plain WPF-UI Button — not a subclass — keeps the implicit themed style working.</summary>
    private static Wpf.Ui.Controls.Button MakeEditButton(ContentControl chipsHost)
    {
        var pencil = new TextBlock
        {
            Text = "\uE70F", // Edit (pencil)
            FontFamily = HotkeyChips.GlyphFont,
            FontSize = 12,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        pencil.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(chipsHost);
        content.Children.Add(pencil);
        return new Wpf.Ui.Controls.Button
        {
            Content = content,
            Appearance = Wpf.Ui.Controls.ControlAppearance.Transparent,
            Cursor = Cursors.Hand,
            Padding = new Thickness(8, 3, 8, 3),
            // Matches the standard row-control height (ComboBox / NumberBox), so the hotkey card
            // rows come out the same height as every other card on the page.
            MinHeight = 35.5,
            HorizontalContentAlignment = HorizontalAlignment.Right,
        };
    }

    /// <summary>Rebuilds a row's chips (also the language-change path: the placeholder is localized).</summary>
    private void RefreshHotkeyRow(string action)
    {
        if (!_chipHosts.TryGetValue(action, out var host)) return;
        var binding = _editedBindings.GetValueOrDefault(action, new HotkeyBinding());
        host.Content = binding.IsEmpty
            ? HotkeyChips.Placeholder()
            : HotkeyChips.Build(binding, HotkeyChips.ChipSize.Inline);
    }

    /// <summary>Opens the modal capture dialog for one action. Only reachable by an actual
    /// click on a row's edit button — the diagnostic --render-settings / MSS_DUMP_TREE paths never
    /// construct the dialog. On Save the binding joins _editedBindings (persisted with the rest when
    /// the window closes); on any other exit the previous binding is kept untouched.</summary>
    private void OpenHotkeyDialog(string action)
    {
        var dialog = new HotkeyEditDialog(action, _editedBindings, _hotkeys, _localization);
        if (!dialog.ShowAndEdit(this)) return;
        _editedBindings[action] = dialog.Result;
        RefreshHotkeyRow(action);
        RefreshConflicts();
    }

    private void LoadCaptureTab()
    {
        var capture = _settings.Current.Capture;
        DelayBox.Value = capture.DelaySeconds;
        AutoDetectBox.IsChecked = capture.AutoElementDetection;
        ShowMagnifierBox.IsChecked = capture.ShowMagnifier;
        CaptureCursorBox.IsChecked = capture.CaptureCursor;
        TransparentCornersBox.IsChecked = capture.WindowTransparentCorners;
        MacShadowBox.IsChecked = capture.MacStyleWindowShadow;
        HistoryMaxBox.Value = _settings.Current.HistoryMaxCount;
        UpdateMacShadowHint();

        // Capture priority is only meaningful when elevated. Elevated → show the toggle; normal → show
        // the "run as admin" button, since the priority boost can't work without elevation.
        bool elevated = ElevationService.IsElevated;
        CapturePriorityBox.IsChecked = capture.CapturePriority;
        CapturePriorityBox.Visibility = elevated ? Visibility.Visible : Visibility.Collapsed;
        RunAsAdminButton.Visibility = elevated ? Visibility.Collapsed : Visibility.Visible;
        CapturePriorityHint.Text = L.Get(elevated ? "Settings.CapturePriorityHint" : "Settings.CapturePriorityHintNormal");
    }

    private void OnMacShadowChanged(object sender, RoutedEventArgs e)
    {
        UpdateMacShadowHint();
        if (_loading) return;
        // Apply immediately: the window otherwise persists only on close, and users toggling this
        // expect the very next capture to include the shadow.
        _settings.Current.Capture.MacStyleWindowShadow = MacShadowBox.IsChecked == true;
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }
    }

    private void OnCapturePriorityChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // Apply immediately (mirrors OnMacShadowChanged) so the next capture picks it up without
        // waiting for the window to close.
        _settings.Current.Capture.CapturePriority = CapturePriorityBox.IsChecked == true;
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }
    }

    private void OnRunAsAdminClick(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            L.Get("Settings.RunAsAdminConfirm"), L.Get("Settings.CapturePriority"),
            MessageBoxButton.OKCancel, MessageBoxImage.Question, MessageBoxResult.OK);
        if (answer != MessageBoxResult.OK) return;
        // Persist any pending edits before the process is replaced by the elevated instance.
        try { WriteBack(); _settings.Save(); }
        catch (Exception ex) { Log.Error("Saving settings before elevation failed", ex); }
        _onRestartAsAdmin?.Invoke();
    }

    /// <summary>Warns that a JPG export drops the transparent mac-style shadow (it fills the surround
    /// with white). Shown only when the shadow is enabled and the chosen format has no alpha. The
    /// Format control lives on another tab, so this is re-evaluated from both tabs' load paths.</summary>
    private void UpdateMacShadowHint()
    {
        if (MacShadowHint is null || MacShadowBox is null || FormatBox is null) return;
        bool shadowOn = MacShadowBox.IsChecked == true;
        bool formatKeepsAlpha = (ImageFormat)FormatBox.SelectedIndex is ImageFormat.Png or ImageFormat.WebP;
        MacShadowHint.Visibility = shadowOn && !formatKeepsAlpha ? Visibility.Visible : Visibility.Collapsed;
    }

    private void LoadOutputTab()
    {
        var output = _settings.Current.Output;
        SaveDirBox.Text = output.SaveDirectory;
        // An empty folder means the default; say which one instead of leaving the box silent.
        SaveDirHint.Text = string.Format(L.Get("Settings.SaveDirDefault"), AppPaths.DefaultSaveDir);
        SaveDirHint.Visibility = string.IsNullOrWhiteSpace(output.SaveDirectory) ? Visibility.Visible : Visibility.Collapsed;
        SaveDirBox.TextChanged += (_, _) =>
            SaveDirHint.Visibility = string.IsNullOrWhiteSpace(SaveDirBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        FormatBox.SelectedIndex = (int)output.Format;
        JpgQualitySlider.Value = output.JpgQuality;
        WebPQualitySlider.Value = output.WebPQuality;
        JpgQualityValue.Text = output.JpgQuality.ToString(CultureInfo.InvariantCulture);
        WebPQualityValue.Text = output.WebPQuality.ToString(CultureInfo.InvariantCulture);
        UpdateQualityRowVisibility(); // show only the quality slider that applies to the chosen format
        TemplateBox.Text = output.FileNameTemplate;
        AutoSaveBox.IsChecked = output.AutoSave;
        AutoCopyBox.IsChecked = output.AutoCopy;
        AfterRegionBox.SelectedIndex = (int)output.AfterRegionCapture;
        AfterOtherBox.SelectedIndex = (int)output.AfterOtherCapture;
    }

    private void LoadEffectsTab() => ApplyEffectsBox.IsChecked = _settings.Current.Output.ApplyEffectsOnExport;

    // ---- OCR ----

    private bool _ocrDownloading;

    private void LoadOcrTab()
    {
        OcrAccuracyBox.SelectedIndex = _settings.Current.Ocr.Accuracy == OcrAccuracy.Accurate ? 1 : 0;
        OcrAutoCopyBox.IsChecked = _settings.Current.Ocr.CopyAfterRecognize;
        RefreshOcrModelStatus();
    }

    /// <summary>Accurate-model row: installed → status text only; missing → warning color plus the
    /// download button. Both strings are localized, so a live language switch re-runs this.</summary>
    private void RefreshOcrModelStatus()
    {
        bool installed = OcrModelDownloader.AllModelsPresent();
        OcrModelStatus.Text = L.Get(installed ? "Settings.Ocr.Installed" : "Settings.Ocr.NotInstalled");
        OcrModelStatus.SetResourceReference(TextBlock.ForegroundProperty,
            installed ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush");
        OcrDownloadButton.Visibility = installed ? Visibility.Collapsed : Visibility.Visible;
        OcrDownloadButton.IsEnabled = !installed && !_ocrDownloading;
    }

    private void OnOcrAccuracyChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        // Picking "accurate" while its models are absent is legal (recognition falls back to the
        // fast tier), but the status row should say why nothing changed yet.
        if (OcrAccuracyBox.SelectedIndex == 1 && !OcrModelDownloader.AllModelsPresent())
        {
            OcrModelStatus.Text = L.Get("Settings.Ocr.NotInstalled");
            OcrModelStatus.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCautionBrush");
        }
    }

    private async void OnDownloadOcrModels(object sender, RoutedEventArgs e)
    {
        if (_ocrDownloading) return;
        _ocrDownloading = true;
        OcrDownloadButton.IsEnabled = false;
        var progress = new Progress<double>(fraction =>
            OcrDownloadButton.Content = L.Get("Settings.Ocr.Downloading", fraction * 100));
        bool completed = false;
        try
        {
            await OcrModelDownloader.DownloadAsync(progress);
            completed = true;
            Log.Info("Accurate OCR models downloaded.");
        }
        catch (Exception ex)
        {
            Log.Error("Downloading the accurate OCR models failed", ex);
            OcrModelStatus.Text = L.Get("Settings.Ocr.DownloadFailed", ex.Message);
            OcrModelStatus.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        }
        finally
        {
            _ocrDownloading = false;
            OcrDownloadButton.Content = L.Get("Settings.Ocr.Download");
            if (completed) RefreshOcrModelStatus();
            else OcrDownloadButton.IsEnabled = true; // a retry must be possible right away
        }
    }

    // ---- Translation (Hy-MT2 via llama.cpp) ----

    private readonly TranslationService? _translation;
    private bool _translateDownloading;
    private bool _translatePopulating;

    /// <summary>The target language currently selected in the combo box.</summary>
    private TranslationLanguage? SelectedLanguage =>
        (TranslateLanguageBox.SelectedItem as ComboBoxItem)?.Tag as TranslationLanguage;

    private void LoadTranslateTab()
    {
        TranslateAutoDetectBox.IsChecked = _settings.Current.Translation.AutoDetectSource;
        TranslateAutoCopyBox.IsChecked = _settings.Current.Translation.CopyAfterTranslate;
        PopulateTranslateLanguages();
        RefreshTranslateStatus();
    }

    /// <summary>
    /// Fills the language list. Unlike the previous engine there is nothing to fetch: the model is
    /// multilingual, so every language is available as soon as the model is present.
    /// </summary>
    private void PopulateTranslateLanguages()
    {
        _translatePopulating = true;
        try
        {
            bool chinese = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
                .Equals("zh", StringComparison.OrdinalIgnoreCase);
            TranslateLanguageBox.Items.Clear();
            foreach (var language in TranslationLanguages.All)
            {
                TranslateLanguageBox.Items.Add(new ComboBoxItem
                {
                    Content = $"{language.DisplayName(chinese)}  ({language.Code})",
                    Tag = language,
                });
            }

            int index = TranslationLanguages.All
                .Select((l, i) => (l, i))
                .FirstOrDefault(x => string.Equals(x.l.Code, _settings.Current.Translation.ToCode, StringComparison.OrdinalIgnoreCase))
                .i;
            TranslateLanguageBox.SelectedIndex = index;
        }
        finally
        {
            _translatePopulating = false;
        }
    }

    /// <summary>Model row + engine row: both carry localized text, so a live language switch re-runs
    /// this (see the LanguageChanged handler).</summary>
    private void RefreshTranslateStatus()
    {
        bool runtime = TranslationEngine.IsRuntimeInstalled;
        TranslateEngineStatus.Text = runtime
            ? L.Get("Settings.Translate.EngineVersion", TranslationEngine.ReadRuntimeVersion())
            : L.Get("Settings.Translate.EngineMissing");
        TranslateEngineStatus.SetResourceReference(TextBlock.ForegroundProperty,
            runtime ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush");

        bool model = TranslationModelDownloader.IsModelInstalled();
        long have = TranslationModelDownloader.InstalledBytes();
        TranslateModelStatus.Text = model
            ? L.Get("Settings.Translate.Installed", TranslationModelDownloader.ModelBytes / (1024 * 1024))
            : have > 0
                ? L.Get("Settings.Translate.Partial", have * 100.0 / TranslationModelDownloader.ModelBytes)
                : L.Get("Settings.Translate.NotInstalled");
        TranslateModelStatus.SetResourceReference(TextBlock.ForegroundProperty,
            model ? "TextFillColorSecondaryBrush" : "SystemFillColorCautionBrush");

        TranslateDownloadButton.Visibility = model ? Visibility.Collapsed : Visibility.Visible;
        TranslateDownloadButton.IsEnabled = runtime && !model && !_translateDownloading;
        TranslateRemoveButton.Visibility = model || have > 0 ? Visibility.Visible : Visibility.Collapsed;
        TranslateRemoveButton.IsEnabled = (model || have > 0) && !_translateDownloading;
        TranslateLanguageBox.IsEnabled = !_translateDownloading;
    }

    private void OnTranslateLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _translatePopulating) return;
        RefreshTranslateStatus();
    }

    private async void OnDownloadTranslateModel(object sender, RoutedEventArgs e)
    {
        if (_translateDownloading) return;
        _translateDownloading = true;
        TranslateDownloadButton.IsEnabled = false;
        TranslateRemoveButton.IsEnabled = false;
        var progress = new Progress<double>(fraction =>
            TranslateDownloadButton.Content = L.Get("Settings.Translate.Downloading", fraction * 100));
        bool completed = false;
        try
        {
            await TranslationModelDownloader.DownloadAsync(progress);
            completed = true;
            Log.Info("Translation model downloaded.");
            // The engine had no model to load before; drop it so the new file is picked up.
            if (_translation is not null) await _translation.RestartAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Downloading the translation model failed", ex);
            TranslateModelStatus.Text = L.Get("Settings.Translate.DownloadFailed", ex.Message);
            TranslateModelStatus.SetResourceReference(TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        }
        finally
        {
            _translateDownloading = false;
            TranslateDownloadButton.Content = L.Get("Settings.Translate.Download");
            RefreshTranslateStatus();
            if (completed) TranslateModelStatus.Text = L.Get("Settings.Translate.Downloaded");
        }
    }

    private void OnRemoveTranslateModel(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(this,
            L.Get("Settings.Translate.RemoveConfirm"),
            L.Get("Settings.Translate.Remove"), MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;

        if (TranslationModelDownloader.Uninstall())
        {
            Log.Info("Translation model removed.");
            if (_translation is not null) _ = _translation.RestartAsync();
        }
        RefreshTranslateStatus();
    }

    /// <summary>Diagnostic hook for --render-settings: scrolls the visible page to the bottom so the
    /// snapshot can show sections that sit below the fold.</summary>
    public void DiagnosticScrollToEnd()
    {
        // SelectPage only toggles visibility, so the page must be laid out before a ScrollViewer
        // reports any scrollable height.
        UpdateLayout();
        var viewer = FindDeepestScrollViewer(this);
        viewer?.ScrollToEnd();
        UpdateLayout();
    }

    /// <summary>The page viewer, not the (short, non-scrolling) navigation rail: of all the
    /// scrollable viewers in the window, the one with the most overflow is the content page.</summary>
    private static ScrollViewer? FindDeepestScrollViewer(DependencyObject root)
    {
        ScrollViewer? best = null;
        int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer && viewer.ScrollableHeight > (best?.ScrollableHeight ?? 0)) best = viewer;
            var nested = FindDeepestScrollViewer(child);
            if (nested is not null && nested.ScrollableHeight > (best?.ScrollableHeight ?? 0)) best = nested;
        }
        return best;
    }

    private void RefreshAboutTexts()
    {
        var version = typeof(App).Assembly.GetName().Version;
        VersionText.Text = L.Get("Settings.Version", version is null ? "?" : version.ToString(3));
        SettingsPathText.Text = L.Get("Settings.SettingsPath", _settings.FilePath);
    }

    // ---- conflict marking ----

    private void RefreshConflicts()
    {
        // Internal duplicates: the same modifier+key assigned to more than one action.
        var counts = new Dictionary<(int Modifiers, int VirtualKey), int>();
        foreach (var binding in _editedBindings.Values)
        {
            if (binding.IsEmpty) continue;
            var key = (binding.Modifiers, binding.VirtualKey);
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }
        var external = _hotkeys?.FailedActions.ToHashSet() ?? [];
        foreach (var action in HotkeyActions.All)
        {
            var binding = _editedBindings.GetValueOrDefault(action, new HotkeyBinding());
            bool duplicated = !binding.IsEmpty && counts.GetValueOrDefault((binding.Modifiers, binding.VirtualKey)) > 1;
            bool occupied = external.Contains(action);
            _conflictMarks[action].Visibility = duplicated || occupied ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    // ---- event handlers ----

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || _syncingLanguage) return;
        string language = LanguageBox.SelectedIndex switch
        {
            1 => LocalizationService.Chinese,
            2 => LocalizationService.English,
            _ => "",
        };
        _settings.Current.Language = language;
        try
        {
            _localization.Apply(language); // raises LanguageChanged, which refreshes this window
        }
        catch (Exception ex)
        {
            Log.Error("Applying the selected language failed", ex);
        }
    }

    private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        string theme = ThemeBox.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };
        _settings.Current.Theme = theme;
        App.ApplyTheme(theme);
    }

    private void OnStartWithWindowsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || _revertingAutostart) return;
        bool enable = StartWithWindowsBox.IsChecked == true;
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
            _settings.Current.StartWithWindows = enable;
            Log.Info($"Start with Windows {(enable ? "enabled" : "disabled")}.");
        }
        catch (Exception ex)
        {
            Log.Error("Toggling start-with-Windows failed", ex);
            _revertingAutostart = true;
            StartWithWindowsBox.IsChecked = _settings.Current.StartWithWindows;
            _revertingAutostart = false;
        }
    }

    private void OnQualityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (JpgQualityValue is null || WebPQualityValue is null) return; // still building the tree
        JpgQualityValue.Text = ((int)JpgQualitySlider.Value).ToString(CultureInfo.InvariantCulture);
        WebPQualityValue.Text = ((int)WebPQualitySlider.Value).ToString(CultureInfo.InvariantCulture);
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateQualityRowVisibility();
        UpdateMacShadowHint(); // the shadow-format warning depends on the chosen format
    }

    /// <summary>Shows only the quality control the chosen format uses. PNG is lossless and has no
    /// quality knob, so both sliders hide; JPG and WebP each show their own.</summary>
    private void UpdateQualityRowVisibility()
    {
        // May fire from FormatBox.SelectionChanged before the whole tree is built.
        if (JpgQualityCard is null || WebPQualityCard is null) return;
        var format = (ImageFormat)FormatBox.SelectedIndex;
        JpgQualityCard.Visibility = format == ImageFormat.Jpg ? Visibility.Visible : Visibility.Collapsed;
        WebPQualityCard.Visibility = format == ImageFormat.WebP ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnBrowseSaveDir(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = L.Get("Settings.SaveDir") };
            string current = SaveDirBox.Text.Trim();
            if (Directory.Exists(current)) dialog.FolderName = current;
            if (dialog.ShowDialog(this) == true) SaveDirBox.Text = dialog.FolderName;
        }
        catch (Exception ex)
        {
            Log.Error("Browsing for the save folder failed", ex);
        }
    }

    private void OnOpenLog(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{AppPaths.LogDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error("Opening the log folder failed", ex);
        }
    }

    /// <summary>Hidden "easter egg": five clicks on the About nav item reveal the Debug category.
    /// Runs on PreviewMouseLeftButtonDown so it counts even though clicking also selects the item;
    /// once unlocked the counter stops and the item stays revealed for the window's lifetime.</summary>
    private void OnAboutNavClicked(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (NavDebugItem.Visibility == Visibility.Visible) return; // already unlocked
        if (++_debugUnlockClicks < DebugUnlockClickCount) return;
        NavDebugItem.Visibility = Visibility.Visible;
        _notifyConflict?.Invoke(L.Get("Settings.Debug.Unlocked"));
        Log.Info("Hidden debug category unlocked in settings.");
    }

    /// <summary>Debug category: opens the OOBE welcome window to preview its entrance animation,
    /// without touching the OobeCompleted flag.</summary>
    private void OnPreviewOobeClick(object sender, RoutedEventArgs e) => _onPreviewOobe?.Invoke();

    /// <summary>Clears the OOBE-completed flag so the welcome animation replays on the next app start.</summary>
    private void OnResetOobeClick(object sender, RoutedEventArgs e)
    {
        try
        {
            _settings.Current.OobeCompleted = false;
            _settings.Save();
            _notifyConflict?.Invoke(L.Get("Settings.ResetOobe.Done"));
            Log.Info("OOBE reset from settings; welcome screen will replay on next launch.");
        }
        catch (Exception ex)
        {
            Log.Error("Resetting the OOBE flag from settings failed", ex);
        }
    }

    private void OnOk(object sender, RoutedEventArgs e) => Close();

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        foreach (var action in HotkeyActions.All)
        {
            if (_actionLabels.TryGetValue(action, out var label)) label.Text = L.Get($"Mode.{action}");
            RefreshHotkeyRow(action); // placeholder text is localized; chips content is language-neutral
            if (_conflictMarks.TryGetValue(action, out var mark)) mark.ToolTip = L.Get("Settings.Conflict");
        }
        RefreshAboutTexts();
        // These two hints are set imperatively at load time; without refreshing them here they keep
        // the previous language after a live switch (tray menu) until the window reopens.
        CapturePriorityHint.Text = L.Get(ElevationService.IsElevated ? "Settings.CapturePriorityHint" : "Settings.CapturePriorityHintNormal");
        SaveDirHint.Text = string.Format(L.Get("Settings.SaveDirDefault"), AppPaths.DefaultSaveDir);
        if (!_ocrDownloading) RefreshOcrModelStatus();
        if (!_translateDownloading) RefreshTranslateStatus();
        // The language names in the combo box follow the UI language too.
        if (!_translatePopulating) PopulateTranslateLanguages();
        // The language may have been changed elsewhere (e.g. tray menu) while this window is open;
        // without this sync, OK would write the stale combo value back and revert that choice.
        _syncingLanguage = true;
        LanguageBox.SelectedIndex = _settings.Current.Language switch
        {
            LocalizationService.Chinese => 1,
            LocalizationService.English => 2,
            _ => 0,
        };
        _syncingLanguage = false;
    }

    // ---- persistence ----

    private void OnClosed(object? sender, EventArgs e)
    {
        _localization.LanguageChanged -= OnLanguageChanged;
        if (!_wasShown) return; // never shown (e.g. --smoke): persist nothing

        WriteBack();
        try
        {
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Saving settings failed", ex);
        }

        var failed = _hotkeys?.ReRegister() ?? [];
        foreach (var action in failed)
        {
            var binding = _editedBindings.GetValueOrDefault(action, new HotkeyBinding());
            _notifyConflict?.Invoke(L.Get("Toast.HotkeyConflict", HotkeyService.Format(binding)));
        }
    }

    private void WriteBack()
    {
        var s = _settings.Current;
        s.Language = LanguageBox.SelectedIndex switch
        {
            1 => LocalizationService.Chinese,
            2 => LocalizationService.English,
            _ => "",
        };
        s.Theme = ThemeBox.SelectedIndex switch
        {
            1 => "Light",
            2 => "Dark",
            _ => "System",
        };
        s.StartWithWindows = StartWithWindowsBox.IsChecked == true;
        s.Hotkeys.Bindings = _editedBindings;
        s.Capture.DelaySeconds = ClampValue(DelayBox.Value, s.Capture.DelaySeconds, 1, 60);
        s.Capture.AutoElementDetection = AutoDetectBox.IsChecked == true;
        s.Capture.ShowMagnifier = ShowMagnifierBox.IsChecked == true;
        s.Capture.CaptureCursor = CaptureCursorBox.IsChecked == true;
        s.Capture.WindowTransparentCorners = TransparentCornersBox.IsChecked == true;
        s.Capture.MacStyleWindowShadow = MacShadowBox.IsChecked == true;
        // Only persist the priority toggle when elevated: when normal the checkbox is hidden and the
        // row shows the "run as admin" button instead, so its (unchecked) state must not clobber the
        // stored preference.
        if (ElevationService.IsElevated) s.Capture.CapturePriority = CapturePriorityBox.IsChecked == true;
        s.HistoryMaxCount = ClampValue(HistoryMaxBox.Value, s.HistoryMaxCount, 0, 5000);
        s.Output.SaveDirectory = SaveDirBox.Text.Trim();
        s.Output.Format = Enum.IsDefined((ImageFormat)FormatBox.SelectedIndex)
            ? (ImageFormat)FormatBox.SelectedIndex : s.Output.Format;
        s.Output.JpgQuality = (int)JpgQualitySlider.Value;
        s.Output.WebPQuality = (int)WebPQualitySlider.Value;
        s.Output.FileNameTemplate = TemplateBox.Text;
        s.Output.AutoSave = AutoSaveBox.IsChecked == true;
        s.Output.AutoCopy = AutoCopyBox.IsChecked == true;
        s.Output.AfterRegionCapture = Enum.IsDefined((AfterCaptureAction)AfterRegionBox.SelectedIndex)
            ? (AfterCaptureAction)AfterRegionBox.SelectedIndex : s.Output.AfterRegionCapture;
        s.Output.AfterOtherCapture = Enum.IsDefined((AfterCaptureAction)AfterOtherBox.SelectedIndex)
            ? (AfterCaptureAction)AfterOtherBox.SelectedIndex : s.Output.AfterOtherCapture;
        s.Output.ApplyEffectsOnExport = ApplyEffectsBox.IsChecked == true;
        s.Ocr.Accuracy = OcrAccuracyBox.SelectedIndex == 1 ? OcrAccuracy.Accurate : OcrAccuracy.Fast;
        s.Ocr.CopyAfterRecognize = OcrAutoCopyBox.IsChecked == true;
        s.Translation.AutoDetectSource = TranslateAutoDetectBox.IsChecked == true;
        s.Translation.CopyAfterTranslate = TranslateAutoCopyBox.IsChecked == true;
        if (SelectedLanguage is { } language) s.Translation.ToCode = language.Code;
    }

    /// <summary>Clamps a NumberBox value (null when the box is empty) into range, falling back to the
    /// stored value when unset.</summary>
    private static int ClampValue(double? value, int fallback, int min, int max)
    {
        if (value is not { } v || double.IsNaN(v)) return fallback;
        return Math.Clamp((int)Math.Round(v), min, max);
    }
}
