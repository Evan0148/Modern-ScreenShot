using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ModernScreenShot.App.Localization;
using ModernScreenShot.Core.Settings;
using L = ModernScreenShot.App.Localization.LocalizationService;

namespace ModernScreenShot.App.Shell;

/// <summary>
/// Button that records a hotkey: click to start listening, press any modifier+key combination to
/// capture it, press Esc (or move focus away) to leave. Esc while listening clears the binding.
/// The display uses "Ctrl+Shift+A" style names, which are intentionally not localized.
/// </summary>
public sealed class HotkeyRecorder : Button
{
    private bool _listening;
    private HotkeyBinding _binding = new();

    /// <summary>Raised after the binding changed (captured or cleared).</summary>
    public event EventHandler? BindingChanged;

    public HotkeyRecorder()
    {
        MinWidth = 140;
        HorizontalContentAlignment = HorizontalAlignment.Center;
        Cursor = Cursors.Hand;
        ToolTip = L.Get("Settings.PressKeys");
        Click += (_, _) => BeginListening();
        PreviewLostKeyboardFocus += (_, _) => StopListening();
        Refresh();
    }

    public HotkeyBinding Binding
    {
        get => _binding.Clone();
        set
        {
            _binding = value.Clone();
            Refresh();
        }
    }

    /// <summary>Re-renders the current state (also used on language switches).</summary>
    public void Refresh() => Content = _listening
        ? L.Get("Settings.PressKeys")
        : _binding.IsEmpty ? L.Get("Common.None") : HotkeyService.Format(_binding);

    private void BeginListening()
    {
        if (_listening) return;
        _listening = true;
        Refresh();
        Focus();
    }

    private void StopListening()
    {
        if (!_listening) return;
        _listening = false;
        Refresh();
    }

    private void Capture(HotkeyBinding binding)
    {
        _binding = binding;
        StopListening();
        BindingChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (!_listening)
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        e.Handled = true;
        // Alt-combinations arrive as Key.System with the real key in SystemKey.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                // Esc clears the binding entirely (documented behavior).
                Capture(new HotkeyBinding());
                return;
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                 or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin:
                // Only modifiers so far: live preview, still waiting for the main key.
                Content = PreviewText(Keyboard.Modifiers);
                return;
            case Key.None:
                return;
            default:
            {
                int vk = KeyInterop.VirtualKeyFromKey(key);
                if (vk == 0) return;
                int modifiers = ToWin32Modifiers(Keyboard.Modifiers);
                if (modifiers == 0)
                {
                    // A bare key would be swallowed system-wide once registered; keep listening.
                    Content = PreviewText(Keyboard.Modifiers);
                    return;
                }
                Capture(new HotkeyBinding { Modifiers = modifiers, VirtualKey = vk });
                return;
            }
        }
    }

    private static int ToWin32Modifiers(ModifierKeys modifiers)
    {
        int m = 0;
        if (modifiers.HasFlag(ModifierKeys.Alt)) m |= HotkeySettings.Alt;
        if (modifiers.HasFlag(ModifierKeys.Control)) m |= HotkeySettings.Ctrl;
        if (modifiers.HasFlag(ModifierKeys.Shift)) m |= HotkeySettings.Shift;
        if (modifiers.HasFlag(ModifierKeys.Windows)) m |= HotkeySettings.Win;
        return m;
    }

    private static string PreviewText(ModifierKeys modifiers)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add("…");
        return string.Join("+", parts);
    }
}
