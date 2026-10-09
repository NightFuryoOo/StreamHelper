using System;
using System.Runtime.InteropServices;

namespace StreamHelper.Ui;

internal static class NativeMethods
{
    public const int GwlExStyle = -20;
    public const int WsExTransparent = 0x00000020;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExNoActivate = 0x08000000;

    public const int WmHotkey = 0x0312;
    public const int WmMouseActivate = 0x0021;
    public const int MaActivate = 1;
    public const int MaNoActivate = 3;
    public const uint ModNoRepeat = 0x4000;

    public static readonly IntPtr HwndTopmost = new(-1);
    public static readonly IntPtr HwndMessage = new(-3);

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out Point point);

    public static (int X, int Y) CursorPixels() => GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    public static bool Exists(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    public static void RemoveExStyle(IntPtr hwnd, int style) =>
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) & ~style);

    public static void AddExStyle(IntPtr hwnd, int style) =>
        SetWindowLong(hwnd, GwlExStyle, GetWindowLong(hwnd, GwlExStyle) | style);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DwmUseImmersiveDarkModeOld = 19;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    public static void UseDarkTitleBar(IntPtr hwnd, int captionColor, int textColor, int borderColor)
    {
        try
        {
            var dark = 1;
            if (DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(hwnd, DwmUseImmersiveDarkModeOld, ref dark, sizeof(int));
            }
            DwmSetWindowAttribute(hwnd, DwmCaptionColor, ref captionColor, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmTextColor, ref textColor, sizeof(int));
            DwmSetWindowAttribute(hwnd, DwmBorderColor, ref borderColor, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
        }
    }

    public static void KeepOnTop(IntPtr hwnd) =>
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
}
