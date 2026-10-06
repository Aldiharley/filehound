using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace FileHound.App.Services;

/// <summary>A global hotkey such as "Ctrl+Alt+Space".</summary>
public readonly record struct HotkeyGesture(ModifierKeys Modifiers, Key Key)
{
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; continue;
                case "alt": mods |= ModifierKeys.Alt; continue;
                case "shift": mods |= ModifierKeys.Shift; continue;
                case "win" or "windows": mods |= ModifierKeys.Windows; continue;
            }
            if (key is not null) return false;
            if (raw.Length == 1 && char.IsAsciiDigit(raw[0])) key = Key.D0 + (raw[0] - '0');
            else if (Enum.TryParse<Key>(raw, ignoreCase: true, out var k)) key = k;
            else return false;
        }
        if (key is null || mods == ModifierKeys.None || IsModifierKey(key.Value)) return false;
        gesture = new HotkeyGesture(mods, key.Value);
        return true;
    }

    public static bool IsModifierKey(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(Key is >= Key.D0 and <= Key.D9 ? ((int)(Key - Key.D0)).ToString() : Key.ToString());
        return string.Join("+", parts);
    }
}

/// <summary>Registers one system-wide hotkey on a window and raises <see cref="Pressed"/>.</summary>
public sealed class HotkeyService : IDisposable
{
    private const int HotkeyId = 0xB001;
    private HwndSource? _source;
    private bool _registered;

    public event EventHandler? Pressed;

    public void Attach(Window window)
    {
        var handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source.AddHook(WndProc);
    }

    /// <summary>Registers (replacing any previous) hotkey. Returns false when another app owns the combination.</summary>
    public bool Register(HotkeyGesture gesture)
    {
        if (_source is null) return false;
        Unregister();
        uint mods = NativeMethods.MOD_NOREPEAT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Control)) mods |= NativeMethods.MOD_CONTROL;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Alt)) mods |= NativeMethods.MOD_ALT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Shift)) mods |= NativeMethods.MOD_SHIFT;
        if (gesture.Modifiers.HasFlag(ModifierKeys.Windows)) mods |= NativeMethods.MOD_WIN;
        uint vk = (uint)KeyInterop.VirtualKeyFromKey(gesture.Key);
        _registered = NativeMethods.RegisterHotKey(_source.Handle, HotkeyId, mods, vk);
        if (!_registered) Log.Warn($"Hotkey {gesture} could not be registered (error {Marshal.GetLastPInvokeError()})");
        return _registered;
    }

    public void Unregister()
    {
        if (_registered && _source is not null) NativeMethods.UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
    }

    private nint WndProc(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && wParam == HotkeyId)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return 0;
    }

    public void Dispose()
    {
        Unregister();
        _source?.RemoveHook(WndProc);
    }
}
