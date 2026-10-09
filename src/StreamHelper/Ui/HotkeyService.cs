using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;
using System.Windows.Interop;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public sealed class HotkeyService : IDisposable
{
    private const int BaseId = 0x4F52;
    private readonly HwndSource _source;
    private readonly HashSet<HotkeyAction> _registered = new();

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("StreamHelperHotkey")
        {
            ParentWindow = NativeMethods.HwndMessage,
            Width = 0,
            Height = 0,
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public event Action<HotkeyAction>? Pressed;

    internal IntPtr Handle => _source.Handle;

    internal static int IdOf(HotkeyAction action) => BaseId + (int)action;

    public bool IsRegistered(HotkeyAction action) => _registered.Contains(action);

    public bool Register(HotkeyAction action, uint modifiers, uint virtualKey)
    {
        Unregister(action);
        if (virtualKey == 0) return false;
        if (!NativeMethods.RegisterHotKey(_source.Handle, IdOf(action), modifiers | NativeMethods.ModNoRepeat, virtualKey)) return false;
        _registered.Add(action);
        return true;
    }

    public void Unregister(HotkeyAction action)
    {
        if (!_registered.Remove(action)) return;
        NativeMethods.UnregisterHotKey(_source.Handle, IdOf(action));
    }

    public void UnregisterAll()
    {
        foreach (var action in _registered.ToArray()) Unregister(action);
    }

    public IReadOnlyList<HotkeyAction> RegisterAll(AppSettings settings)
    {
        var failed = new List<HotkeyAction>();
        foreach (var action in HotkeyActions.All)
        {
            var binding = settings.GetHotkey(action);
            if (!binding.IsSet)
            {
                Unregister(action);
                continue;
            }
            if (!Register(action, binding.Modifiers, binding.VirtualKey)) failed.Add(action);
        }
        return failed;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmHotkey)
        {
            var index = wParam.ToInt32() - BaseId;
            if (index >= 0 && Enum.IsDefined(typeof(HotkeyAction), index))
            {
                handled = true;
                Pressed?.Invoke((HotkeyAction)index);
            }
        }
        return IntPtr.Zero;
    }

    public static string Describe(uint modifiers, uint virtualKey)
    {
        if (virtualKey == 0) return "Не задано";
        var parts = new System.Collections.Generic.List<string>();
        if ((modifiers & AppSettings.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & AppSettings.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & AppSettings.ModShift) != 0) parts.Add("Shift");
        if ((modifiers & AppSettings.ModWin) != 0) parts.Add("Win");
        parts.Add(KeyName(KeyInterop.KeyFromVirtualKey((int)virtualKey)));
        return string.Join(" + ", parts);
    }

    public static string Describe(HotkeyBinding binding) => Describe(binding.Modifiers, binding.VirtualKey);

    private static string KeyName(Key key) => key switch
    {
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Escape => "Esc",
        Key.Space => "Space",
        Key.Oem3 => "` (тильда)",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        Key.OemOpenBrackets => "[",
        Key.Oem6 => "]",
        Key.Oem5 => "\\",
        Key.Oem1 => ";",
        Key.OemQuotes => "'",
        Key.OemComma => ",",
        Key.OemPeriod => ".",
        Key.OemQuestion => "/",
        Key.Snapshot => "PrintScreen",
        Key.Next => "PageDown",
        Key.Prior => "PageUp",
        Key.Scroll => "ScrollLock",
        Key.Capital => "CapsLock",
        Key.Add => "Num +",
        Key.Subtract => "Num -",
        Key.Multiply => "Num *",
        Key.Divide => "Num /",
        Key.Decimal => "Num .",
        >= Key.D0 and <= Key.D9 => ((int)key - (int)Key.D0).ToString(),
        >= Key.NumPad0 and <= Key.NumPad9 => "Num " + ((int)key - (int)Key.NumPad0),
        _ => key.ToString(),
    };
}
