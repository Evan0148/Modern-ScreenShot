using System.Windows.Input;
using System.Windows.Interop;
using ModernScreenShot.App.Interop;
using ModernScreenShot.App.Services;
using ModernScreenShot.Core.Settings;

namespace ModernScreenShot.App.Shell;

/// <summary>
/// Registers the global hotkeys configured in settings on a hidden message-only window and raises
/// <see cref="HotkeyPressed"/> on the UI thread. Hotkeys that could not be registered (already in
/// use by another application) are exposed as conflicts for the settings window.
/// The constructor is side-effect free; call <see cref="Start"/> to create the window and register.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly Dictionary<int, string> _registered = [];
    private readonly List<string> _failedActions = [];
    private HwndSource? _source;
    private bool _disposed;

    /// <summary>Action name (HotkeyActions constant) of a pressed hotkey, raised on the UI thread.</summary>
    public event Action<string>? HotkeyPressed;

    public HotkeyService(SettingsStore settings) => _settings = settings;

    /// <summary>Actions whose last registration attempt failed (occupied by another application).</summary>
    public IReadOnlyList<string> FailedActions => _failedActions;

    /// <summary>
    /// Unregisters every hotkey so the keyboard input reaches capture UIs (the hotkey edit dialog)
    /// instead of being swallowed by RegisterHotKey system-wide. Every Suspend must be paired with
    /// a <see cref="Resume"/>; until then the app's global hotkeys are inert.
    /// </summary>
    public void Suspend()
    {
        if (_disposed) return;
        UnregisterAll();
        Log.Info("Hotkeys suspended: keyboard capture UI is open.");
    }

    /// <summary>Re-registers the configured hotkeys after <see cref="Suspend"/>.</summary>
    public void Resume()
    {
        if (_disposed) return;
        ReRegister();
        Log.Info("Hotkeys resumed after capture UI closed.");
    }

    /// <summary>Creates the message-only window and registers every configured hotkey.</summary>
    public void Start()
    {
        if (_disposed) return;
        if (_source is not null) return;
        var parameters = new HwndSourceParameters("ModernScreenShot Hotkeys")
        {
            Width = 0,
            Height = 0,
            PositionX = 0,
            PositionY = 0,
            WindowStyle = 0,
            ExtendedWindowStyle = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE: invisible helper window
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
        ReRegister();
    }

    /// <summary>
    /// Unregisters everything and re-registers the current bindings (after settings changes).
    /// Returns the actions that could not be registered.
    /// </summary>
    public IReadOnlyList<string> ReRegister()
    {
        if (_disposed || _source is null)
        {
            Log.Warn("Hotkey (re-)registration skipped: the hotkey window is not running.");
            return _failedActions;
        }

        UnregisterAll();
        _failedActions.Clear();
        int id = 1;
        foreach (var action in HotkeyActions.All)
        {
            if (!_settings.Current.Hotkeys.Bindings.TryGetValue(action, out var binding) || binding.IsEmpty) continue;
            uint modifiers = (uint)binding.Modifiers | NativeMethods.MOD_NOREPEAT;
            uint virtualKey = (uint)binding.VirtualKey;
            if (NativeMethods.RegisterHotKey(_source.Handle, id, modifiers, virtualKey))
            {
                _registered[id] = action;
            }
            else
            {
                _failedActions.Add(action);
                Log.Warn($"Hotkey '{action}' ({Format(binding)}) could not be registered; it is likely in use by another application.");
            }
            id++;
        }
        Log.Info($"Hotkeys registered: {_registered.Count} ok, {_failedActions.Count} failed.");
        return _failedActions;
    }

    /// <summary>Formats a binding like "Ctrl+Shift+A" for display; empty string when unassigned.</summary>
    public static string Format(HotkeyBinding binding)
    {
        if (binding.IsEmpty) return "";
        var parts = new List<string>(5);
        if ((binding.Modifiers & HotkeySettings.Ctrl) != 0) parts.Add("Ctrl");
        if ((binding.Modifiers & HotkeySettings.Shift) != 0) parts.Add("Shift");
        if ((binding.Modifiers & HotkeySettings.Alt) != 0) parts.Add("Alt");
        if ((binding.Modifiers & HotkeySettings.Win) != 0) parts.Add("Win");
        parts.Add(VirtualKeyName(binding.VirtualKey));
        return string.Join("+", parts);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY)
        {
            handled = true;
            int id = wParam.ToInt32();
            if (_registered.TryGetValue(id, out var action))
            {
                Log.Info($"Hotkey pressed: {action}");
                HotkeyPressed?.Invoke(action);
            }
        }
        return IntPtr.Zero;
    }

    private void UnregisterAll()
    {
        if (_source is null) return;
        foreach (var id in _registered.Keys)
        {
            if (!NativeMethods.UnregisterHotKey(_source.Handle, id))
                Log.Warn($"UnregisterHotKey failed for id {id}.");
        }
        _registered.Clear();
    }

    /// <summary>Display name ("Ctrl", "A", "F5", "Num1", ...) for a Win32 virtual key code.</summary>
    public static string VirtualKeyName(int vk)
    {
        switch (vk)
        {
            case >= 0x30 and <= 0x39: // 0-9
            case >= 0x41 and <= 0x5A: // A-Z
                return ((char)vk).ToString();
            case >= 0x70 and <= 0x87: return $"F{vk - 0x70 + 1}";
            case 0x08: return "Back";
            case 0x09: return "Tab";
            case 0x0D: return "Enter";
            case 0x13: return "Pause";
            case 0x14: return "CapsLock";
            case 0x1B: return "Esc";
            case 0x20: return "Space";
            case 0x21: return "PgUp";
            case 0x22: return "PgDn";
            case 0x23: return "End";
            case 0x24: return "Home";
            case 0x25: return "Left";
            case 0x26: return "Up";
            case 0x27: return "Right";
            case 0x28: return "Down";
            case 0x2C: return "PrtSc";
            case 0x2D: return "Ins";
            case 0x2E: return "Del";
            case >= 0x60 and <= 0x69: return $"Num{vk - 0x60}";
            // Numpad operators: the operator glyph is the name the user pressed — the old
            // "Num1".."Num6" mapping displayed a different key than the one on the keyboard.
            case 0x6A: return "Num*";
            case 0x6B: return "Num+";
            case 0x6D: return "Num-";
            case 0x6E: return "Num.";
            case 0x6F: return "Num/";
            default:
                try
                {
                    var key = KeyInterop.KeyFromVirtualKey(vk);
                    if (key != Key.None) return key.ToString();
                }
                catch (InvalidOperationException)
                {
                    // fall through to hex
                }
                return $"0x{vk:X2}";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UnregisterAll();
        _source?.Dispose();
        _source = null;
        Log.Info("Hotkey service disposed.");
    }
}
