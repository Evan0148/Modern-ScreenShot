using ModernScreenShot.Core.Imaging;

namespace ModernScreenShot.Core.Settings;

public enum CaptureMode { Region, Fullscreen, AllMonitors, ActiveWindow, WindowPick, LastRegion, Scrolling, DelayRegion }

public enum AfterCaptureAction { ShowToolbar, OpenEditor, CopyOnly, SaveOnly, Pin }

public enum ImageFormat { Png, Jpg, WebP }

public sealed class HotkeyBinding
{
    /// <summary>Win32 MOD_* flags: Alt=1, Ctrl=2, Shift=4, Win=8.</summary>
    public int Modifiers { get; set; }
    /// <summary>Win32 virtual key code; 0 = unassigned.</summary>
    public int VirtualKey { get; set; }

    public bool IsEmpty => VirtualKey == 0;
    public HotkeyBinding Clone() => (HotkeyBinding)MemberwiseClone();
}

public sealed class HotkeySettings
{
    public const int Alt = 1, Ctrl = 2, Shift = 4, Win = 8;

    public Dictionary<string, HotkeyBinding> Bindings { get; set; } = CreateDefaults();

    /// <summary>Keys are HotkeyActions constants.</summary>
    public static Dictionary<string, HotkeyBinding> CreateDefaults() => new()
    {
        [HotkeyActions.Region] = new() { Modifiers = Ctrl | Shift, VirtualKey = 'A' },
        [HotkeyActions.Fullscreen] = new() { Modifiers = Ctrl | Shift, VirtualKey = 'F' },
        [HotkeyActions.ActiveWindow] = new() { Modifiers = Ctrl | Shift, VirtualKey = 'W' },
        [HotkeyActions.WindowPick] = new() { Modifiers = Ctrl | Shift, VirtualKey = 'P' },
        [HotkeyActions.DelayRegion] = new() { Modifiers = Ctrl | Shift, VirtualKey = 'D' },
        [HotkeyActions.LastRegion] = new() { Modifiers = Ctrl | Shift, VirtualKey = 'R' },
        [HotkeyActions.Scrolling] = new() { Modifiers = Ctrl | Shift, VirtualKey = 'S' },
        [HotkeyActions.AllMonitors] = new(),
        [HotkeyActions.History] = new() { Modifiers = Ctrl | Shift, VirtualKey = 'H' },
    };
}

public static class HotkeyActions
{
    public const string Region = "Region";
    public const string Fullscreen = "Fullscreen";
    public const string AllMonitors = "AllMonitors";
    public const string ActiveWindow = "ActiveWindow";
    public const string WindowPick = "WindowPick";
    public const string DelayRegion = "DelayRegion";
    public const string LastRegion = "LastRegion";
    public const string Scrolling = "Scrolling";
    public const string History = "History";

    public static readonly string[] All = [Region, Fullscreen, AllMonitors, ActiveWindow, WindowPick, DelayRegion, LastRegion, Scrolling, History];
}

public sealed class OutputSettings
{
    /// <summary>Empty = Pictures\Modern-ScreenShot.</summary>
    public string SaveDirectory { get; set; } = "";
    public ImageFormat Format { get; set; } = ImageFormat.Png;
    public int JpgQuality { get; set; } = 92;
    public int WebPQuality { get; set; } = 90;
    public string FileNameTemplate { get; set; } = "Screenshot_{yyyy}-{MM}-{dd}_{HH}-{mm}-{ss}";
    public bool AutoSave { get; set; }
    public bool AutoCopy { get; set; } = true;
    public AfterCaptureAction AfterRegionCapture { get; set; } = AfterCaptureAction.ShowToolbar;
    public AfterCaptureAction AfterOtherCapture { get; set; } = AfterCaptureAction.OpenEditor;
    /// <summary>Apply the effect pipeline when copying/saving from the editor.</summary>
    public bool ApplyEffectsOnExport { get; set; } = true;
    public int Counter { get; set; } = 1;
}

public sealed class CaptureSettings
{
    public int DelaySeconds { get; set; } = 3;
    public bool ShowMagnifier { get; set; } = true;
    public bool CaptureCursor { get; set; }
    /// <summary>Mask Windows 11 rounded window corners as transparent in window captures.</summary>
    public bool WindowTransparentCorners { get; set; } = true;
    public bool PlaySound { get; set; }
    public int[]? LastRegion { get; set; }
    public int ScrollIntervalMs { get; set; } = 350;
    public int ScrollMaxHeight { get; set; } = 20000;
}

public sealed class EditorSettings
{
    public string StrokeColor { get; set; } = "#FFFF3B30";
    public double StrokeThickness { get; set; } = 4;
    public string FontFamily { get; set; } = "Microsoft YaHei UI";
    public double FontSize { get; set; } = 20;
    public int MosaicCellSize { get; set; } = 12;
    public int BlurRadius { get; set; } = 10;
    public double SpotlightDim { get; set; } = 0.6;
    public double MagnifierZoom { get; set; } = 2.5;
    public bool FillShape { get; set; }
    public bool DashedLine { get; set; }
    public bool FontBold { get; set; }
    public double StepRadius { get; set; } = 16;
    public bool MosaicPixelate { get; set; } = true;
    public List<string> Palette { get; set; } =
        ["#FFFF3B30", "#FFFF9500", "#FFFFCC00", "#FF34C759", "#FF007AFF", "#FF5856D6", "#FFAF52DE", "#FFFFFFFF", "#FF8E8E93", "#FF000000"];
}

public sealed class AppSettings
{
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>"zh-CN" or "en-US". Empty = follow system UI culture.</summary>
    public string Language { get; set; } = "";
    /// <summary>"System", "Light" or "Dark".</summary>
    public string Theme { get; set; } = "System";
    public bool StartWithWindows { get; set; }
    public bool FirstRunShown { get; set; }
    public HotkeySettings Hotkeys { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public CaptureSettings Capture { get; set; } = new();
    public EditorSettings Editor { get; set; } = new();
    /// <summary>Effect settings applied to new captures (last used).</summary>
    public EffectSettings Effects { get; set; } = BuiltInPresets.Clean().Settings;
    public List<EffectPreset> UserPresets { get; set; } = [];
    public int HistoryMaxCount { get; set; } = 200;
}
