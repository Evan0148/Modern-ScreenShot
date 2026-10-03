using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using ModernScreenShot.App.Capture;
using ModernScreenShot.App.Controls;
using ModernScreenShot.App.Editor;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Localization;
using ModernScreenShot.App.Ocr;
using ModernScreenShot.App.Output;
using ModernScreenShot.App.Overlay;
using ModernScreenShot.App.Services;
using ModernScreenShot.App.Settings;
using ModernScreenShot.App.Shell;
using ModernScreenShot.Core.Annotation;
using ModernScreenShot.Core.History;
using ModernScreenShot.Core.Imaging;
using ModernScreenShot.Core.Ocr;
using ModernScreenShot.Core.Output;
using ModernScreenShot.Core.Settings;
using Backdrop = Wpf.Ui.Controls.WindowBackdropType;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App;

/// <summary>
/// Feature wiring. Later tasks register their services and start-up logic here so App.xaml.cs stays stable.
/// </summary>
public partial class App
{
    /// <summary>True once the tray shell is running; then the app stays resident.</summary>
    private bool TrayStarted { get; set; }

    private readonly List<Window> EditorWindows = [];

    private SingleInstance? _singleInstance;
    private HotkeyService? _hotkeys;
    private TrayService? _tray;
    private SettingsWindow? _settingsWindow;
    private bool _captureInProgress;

    partial void RegisterFeatureServices(IServiceCollection services)
    {
        services.AddSingleton<ScrollingCaptureService>();
        services.AddSingleton<CaptureService>();
        services.AddSingleton<ClipboardService>();
        services.AddSingleton<ImageExporter>();
        services.AddSingleton<HistoryRecorder>();
        // Offline text recognition; the ONNX models load lazily on the first OCR, never at startup.
        services.AddSingleton<OcrService>();
        // Shell services: constructors are side-effect free; Start() is called explicitly below.
        services.AddSingleton<SingleInstance>();
        services.AddSingleton<HotkeyService>();
        services.AddSingleton<TrayService>();
    }

    partial void OnStartupCompleted(string[] args)
    {
        // Diagnostic render probes (--render-*) must run in-process regardless of any resident
        // instance: relaying to a resident app (worse, an elevated one this probe can't signal)
        // would defeat the snapshot entirely. Handle them before the single-instance handshake.
        string? earlyRender = args.FirstOrDefault(a => a.StartsWith("--render-", StringComparison.OrdinalIgnoreCase));
        if (earlyRender is not null)
        {
            RunDiagnosticRender(earlyRender["--render-".Length..]);
            return;
        }

        // --titlebar-probe[=target]: diagnostic title-bar probe used by tools/titlebar_probe.ps1.
        // Same pre-handshake placement as --render-*: a resident instance must never intercept it,
        // and the probe process has to own the window whose geometry/hit values it reports.
        string? titlebarProbe = args.FirstOrDefault(a => a.StartsWith("--titlebar-probe", StringComparison.OrdinalIgnoreCase));
        if (titlebarProbe is not null)
        {
            RunTitleBarProbe(titlebarProbe);
            return;
        }

        // --reset-oobe: clear the welcome-completed flag and exit, so the next normal launch replays
        // the OOBE. Handy for development and wired to the settings "show welcome again" button.
        if (args.Any(a => string.Equals(a, "--reset-oobe", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var store = Services.GetRequiredService<SettingsStore>();
                store.Current.OobeCompleted = false;
                store.Save();
                Log.Info("OOBE reset: welcome screen will replay on the next launch.");
            }
            catch (Exception ex) { Log.Error("Resetting the OOBE flag failed", ex); }
            Shutdown(0);
            return;
        }

        // --probe-oobe: live-path animation gate. Shows the welcome window at Opacity 0 (invisible but
        // fully composited, so the animation clocks tick), then asserts — BEFORE the window's own 5s
        // watchdog can mask a stall — that the intro, both words, the chrome and every letter reached
        // their revealed state, and that the two words sit side by side without overlapping. Exit 0 =
        // pass. This covers the real animation path that --render-* cannot see (UiMotion.Suppress
        // snaps straight to the final frame), which is how the broken preview shipped unnoticed.
        if (args.Any(a => string.Equals(a, "--probe-oobe", StringComparison.OrdinalIgnoreCase)))
        {
            RunOobeAnimationProbe();
            return;
        }

        // Single instance: when another instance already owns the mutex, hand it the command line
        // and exit without touching anything else.
        var single = Services.GetRequiredService<SingleInstance>();
        if (!single.TryStart(OnForwardedArguments))
        {
            if (!SingleInstance.TryForward(args))
                Log.Warn("Another instance is running but the arguments could not be forwarded.");
            Shutdown(0);
            return;
        }

        try
        {
            StartShell(single);
        }
        catch (Exception ex)
        {
            Log.Error("Tray shell startup failed", ex);
            if (ParseCaptureArg(args) is null)
            {
                Shutdown(1);
                return;
            }
            // With an explicit capture request the capture itself may still work; fall through.
        }

        if (ParseCaptureArg(args) is { } mode) RunCapture(mode);
        // Hidden diagnostic switches: open specific windows on startup (used for UI verification runs).
        if (StartupArgs.Any(a => string.Equals(a, "--show-settings", StringComparison.OrdinalIgnoreCase))) OpenSettings();
        if (StartupArgs.Any(a => string.Equals(a, "--show-history", StringComparison.OrdinalIgnoreCase))) OpenHistory();
        if (StartupArgs.Any(a => string.Equals(a, "--show-editor", StringComparison.OrdinalIgnoreCase))) ShowDiagnosticEditor();
        if (StartupArgs.Any(a => string.Equals(a, "--show-oobe", StringComparison.OrdinalIgnoreCase))) ShowOobe();
        if (StartupArgs.Any(a => string.Equals(a, "--show-ocr", StringComparison.OrdinalIgnoreCase))) ShowDiagnosticOcr();
        // Note: --render-* is handled earlier (before the single-instance handshake) so a resident
        // instance can't intercept the snapshot.
        if (!TrayStarted && EditorWindows.Count == 0) Shutdown(0);
    }

    /// <summary>Renders a diagnostic window to a PNG via RenderTargetBitmap, then exits.</summary>
    private void RunDiagnosticRender(string target)
    {
        // This path runs before StartShell, so apply the stored theme here — otherwise the snapshot
        // always uses the default (Light) theme regardless of the settings the probe wrote.
        ApplyTheme(Services.GetRequiredService<SettingsStore>().Current.Theme);
        // Deterministic snapshots: every UiMotion factory snaps its properties to the final values
        // instead of animating. No reset needed — this probe process always hard-exits when done.
        UiMotion.Suppress = true;
        switch (target.ToLowerInvariant())
        {
            case "settings": OpenSettings(); break;
            case "editor": ShowDiagnosticEditor(); break;
            case "history": OpenHistory(); break;
            case "oobe": ShowOobe(); break;
            case "ocr": ShowDiagnosticOcr(); break;
            default:
                Log.Warn($"--render-{target}: unknown window target.");
                Shutdown(2);
                return;
        }
        string? outPath = Environment.GetEnvironmentVariable("MSS_RENDER_OUT");
        if (string.IsNullOrWhiteSpace(outPath))
        {
            Log.Warn("--render-* requires the MSS_RENDER_OUT environment variable.");
            Shutdown(2);
            return;
        }
        // Give the window time to lay out and settle async content (history thumbnails) before the
        // snapshot; MSS_RENDER_WAIT_MS raises the default for slow starts.
        int waitMs = 1500;
        if (int.TryParse(Environment.GetEnvironmentVariable("MSS_RENDER_WAIT_MS"), out var parsed) && parsed > 0) waitMs = parsed;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(waitMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Window? win = null;
                foreach (Window w in Application.Current.Windows)
                    if (w is SettingsWindow or EditorWindow or HistoryWindow or OobeWindow or OcrResultWindow && w.Content is UIElement) { win = w; break; }
                if (win is null)
                    throw new InvalidOperationException($"no diagnostic window found for '{target}'.");
                // MSS_RENDER_TAB selects a settings page by index (0-based) before the snapshot.
                if (win is SettingsWindow settingsWindow
                    && int.TryParse(Environment.GetEnvironmentVariable("MSS_RENDER_TAB"), out var tab))
                {
                    settingsWindow.SelectPage(tab);
                }
                // MSS_RENDER_W/H force a window size; MSS_RENDER_EDITOR_EFFECTS=1 opens the effects
                // panel; MSS_RENDER_EDITOR_SELECT=1 selects the last annotation item;
                // MSS_RENDER_EDITOR_TOOL=<name> activates a tool — editor property-panel states.
                if (int.TryParse(Environment.GetEnvironmentVariable("MSS_RENDER_W"), out var rw) && rw >= 200) win.Width = rw;
                if (int.TryParse(Environment.GetEnvironmentVariable("MSS_RENDER_H"), out var rh) && rh >= 200) win.Height = rh;
                bool effectsRequested = false;
                if (win is EditorWindow editorWindow)
                {
                    effectsRequested = Environment.GetEnvironmentVariable("MSS_RENDER_EDITOR_EFFECTS") == "1";
                    editorWindow.DiagnosticPrepare(
                        effectsRequested,
                        Environment.GetEnvironmentVariable("MSS_RENDER_EDITOR_SELECT"),
                        Environment.GetEnvironmentVariable("MSS_RENDER_EDITOR_TOOL"),
                        Environment.GetEnvironmentVariable("MSS_RENDER_EDITOR_FX"));
                }
                win.UpdateLayout();
                // The effects preview bitmap arrives via a 60ms debounce timer + background compose;
                // without an extra delay the snapshot races it and shows an empty preview box.
                var snapshotTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(effectsRequested ? 900 : 0),
                };
                snapshotTimer.Tick += (_, _) =>
                {
                    snapshotTimer.Stop();
                    var exit = 0;
                    try
                    {
                        if (Environment.GetEnvironmentVariable("MSS_DUMP_TREE") == "1")
                            DumpVisualTree(win!, outPath! + ".tree.txt");
                        SnapshotWindow(win!, target, outPath!);
                        Log.Info($"--render-{target}: wrote {outPath}");
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"--render-{target} failed", ex);
                        exit = 1;
                    }
                    // One-shot probe process: hard-exit. The graceful Shutdown occasionally leaves
                    // the dispatcher waiting on the in-flight preview task and the probe hangs.
                    Environment.Exit(exit);
                };
                snapshotTimer.Start();
            }
            catch (Exception ex)
            {
                Log.Error($"--render-{target} failed", ex);
                Shutdown(1);
            }
        };
        timer.Start();
    }

    /// <summary>Renders the window visual itself to a PNG (its Background paint lives on the Window,
    /// not on Content — rendering Content alone yields a transparent backdrop).</summary>
    private static void SnapshotWindow(Window win, string target, string outPath)
    {
        win.UpdateLayout();
        double sx = 1, sy = 1;
        try { var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(win); sx = dpi.DpiScaleX; sy = dpi.DpiScaleY; } catch { /* 96 dpi fallback */ }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Math.Max(1, (int)Math.Round(win.ActualWidth * sx)),
            Math.Max(1, (int)Math.Round(win.ActualHeight * sy)),
            96 * sx, 96 * sy, System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(win);
        var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
        enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using var fs = System.IO.File.Create(outPath);
        enc.Save(fs);
    }

    // ---- title bar probe (--titlebar-probe) ----
    //
    // Diagnostic-only harness for tools/titlebar_probe.ps1. It reports (a) title-bar geometry
    // measured against the real, physical window edge, (b) WM_NCHITTEST answers from the live HWND
    // and (c) forced interaction states (hover / pressed / inactive / residue triggers) rendered to
    // MSS_RENDER_OUT for pixel sampling. Nothing here runs on a normal launch; the probe process
    // hard-exits and never joins the shell.

    private const int WmNcHitTest = 0x0084;
    private const int WmNcMouseLeave = 0x02A3;
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    // Glyphs as raw code points: the probe must keep classifying buttons even if the control
    // constants move during the title-bar refactor.
    private const string ProbeGlyphMinimize = "\uE921";
    private const string ProbeGlyphMaximize = "\uE922";
    private const string ProbeGlyphRestore = "\uE923";
    private const string ProbeGlyphClose = "\uE8BB";

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint flags, int dx, int dy, uint data, IntPtr extraInfo);

    private sealed record ProbeButtonInfo(string Role, double X, double Y, double W, double H,
        double CenterX, double CenterY, double PhysL, double PhysT, double PhysR, double PhysB,
        double PhysCenterX, double PhysCenterY);

    private void RunTitleBarProbe(string arg)
    {
        // Same preconditions as --render-*: observe the stored theme and settle the window
        // entrances so geometry and state pixels are deterministic.
        ApplyTheme(Services.GetRequiredService<SettingsStore>().Current.Theme);
        UiMotion.Suppress = true;

        int separator = arg.IndexOf('=');
        string target = (separator >= 0 && separator < arg.Length - 1 ? arg[(separator + 1)..] : null)
            ?? Environment.GetEnvironmentVariable("MSS_TITLEBAR_PROBE_TARGET")
            ?? "settings";
        target = target.Trim().ToLowerInvariant();
        string state = (Environment.GetEnvironmentVariable("MSS_TITLEBAR_PROBE_STATE") ?? "rest").Trim().ToLowerInvariant();
        string? outPath = Environment.GetEnvironmentVariable("MSS_TITLEBAR_PROBE_OUT");
        if (string.IsNullOrWhiteSpace(outPath))
            outPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "mss_titlebar_probe.txt");

        Window? win = OpenProbeWindow(target);
        if (win is null)
        {
            WriteProbeFile(outPath, [$"TBPROBE target={target}", "TBPROBE error=unknown-target", "TBPROBE done=0"]);
            Environment.Exit(2);
        }

        int waitMs = 1200;
        if (int.TryParse(Environment.GetEnvironmentVariable("MSS_TITLEBAR_PROBE_WAIT_MS"), out var parsed) && parsed > 0) waitMs = parsed;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(waitMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try { RunTitleBarProbeSequence(win!, target, state, outPath); }
            catch (Exception ex)
            {
                Log.Error("--titlebar-probe failed", ex);
                AppendProbeFile(outPath, ["TBPROBE error=" + ex.GetType().Name, "TBPROBE done=0"]);
                Environment.Exit(1);
            }
        };
        timer.Start();
    }

    private Window? OpenProbeWindow(string target)
    {
        switch (target)
        {
            case "settings": OpenSettings(); break;
            case "editor": ShowDiagnosticEditor(); break;
            case "history": OpenHistory(); break;
            case "oobe": ShowOobe(); break;
            case "ocr": ShowDiagnosticOcr(); break;
            case "hotkey":
            {
                var dialog = new HotkeyEditDialog("Region", HotkeySettings.CreateDefaults(), null,
                    Services.GetRequiredService<LocalizationService>());
                dialog.Show();
                return dialog;
            }
            default:
                Log.Warn($"--titlebar-probe: unknown window target '{target}'.");
                return null;
        }
        foreach (Window w in Application.Current.Windows)
        {
            if (w is SettingsWindow or EditorWindow or HistoryWindow or OobeWindow or OcrResultWindow && w.Content is UIElement)
                return w;
        }
        return null;
    }

    private void RunTitleBarProbeSequence(Window win, string target, string state, string outPath)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
        NativeMethods.SetForegroundWindow(hwnd);
        win.Activate();
        win.UpdateLayout();

        var lines = new List<string>
        {
            $"TBPROBE target={target}",
            $"TBPROBE state={state}",
            $"TBPROBE hwnd=0x{hwnd.ToInt64():X}",
            $"TBPROBE os={Environment.OSVersion.Version}",
        };

        var bar = FindDescendants<AppTitleBar>(win).FirstOrDefault();
        if (bar is null)
        {
            lines.Add("TBPROBE error=no-title-bar");
            lines.Add("TBPROBE done=0");
            WriteProbeFile(outPath, lines);
            Environment.Exit(2);
            return;
        }

        if (Environment.GetEnvironmentVariable("MSS_DUMP_TREE") == "1")
        {
            string treePath = (Environment.GetEnvironmentVariable("MSS_RENDER_OUT") ?? outPath) + ".tree.txt";
            DumpVisualTree(win, treePath);
        }

        NativeMethods.GetWindowRect(hwnd, out RECT windowRect);
        RECT visibleFrame = NativeMethods.GetFrameBounds(hwnd);
        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;

        var barOrigin = bar.TransformToVisual(win).Transform(new Point(0, 0));
        double barX = barOrigin.X, barY = barOrigin.Y;
        double barW = bar.ActualWidth, barH = bar.ActualHeight;
        double contentSlotTop = ComputeProbeContentSlotTop(bar, win);
        double contentTopOffset = double.IsNaN(contentSlotTop) ? double.NaN : contentSlotTop - barY;
        var card = FindProbeCard(bar);

        lines.Add($"TBPROBE windowDip={Fmt(win.ActualWidth)},{Fmt(win.ActualHeight)}");
        lines.Add($"TBPROBE dpi={dpi}");
        lines.Add($"TBPROBE scale={Fmt(scale)}");
        lines.Add($"TBPROBE layered={(win.AllowsTransparency ? 1 : 0)}");
        lines.Add($"TBPROBE windowRect={windowRect.Left},{windowRect.Top},{windowRect.Right},{windowRect.Bottom}");
        lines.Add($"TBPROBE visibleFrame={visibleFrame.Left},{visibleFrame.Top},{visibleFrame.Right},{visibleFrame.Bottom}");
        lines.Add($"TBPROBE bar={Fmt(barX)},{Fmt(barY)},{Fmt(barW)},{Fmt(barH)}");
        lines.Add($"TBPROBE barHeight={Fmt(barH)}");
        lines.Add($"TBPROBE contentTopOffset={Fmt(contentTopOffset)}");
        lines.Add($"TBPROBE token=secondary|{ResolveBrushHex(TitleBarMetrics.SubtleFillColorSecondaryBrushKey)}");
        lines.Add($"TBPROBE token=tertiary|{ResolveBrushHex(TitleBarMetrics.SubtleFillColorTertiaryBrushKey)}");
        lines.Add($"TBPROBE token=textTertiary|{ResolveBrushHex(TitleBarMetrics.TextFillColorTertiaryBrushKey)}");
        if (card is not null && win.AllowsTransparency)
        {
            var cardCorner = card.PointToScreen(new Point(card.ActualWidth, card.ActualHeight / 2));
            lines.Add($"TBPROBE cardRight={Fmt(cardCorner.X)}");
            lines.Add($"TBPROBE cardInnerRight={Fmt(cardCorner.X - card.BorderThickness.Right * scale)}");
        }

        // Caption buttons left-to-right; role by glyph with a positional fallback (rightmost =
        // close) so a template refactor cannot silently drop the assertions.
        var buttons = new List<ProbeButtonInfo>();
        foreach (var button in FindDescendants<Button>(bar)
                     .Where(b => b.Visibility == Visibility.Visible && b.ActualWidth >= 20 && b.ActualHeight >= 10)
                     .OrderBy(b => b.TransformToVisual(win).Transform(new Point(0, 0)).X))
        {
            buttons.Add(DescribeProbeButton(button, ProbeButtonRole(button), win));
        }
        for (int i = buttons.Count - 1; i >= 0; i--)
        {
            if (buttons[i].Role != "unknown") continue;
            buttons[i] = buttons[i] with { Role = i == buttons.Count - 1 ? "close" : "max" };
            break;
        }

        foreach (var b in buttons)
            lines.Add($"TBPROBE button={b.Role}|{Fmt(b.X)},{Fmt(b.Y)},{Fmt(b.W)},{Fmt(b.H)}|{Fmt(b.PhysL)},{Fmt(b.PhysT)},{Fmt(b.PhysR)},{Fmt(b.PhysB)}");

        var title = FindDescendants<TextBlock>(bar)
            .FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text) && t.Text == bar.Text)
            ?? FindDescendants<TextBlock>(bar).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.Text));
        if (title is not null)
        {
            var titleOrigin = title.TransformToVisual(win).Transform(new Point(0, 0));
            lines.Add($"TBPROBE title={Fmt(titleOrigin.X)},{Fmt(titleOrigin.Y)},{Fmt(title.ActualWidth)},{Fmt(title.ActualHeight)}");
        }

        // WM_NCHITTEST answers from the real HWND. On a window without a maximize button the
        // max-button slot (left of close) is probed instead so the NoResize gate stays checkable.
        var max = buttons.FirstOrDefault(b => b.Role == "max");
        var close = buttons.FirstOrDefault(b => b.Role == "close") ?? buttons.LastOrDefault();
        var blank = bar.PointToScreen(new Point(barW * 0.5, barH * 0.5));
        lines.Add($"TBPROBE hittest=blank|{Fmt(blank.X)},{Fmt(blank.Y)}|{ProbeHitTest(hwnd, blank.X, blank.Y)}");
        if (max is not null)
        {
            lines.Add($"TBPROBE hittest=max|{Fmt(max.PhysCenterX)},{Fmt(max.PhysCenterY)}|{ProbeHitTest(hwnd, max.PhysCenterX, max.PhysCenterY)}");
        }
        else if (close is not null)
        {
            double slotX = close.PhysL - 24 * scale;
            lines.Add($"TBPROBE hittest=max|{Fmt(slotX)},{Fmt(close.PhysCenterY)}|{ProbeHitTest(hwnd, slotX, close.PhysCenterY)}");
        }

        WriteProbeFile(outPath, lines);

        if (state is "rest")
        {
            ProbeSnapshot(win, target);
            AppendProbeFile(outPath, ["TBPROBE done=1"]);
            Environment.Exit(0);
        }
        RunProbeState(win, target, state, hwnd, max, close, outPath);
    }

    /// <summary>Drives the requested interaction state through the real cursor / message path so
    /// WPF's own state machine (and, later, the snap hook's NC state) is exercised, not simulated.</summary>
    private void RunProbeState(Window win, string target, string state, IntPtr hwnd,
        ProbeButtonInfo? max, ProbeButtonInfo? close, string outPath)
    {
        var hover = (state == "close-hover" ? close : max) ?? close ?? max;
        if (hover is null)
        {
            AppendProbeFile(outPath, ["TBPROBE error=no-caption-button", "TBPROBE done=0"]);
            Environment.Exit(2);
            return;
        }

        void Finish()
        {
            AppendProbeFile(outPath, ["TBPROBE done=1"]);
            Environment.Exit(0);
        }

        NativeMethods.SetCursorPos((int)hover.PhysCenterX, (int)hover.PhysCenterY);
        ProbeDelay(550, () =>
        {
            switch (state)
            {
                case "hover":
                case "close-hover":
                    ProbeSnapshot(win, target);
                    Finish();
                    break;

                case "pressed":
                    mouse_event(MouseEventLeftDown, 0, 0, 0, IntPtr.Zero);
                    ProbeDelay(350, () =>
                    {
                        ProbeSnapshot(win, target);
                        NativeMethods.SetCursorPos(2, 2); // release away from the button: no Click
                        mouse_event(MouseEventLeftUp, 0, 0, 0, IntPtr.Zero);
                        Finish();
                    });
                    break;

                case "inactive":
                case "residue-deactivate":
                {
                    var auxiliary = CreateProbeAuxiliaryWindow();
                    auxiliary.Show();
                    var auxHwnd = new System.Windows.Interop.WindowInteropHelper(auxiliary).Handle;
                    auxiliary.Activate();
                    NativeMethods.SetForegroundWindow(auxHwnd);
                    ProbeDelay(650, () =>
                    {
                        // The trigger must be real: report whether the probe window actually lost
                        // activation, so a pass can never be vacuous.
                        bool deactivated = NativeMethods.GetForegroundWindow() != hwnd;
                        AppendProbeFile(outPath, [$"TBPROBE activeAfterTrigger={(deactivated ? 0 : 1)}"]);
                        if (max is not null)
                        {
                            int code = ProbeHitTest(hwnd, max.PhysCenterX, max.PhysCenterY);
                            AppendProbeFile(outPath, [$"TBPROBE hittest=maxInactive|{Fmt(max.PhysCenterX)},{Fmt(max.PhysCenterY)}|{code}"]);
                        }
                        ProbeSnapshot(win, target);
                        Finish();
                    });
                    break;
                }

                case "residue-modal":
                {
                    var modal = new Window
                    {
                        Style = null,
                        Owner = win,
                        Width = 320,
                        Height = 160,
                        WindowStartupLocation = WindowStartupLocation.CenterOwner,
                        ShowInTaskbar = false,
                        ResizeMode = ResizeMode.NoResize,
                        Title = "titlebar-probe-modal",
                    };
                    ProbeDelay(650, () =>
                    {
                        ProbeSnapshot(win, target);
                        modal.Close();
                        Finish();
                    });
                    modal.ShowDialog(); // nested dispatcher frame: the timer above still ticks
                    break;
                }

                case "residue-ncleave":
                    SendMessage(hwnd, WmNcMouseLeave, IntPtr.Zero, IntPtr.Zero);
                    ProbeDelay(400, () =>
                    {
                        ProbeSnapshot(win, target);
                        Finish();
                    });
                    break;

                default:
                    AppendProbeFile(outPath, ["TBPROBE error=unknown-state", "TBPROBE done=0"]);
                    Environment.Exit(2);
                    break;
            }
        });
    }

    /// <summary>Top of the layout slot directly below the bar: each non-bar sibling's origin minus
    /// its top margin (Grid rows / DockPanel docks define the slot; margins are decorative).</summary>
    private static double ComputeProbeContentSlotTop(AppTitleBar bar, Visual win)
    {
        if (VisualTreeHelper.GetParent(bar) is not FrameworkElement parent) return double.NaN;
        double min = double.MaxValue;
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(parent, i) is not FrameworkElement child || ReferenceEquals(child, bar)) continue;
            if (child.ActualHeight <= 0) continue;
            var origin = child.TransformToVisual(win).Transform(new Point(0, 0));
            min = Math.Min(min, origin.Y - child.Margin.Top);
        }
        return min == double.MaxValue ? double.NaN : min;
    }

    /// <summary>Innermost Border ancestor of the bar — the rounded card of a layered dialog, whose
    /// transparent shadow margin is not part of the visible card. Null for plain windows.</summary>
    private static Border? FindProbeCard(AppTitleBar bar)
    {
        DependencyObject? current = VisualTreeHelper.GetParent(bar);
        while (current is not null and not Window)
        {
            if (current is Border border) return border;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private static ProbeButtonInfo DescribeProbeButton(Button button, string role, Visual win)
    {
        var toWindow = button.TransformToVisual(win);
        var origin = toWindow.Transform(new Point(0, 0));
        var center = toWindow.Transform(new Point(button.ActualWidth / 2, button.ActualHeight / 2));
        var topLeft = button.PointToScreen(new Point(0, 0));
        var bottomRight = button.PointToScreen(new Point(button.ActualWidth, button.ActualHeight));
        return new ProbeButtonInfo(role, origin.X, origin.Y, button.ActualWidth, button.ActualHeight,
            center.X, center.Y, topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y,
            (topLeft.X + bottomRight.X) / 2, (topLeft.Y + bottomRight.Y) / 2);
    }

    private static string ProbeButtonRole(Button button)
    {
        string? glyph = button switch
        {
            CaptionButton caption => caption.Glyph,
            _ => (button.Content as TextBlock)?.Text,
        };
        return glyph switch
        {
            ProbeGlyphMinimize => "min",
            ProbeGlyphMaximize or ProbeGlyphRestore => "max",
            ProbeGlyphClose => "close",
            _ => "unknown",
        };
    }

    private static int ProbeHitTest(IntPtr hwnd, double physX, double physY)
    {
        int x = (int)Math.Round(physX), y = (int)Math.Round(physY);
        var lParam = (IntPtr)((y << 16) | (x & 0xFFFF));
        return (int)SendMessage(hwnd, WmNcHitTest, IntPtr.Zero, lParam).ToInt64();
    }

    private static string ResolveBrushHex(string key) =>
        Application.Current?.TryFindResource(key) is SolidColorBrush brush
            ? $"#{brush.Color.A:X2}{brush.Color.R:X2}{brush.Color.G:X2}{brush.Color.B:X2}"
            : "none";

    private static Window CreateProbeAuxiliaryWindow()
    {
        var work = SystemParameters.WorkArea;
        return new Window
        {
            // Explicitly bypass any implicit Window style: the probe's throw-away window must not
            // resolve app-level window styling (that path throws when it touches AllowsTransparency).
            Style = null,
            Width = 160,
            Height = 80,
            Left = work.Right - 200,
            Top = work.Bottom - 120,
            WindowStartupLocation = WindowStartupLocation.Manual,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.NoResize,
            Title = "titlebar-probe-aux",
        };
    }

    private static void ProbeDelay(int ms, Action action)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => { timer.Stop(); action(); };
        timer.Start();
    }

    private static void ProbeSnapshot(Window win, string target)
    {
        string? outPath = Environment.GetEnvironmentVariable("MSS_RENDER_OUT");
        if (!string.IsNullOrWhiteSpace(outPath)) SnapshotWindow(win, target, outPath);
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindDescendants<T>(child)) yield return nested;
        }
    }

    private static string Fmt(double value) =>
        double.IsNaN(value) ? "nan" : value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    private static void WriteProbeFile(string path, IEnumerable<string> lines)
    {
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        System.IO.File.WriteAllLines(path, lines);
    }

    private static void AppendProbeFile(string path, IEnumerable<string> lines)
    {
        string? dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
        System.IO.File.AppendAllLines(path, lines);
    }

    partial void OnSmokeTest(IServiceProvider services)
    {
        _ = services.GetRequiredService<ScrollingCaptureService>();
        _ = services.GetRequiredService<CaptureService>();
        _ = services.GetRequiredService<ClipboardService>();
        _ = services.GetRequiredService<ImageExporter>();
        // Shell services resolve without side effects (tray icon / hotkeys / mutex start explicitly).
        _ = services.GetRequiredService<SingleInstance>();
        _ = services.GetRequiredService<HotkeyService>();
        _ = services.GetRequiredService<TrayService>();
        // Instantiate the editor off-screen to catch XAML/ctor regressions in both languages.
        var testResult = new CaptureResult
        {
            Image = new PixelBuffer(64, 48),
            Mode = CaptureMode.Region,
            SourceRect = new PixelRect(0, 0, 64, 48),
        };
        var editor = new EditorWindow(testResult, services.GetRequiredService<SettingsStore>(),
            services.GetRequiredService<ClipboardService>(), services.GetRequiredService<ImageExporter>());
        editor.Close();
        var history = new HistoryWindow(services.GetRequiredService<HistoryStore>(),
            services.GetRequiredService<SettingsStore>(), services.GetRequiredService<ClipboardService>(),
            (_, _) => { });
        history.Close();
        var settings = new SettingsWindow(services.GetRequiredService<SettingsStore>(),
            services.GetRequiredService<LocalizationService>(),
            services.GetRequiredService<HotkeyService>(), _ => { }, null);
        settings.Close();
        var countdown = new CountdownWindow();
        countdown.Close();
        var oobe = new OobeWindow(() => { });
        oobe.Close();
        // OCR: the models must ship next to the app, and the result window must construct cleanly.
        var ocrService = services.GetRequiredService<OcrService>();
        if (!ocrService.HasBundledModels)
            throw new InvalidOperationException($"OCR models are missing next to the app: {OcrService.DefaultBundleDir}");
        var ocrWindow = new OcrResultWindow(
            new OcrTextResult { Text = "smoke", Lines = [new OcrLine("smoke", 1.0)], ElapsedMs = 1 },
            services.GetRequiredService<ClipboardService>(), autoCopy: false);
        ocrWindow.Close();
        // Pin window: covers the Snipaste-style shadow + context-menu construction (never shown).
        var pin = new PinWindow(new PixelBuffer(64, 48), services.GetRequiredService<ImageExporter>(),
            services.GetRequiredService<ClipboardService>(), null, null,
            store: services.GetRequiredService<SettingsStore>());
        pin.Close();
        Log.Info("smoke: shell services, SettingsWindow, CountdownWindow, OobeWindow, OcrResultWindow and PinWindow instantiated (nothing shown).");
    }

    // ---- shell ----

    private void StartShell(SingleInstance single)
    {
        var hotkeys = Services.GetRequiredService<HotkeyService>();
        hotkeys.HotkeyPressed += OnHotkeyAction;
        hotkeys.Start();

        var tray = Services.GetRequiredService<TrayService>();
        tray.Start(new TrayService.Actions
        {
            RunCapture = RunCapture,
            RunDelayedCapture = RunDelayedCapture,
            OpenHistory = OpenHistory,
            OpenSettings = OpenSettings,
            Exit = ExitApp,
        });

        _singleInstance = single;
        _hotkeys = hotkeys;
        _tray = tray;
        TrayStarted = true;

        ApplyTheme(Services.GetRequiredService<SettingsStore>().Current.Theme);
        ShowOobeIfNeeded();
        Application.Current.Exit += OnAppExitCleanup;
        Log.Info("Tray shell started (tray icon, hotkeys, single-instance listener).");
    }

    /// <summary>Applies the stored theme ("System"/"Light"/"Dark") via WPF-UI.</summary>
    internal static void ApplyTheme(string theme)
    {
        try
        {
            var appTheme = theme switch
            {
                "Light" => Wpf.Ui.Appearance.ApplicationTheme.Light,
                "Dark" => Wpf.Ui.Appearance.ApplicationTheme.Dark,
                _ => Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme() == Wpf.Ui.Appearance.SystemTheme.Dark
                    ? Wpf.Ui.Appearance.ApplicationTheme.Dark
                    : Wpf.Ui.Appearance.ApplicationTheme.Light,
            };
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(appTheme, Backdrop.None, false);
            Log.Info($"Theme applied: '{theme}' -> {appTheme}.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Theme '{theme}' could not be applied: {ex.Message}");
        }
    }

    /// <summary>
    /// First-run gate. On the very first launch the OOBE welcome window is shown (with its entrance
    /// animation); when the user finishes/skips it, the completion flag is persisted and the resident
    /// tray hint is shown. On later launches the OOBE is skipped and only the tray hint runs once.
    /// </summary>
    private void ShowOobeIfNeeded()
    {
        var store = Services.GetRequiredService<SettingsStore>();
        if (store.Current.OobeCompleted)
        {
            ShowFirstRunNotice();
            return;
        }
        ShowOobe();
    }

    /// <summary>Opens the OOBE welcome window; on completion persists the flag and shows the tray hint.
    /// Also used by the --show-oobe diagnostic switch (which does not re-gate on the flag).</summary>
    private void ShowOobe()
    {
        var oobe = new OobeWindow(OnOobeCompleted);
        oobe.Closed += OnEditorClosed; // keep the app alive while the welcome window is open
        EditorWindows.Add(oobe);
        oobe.Opacity = 0; // fade-in preset: WindowFadeIn brings it up right after Show
        oobe.Show();
        oobe.Activate();
        UiMotion.WindowFadeIn(oobe, 200);
        Log.Info("OOBE welcome window opened.");
    }

    /// <summary>Debug-category preview: replays the OOBE welcome window (with its entrance animation)
    /// without touching the OobeCompleted flag or any preference — the config step is shown but inert.</summary>
    private void PreviewOobe()
    {
        var oobe = new OobeWindow(onCompleted: () => { }, persistSettings: false);
        oobe.Closed += OnEditorClosed;
        EditorWindows.Add(oobe);
        oobe.Opacity = 0;
        oobe.Show();
        oobe.Activate();
        UiMotion.WindowFadeIn(oobe, 200);
        Log.Info("OOBE preview opened from the debug category.");
    }

    /// <summary>
    /// Live-path OOBE animation probe (--probe-oobe). Shows the welcome window invisible (Opacity 0 —
    /// the HWND is composited, so animation clocks tick normally), then at 3.2s — after the natural
    /// end of the animation chain (~2.6s) but BEFORE the window's own 5s watchdog can force-settle a
    /// stalled chain — asserts the revealed state and the no-overlap word geometry. Hard-exits 0/1
    /// like the other diagnostic probes.
    /// </summary>
    private void RunOobeAnimationProbe()
    {
        ApplyTheme(Services.GetRequiredService<SettingsStore>().Current.Theme);
        var win = new OobeWindow(() => { });
        // MSS_PROBE_VISIBLE=1: the end-to-end gate. The window shows for real (topmost, no focus
        // steal) and the probe additionally reads what DWM actually presents — the Opacity-0 mode
        // can never see a black presentation, which is exactly how the black welcome window shipped.
        bool visible = Environment.GetEnvironmentVariable("MSS_PROBE_VISIBLE") == "1";
        win.ShowActivated = false;
        if (visible) win.Topmost = true;
        else win.Opacity = 0; // never flashes on screen; the animation still runs for real
        win.Show();

        // Mid-drag geometry: at 1s the box is fully grown and must be centred (fail fast — a broken
        // drag would otherwise only surface as the 3.2s word checks passing while the drag is wrong).
        var boxTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        boxTimer.Tick += (_, _) =>
        {
            boxTimer.Stop();
            if (!win.DiagnosticBoxCentered)
            {
                Log.Error("probe-oobe: FAIL - the selection box is not centred in the window during the drag.");
                Environment.Exit(1);
            }
            Log.Info("probe-oobe: box centred during drag.");
        };
        boxTimer.Start();

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            int exit;
            try
            {
                bool revealed = win.DiagnosticFullyRevealed;
                bool layout = win.DiagnosticLayoutValid;
                bool pixels = true;
                double black = -1;
                if (visible && revealed && layout)
                {
                    var hwnd = new System.Windows.Interop.WindowInteropHelper(win).Handle;
                    pixels = hwnd != IntPtr.Zero
                             && win.TrySampleOwnScreenPixels(hwnd, out black)
                             && black < 0.98;
                }
                if (revealed && layout && pixels)
                {
                    Log.Info(visible
                        ? $"probe-oobe: PASS - live animation path revealed, words centred without overlap, on-screen pixels sane (black {black:P1})."
                        : "probe-oobe: PASS - intro/words/chrome fully revealed via the live animation path, words side by side without overlap.");
                    exit = 0;
                }
                else
                {
                    Log.Error($"probe-oobe: FAIL - revealed={revealed} layout={layout} pixels={pixels} (black={(black < 0 ? "n/a" : $"{black:P1}")}) geometry[{win.DiagnosticLayoutDescription}] (live animation path did not settle as expected).");
                    exit = 1;
                }
            }
            catch (Exception ex)
            {
                Log.Error("probe-oobe: crashed while checking the settled state", ex);
                exit = 1;
            }
            Environment.Exit(exit);
        };
        timer.Start();
    }

    private void OnOobeCompleted()
    {
        var store = Services.GetRequiredService<SettingsStore>();
        if (!store.Current.OobeCompleted)
        {
            store.Current.OobeCompleted = true;
            try { store.Save(); }
            catch (Exception ex) { Log.Error("Persisting OobeCompleted failed", ex); }
        }
        Log.Info("OOBE completed.");
        // Still surface the resident tray hint the first time so users know the app keeps running.
        ShowFirstRunNotice();
    }

    private void ShowFirstRunNotice()
    {
        var store = Services.GetRequiredService<SettingsStore>();
        if (store.Current.FirstRunShown) return;
        _tray?.ShowNotification(L.Get("FirstRun.Title"), L.Get("FirstRun.Body"));
        store.Current.FirstRunShown = true;
        try
        {
            store.Save();
        }
        catch (Exception ex)
        {
            Log.Error("Persisting FirstRunShown failed", ex);
        }
        Log.Info("First run notice shown.");
    }

    private void OnForwardedArguments(string[] args)
    {
        if (ParseCaptureArg(args) is not { } mode)
        {
            Log.Info("Forwarded arguments contained no capture mode; ignored.");
            return;
        }
        RunCapture(mode);
    }

    private void OnHotkeyAction(string action)
    {
        if (action == HotkeyActions.History)
        {
            OpenHistory();
            return;
        }
        CaptureMode? mode = action switch
        {
            HotkeyActions.Region => CaptureMode.Region,
            HotkeyActions.Fullscreen => CaptureMode.Fullscreen,
            HotkeyActions.AllMonitors => CaptureMode.AllMonitors,
            HotkeyActions.ActiveWindow => CaptureMode.ActiveWindow,
            HotkeyActions.WindowPick => CaptureMode.WindowPick,
            HotkeyActions.DelayRegion => CaptureMode.DelayRegion,
            HotkeyActions.LastRegion => CaptureMode.LastRegion,
            HotkeyActions.Scrolling => CaptureMode.Scrolling,
            _ => null,
        };
        if (mode is { } m) RunCapture(m);
        else Log.Warn($"Unknown hotkey action '{action}'.");
    }

    /// <summary>Tray delay submenu: countdown first (3/5/10 s), then a region capture.</summary>
    private void RunDelayedCapture(int seconds)
    {
        // The countdown pumps its own dispatcher frames; without the guard a hotkey could start a
        // nested region capture whose overlay outlives the countdown, and the countdown's own
        // capture would then be silently dropped by the re-entrancy check below.
        if (_captureInProgress)
        {
            Log.Warn("Delayed capture requested while another capture is in progress; ignored.");
            return;
        }
        _captureInProgress = true;
        try
        {
            if (CountdownWindow.Run(seconds, Services.GetRequiredService<MonitorService>()))
                RunCaptureCore(CaptureMode.Region);
            else
                Log.Info($"Delayed capture ({seconds}s) cancelled.");
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(Services.GetRequiredService<SettingsStore>(),
            Services.GetRequiredService<LocalizationService>(), _hotkeys,
            message => _tray?.ShowNotification(L.Get("Settings.Title"), message),
            RequestRestartAsAdmin,
            PreviewOobe);
        // A closing window already has IsLoaded == false; without the identity check its Closed
        // handler would null the field of the window that was just opened instead.
        _settingsWindow.Closed += (sender, _) =>
        {
            if (ReferenceEquals(sender, _settingsWindow)) _settingsWindow = null;
        };
        _settingsWindow.Opacity = 0; // fade-in preset: WindowFadeIn below brings it up right after Show
        _settingsWindow.Show();
        _settingsWindow.Activate();
        UiMotion.WindowFadeIn(_settingsWindow, 200); // occasional tier — settings opens far less often than the editor
        Log.Info("Settings window opened.");
    }

    private void ExitApp()
    {
        Log.Info("Exit requested from the tray menu.");
        _tray?.Dispose(); // remove the tray icon before shutdown so it never lingers
        Application.Current.Shutdown();
    }

    /// <summary>Relaunches the app elevated (UAC), handing off the single-instance guard. On success
    /// the current instance shuts down; on cancel/failure it keeps running. Wired to the settings UI.</summary>
    private void RequestRestartAsAdmin()
    {
        // Remove the tray icon first so it doesn't linger while the elevated instance starts.
        if (ElevationService.RestartAsAdmin(_singleInstance, OnForwardedArguments))
            _tray?.Dispose();
    }

    /// <summary>Diagnostics aid for headless UI verification: dumps the visual tree with element
    /// types, names and window-relative bounds (MSS_DUMP_TREE=1 alongside --render-*).</summary>
    private static void DumpVisualTree(Window win, string path)
    {
        var sb = new System.Text.StringBuilder();
        void Walk(DependencyObject node, int depth)
        {
            string line = new string(' ', depth * 2);
            if (node is FrameworkElement fe)
            {
                double left = 0, top = 0;
                try
                {
                    var t = fe.TransformToVisual(win);
                    var p = t.Transform(new Point(0, 0));
                    left = p.X; top = p.Y;
                }
                catch { /* not yet on the visual tree */ }
                line += $"{node.GetType().Name} Name={fe.Name} [{left:F0},{top:F0} {fe.ActualWidth:F0}x{fe.ActualHeight:F0}] Vis={fe.Visibility}";
            }
            else line += node.GetType().Name;
            sb.AppendLine(line);
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++) Walk(System.Windows.Media.VisualTreeHelper.GetChild(node, i), depth + 1);
        }
        Walk(win, 0);
        System.IO.File.WriteAllText(path, sb.ToString());
    }

    private void OnAppExitCleanup(object sender, ExitEventArgs e)
    {
        // Idempotent: the service provider also disposes these singletons afterwards.
        _tray?.Dispose();
        _hotkeys?.Dispose();
        _singleInstance?.Dispose();
    }

    private void RunCapture(CaptureMode mode)
    {
        // The overlay pumps a nested dispatcher frame, so hotkeys / tray clicks / forwarded
        // arguments can re-enter this method mid-capture; a second overlay would stack on the
        // first and its "frozen" frame would contain the first overlay's pixels.
        if (_captureInProgress)
        {
            Log.Warn($"Capture '{mode}' requested while another capture is in progress; ignored.");
            return;
        }
        _captureInProgress = true;
        try
        {
            RunCaptureCore(mode);
        }
        finally
        {
            _captureInProgress = false;
        }
    }

    /// <summary>Capture + dispatch. Callers must hold the <see cref="_captureInProgress"/> guard
    /// (RunCapture acquires it; the delayed-capture countdown holds it across the whole sequence).</summary>
    private void RunCaptureCore(CaptureMode mode)
    {
        try
        {
            var capture = Services.GetRequiredService<CaptureService>();
            var result = capture.Capture(mode);
            if (result is null)
            {
                Log.Info($"Capture '{mode}' was cancelled or produced no image.");
                return;
            }
            DispatchCaptureResult(result);
        }
        catch (Exception ex)
        {
            Log.Error($"Capture '{mode}' failed", ex);
            _tray?.ShowNotification(L.Get("Toast.CaptureFailed"), ex.Message);
        }
    }

    /// <summary>
    /// Post-capture routing. Every confirmed capture is recorded in history (original + document +
    /// thumbnail); then the action from the overlay toolbar or the settings is executed.
    /// Inline annotations drawn in the overlay travel with the result: the editor receives them as
    /// an editable document on top of the clean crop, while direct outputs (copy/save/pin) get them
    /// flattened into the pixels.
    /// </summary>
    private void DispatchCaptureResult(CaptureResult result)
    {
        var store = Services.GetRequiredService<SettingsStore>();
        var output = store.Current.Output;
        var action = result.RequestedAction
            ?? (result.Mode is CaptureMode.Region or CaptureMode.WindowPick
                ? output.AfterRegionCapture
                : output.AfterOtherCapture);
        Log.Info($"Dispatching {result.Mode} capture {result.Image.Width}x{result.Image.Height} via {action}.");

        var doc = result.AnnotationDocument ?? new AnnotationDocument
        {
            ImageWidth = result.Image.Width,
            ImageHeight = result.Image.Height,
            Effects = store.Current.Effects.Clone(),
            WindowTitle = result.WindowTitle,
            CaptureMode = result.Mode.ToString(),
        };
        Services.GetRequiredService<HistoryRecorder>().Record(result.Image, doc);

        switch (action)
        {
            case AfterCaptureAction.SaveOnly:
                QuickSave(result, store);
                break;
            case AfterCaptureAction.ShowToolbar:
                // The overlay's toolbar has already been shown and dismissed by a confirm without
                // an explicit choice (Enter / double-click / click-snap): that means the standard
                // edit outcome. In direct modes (fullscreen etc.) there is no overlay toolbar and
                // the editor is the follow-up surface — previously this fell through to Copy.
                OpenEditor(result, doc);
                break;
            case AfterCaptureAction.OpenEditor:
                OpenEditor(result, doc);
                break;
            case AfterCaptureAction.Pin:
                PinCapture(result);
                break;
            case AfterCaptureAction.OcrText:
                RunOcrCapture(result);
                break;
            case AfterCaptureAction.FloatingThumbnail:
                ShowFloatingThumbnail(result, doc);
                break;
            default: // CopyOnly
                CopyToClipboard(result);
                break;
        }
    }

    /// <summary>Overlay copy/save/pin flatten inline annotations into the pixels; without them the clean crop passes through.</summary>
    private static PixelBuffer ImageWithAnnotations(CaptureResult result)
    {
        var doc = result.AnnotationDocument;
        var image = result.Image;
        if (doc is not null && doc.Items.Count > 0)
        {
            try
            {
                image = AnnotationFlattener.Flatten(result.Image, doc);
            }
            catch (Exception ex)
            {
                Log.Error("Flattening the inline annotations failed; exporting without them.", ex);
                image = result.Image;
            }
        }
        return image;
    }

    private void OpenEditor(CaptureResult result, AnnotationDocument? document = null)
    {
        var editor = new EditorWindow(result, Services.GetRequiredService<SettingsStore>(),
            Services.GetRequiredService<ClipboardService>(), Services.GetRequiredService<ImageExporter>(),
            document,
            image => new PinWindow(image, Services.GetRequiredService<ImageExporter>(),
                Services.GetRequiredService<ClipboardService>(), OpenEditorForImage, OpenDefaultSaveFolder,
                Services.GetRequiredService<MonitorService>(),
                store: Services.GetRequiredService<SettingsStore>()),
            // size/position the editor on the monitor the capture came from (multi-monitor)
            Services.GetRequiredService<MonitorService>(),
            Services.GetRequiredService<OcrService>());
        editor.Closed += OnEditorClosed;
        EditorWindows.Add(editor);
        editor.Opacity = 0; // fade-in preset: WindowFadeIn below brings it up right after Show
        editor.Show();
        editor.Activate();
        UiMotion.WindowFadeIn(editor, 150); // short tier — the editor opens dozens of times a day
        Log.Info($"Editor opened for {result.Mode} capture ({result.Image.Width}x{result.Image.Height}).");
    }

    private void OpenEditorForImage(PixelBuffer image)
    {
        var result = new CaptureResult
        {
            Image = image,
            Mode = CaptureMode.Region,
            SourceRect = new PixelRect(0, 0, image.Width, image.Height),
        };
        OpenEditor(result);
    }

    private void PinCapture(CaptureResult result)
    {
        var pin = new PinWindow(ImageWithAnnotations(result), Services.GetRequiredService<ImageExporter>(),
            Services.GetRequiredService<ClipboardService>(), OpenEditorForImage, OpenDefaultSaveFolder,
            // size/position the pin on the monitor the capture came from (multi-monitor)
            Services.GetRequiredService<MonitorService>(), result.SourceRect,
            Services.GetRequiredService<SettingsStore>());
        pin.Closed += OnEditorClosed;
        EditorWindows.Add(pin); // same lifetime rule: keep the app alive while any floating window exists
        pin.Show();
        pin.Activate();
        // Popup entrance (PopIn scales around the pin's centre). Diagnostic renders are covered by
        // UiMotion.Suppress: the factory snaps opacity/scale to their final values instead.
        pin.RenderTransformOrigin = new Point(0.5, 0.5);
        UiMotion.PopIn(pin, fromScale: 0.96, ms: 150);
        Log.Info($"Pinned {result.Image.Width}x{result.Image.Height} to screen.");
    }

    /// <summary>
    /// macOS-style post-capture floating thumbnail. Shows a small card at the cursor monitor's
    /// bottom-right; click opens the editor, the right-click menu copies/saves/pins/closes, and
    /// ignoring it (auto-dismiss) runs the configured landing action (AutoSave &gt; AutoCopy &gt; none).
    /// Reuses the EditorWindows lifetime rule so the app stays alive while the card is visible.
    /// </summary>
    private void ShowFloatingThumbnail(CaptureResult result, AnnotationDocument? doc)
    {
        var store = Services.GetRequiredService<SettingsStore>();
        var output = store.Current.Output;
        var thumb = ImageWithAnnotations(result).ToBitmapSource();

        // Default landing action when the card is ignored: save, else copy, else nothing.
        Action? onLand =
            output.AutoSave ? () => QuickSave(result, store)
            : output.AutoCopy ? () => CopyToClipboard(result)
            : null;

        var window = new FloatingThumbnailWindow(
            thumb,
            Services.GetRequiredService<MonitorService>(),
            onEdit: () => OpenEditor(result, doc),
            onCopy: () => CopyToClipboard(result),
            onSave: () => QuickSave(result, store),
            onPin: () => PinCapture(result),
            onLand: onLand);
        window.Closed += OnEditorClosed;
        EditorWindows.Add(window); // same lifetime rule: keep the app alive while the card exists
        window.Show();
        Log.Info($"Floating thumbnail shown for {result.Mode} capture ({result.Image.Width}x{result.Image.Height}).");
    }

    private static string OpenDefaultSaveFolder()
    {
        try
        {
            var store = Services.GetRequiredService<SettingsStore>();
            string dir = string.IsNullOrWhiteSpace(store.Current.Output.SaveDirectory)
                ? AppPaths.DefaultSaveDir
                : store.Current.Output.SaveDirectory;
            System.IO.Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            return dir;
        }
        catch (Exception ex)
        {
            Log.Error("Opening the save folder failed", ex);
            return AppPaths.DefaultSaveDir;
        }
    }

    /// <summary>Opens the history window (tray menu / hotkey).</summary>
    private void OpenHistory()
    {
        var win = new HistoryWindow(Services.GetRequiredService<HistoryStore>(),
            Services.GetRequiredService<SettingsStore>(), Services.GetRequiredService<ClipboardService>(),
            (result, doc) => OpenEditor(result, doc));
        win.Closed += OnEditorClosed;
        EditorWindows.Add(win);
        win.Opacity = 0; // fade-in preset: WindowFadeIn below brings it up right after Show
        win.Show();
        win.Activate();
        UiMotion.WindowFadeIn(win, 200); // occasional tier — OpenHistory always builds a fresh instance, so this is a first show
        Log.Info("History window opened.");
    }

    /// <summary>Synthetic capture for the --show-editor diagnostic switch (UI verification without a real capture).
    /// Passes a document, matching the real post-capture flow (DispatchCaptureResult always opens with one).</summary>
    private void ShowDiagnosticEditor()
    {
        const int w = 960, h = 600;
        var image = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
        {
            var row = image.Row(y);
            for (int x = 0; x < w; x++)
            {
                int i = x * 4;
                row[i] = (byte)(x * 255 / w);          // B
                row[i + 1] = (byte)(y * 255 / h);      // G
                row[i + 2] = (byte)(160 - x * 80 / w); // R
                row[i + 3] = 255;
            }
        }
        var doc = new AnnotationDocument
        {
            ImageWidth = w,
            ImageHeight = h,
            WindowTitle = "Diagnostic",
            CaptureMode = CaptureMode.Region.ToString(),
        };
        // Sample items so UI verification renders populated content instead of an empty canvas.
        doc.Items.Add(new RectItem { Rect = new RectD(120, 100, 260, 160), StrokeColor = "#FF3B30", StrokeThickness = 4 });
        doc.Items.Add(new PenItem { Points = [new PointD(480, 120), new PointD(560, 220), new PointD(660, 160)], StrokeColor = "#0A84FF", StrokeThickness = 3 });
        doc.Items.Add(new TextItem { Position = new PointD(140, 330), Text = "Diagnostic 诊断 Aa", FontSize = 24, StrokeColor = "#FFFFFF" });
        doc.Items.Add(new StepItem { Center = new PointD(760, 420), Radius = 16, StrokeColor = "#FF9F0A", Number = 1 });
        OpenEditor(new CaptureResult
        {
            Image = image,
            Mode = CaptureMode.Region,
            WindowTitle = "Diagnostic",
            SourceRect = new PixelRect(0, 0, w, h),
        }, doc);
    }

    private void OnEditorClosed(object? sender, EventArgs e)    {
        if (sender is Window w) EditorWindows.Remove(w);
        if (EditorWindows.Count == 0 && !TrayStarted) Shutdown(0);
    }

    private static void CopyToClipboard(CaptureResult result)
    {
        if (Services.GetRequiredService<ClipboardService>().TryPutImage(ImageWithAnnotations(result)))
            Log.Info("Capture copied to clipboard.");
        else
            Log.Error($"Copying {result.Image.Width}x{result.Image.Height} to the clipboard failed after retries.");
    }

    private void QuickSave(CaptureResult result, SettingsStore store)
    {
        try
        {
            var path = Services.GetRequiredService<ImageExporter>().QuickSave(ImageWithAnnotations(result), result.WindowTitle, result.Mode.ToString());
            Log.Info($"Capture saved to {path}");
        }
        catch (Exception ex)
        {
            Log.Error("Quick save failed", ex);
        }
    }

    private static CaptureMode? ParseCaptureArg(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], "--capture", StringComparison.OrdinalIgnoreCase)) continue;
            var mode = (CaptureMode?)(args[i + 1].ToLowerInvariant() switch
            {
                "region" => CaptureMode.Region,
                "fullscreen" => CaptureMode.Fullscreen,
                "all" => CaptureMode.AllMonitors,
                "active" => CaptureMode.ActiveWindow,
                "window" => CaptureMode.WindowPick,
                "last" => CaptureMode.LastRegion,
                "scroll" => CaptureMode.Scrolling,
                "delay" => CaptureMode.DelayRegion,
                _ => null,
            });
            if (mode is not null) return mode;
            // Unrecognized value: keep scanning so a later valid --capture still wins.
        }
        return null;
    }
}
