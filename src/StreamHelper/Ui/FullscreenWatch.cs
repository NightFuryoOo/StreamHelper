using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Threading;

namespace StreamHelper.Ui;

internal sealed class FullscreenWatch
{
    public static readonly TimeSpan[] RecheckAfter = { TimeSpan.FromMilliseconds(400), TimeSpan.FromSeconds(2) };

    private static readonly HashSet<string> ShellClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "MultitaskingViewFrame", "XamlExplorerHostIslandWindow",
        "ForegroundStaging", "Windows.UI.Core.CoreWindow", "LockScreenBackstopFrame",
    };

    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly uint _ownProcess = (uint)Environment.ProcessId;
    private IntPtr _front;

    public FullscreenWatch() => _poll.Tick += (_, _) => Check();

    public event Action<IntPtr>? GameInFront;

    public void Start() => _poll.Start();

    public void Stop() => _poll.Stop();

    public static bool CoversMonitor(NativeMethods.Rect window, NativeMethods.Rect monitor) =>
        window.Left <= monitor.Left && window.Top <= monitor.Top && window.Right >= monitor.Right && window.Bottom >= monitor.Bottom;

    public static bool IsShell(string windowClass) => ShellClasses.Contains(windowClass);

    public static void ShowAgainOnTop(Window window, IntPtr handle)
    {
        window.Hide();
        window.Show();
        NativeMethods.KeepOnTop(handle);
    }

    private void Check()
    {
        var front = NativeMethods.GetForegroundWindow();
        if (front == _front) return;
        _front = front;
        if (!IsGame(front)) return;
        foreach (var delay in RecheckAfter)
        {
            var timer = new DispatcherTimer { Interval = delay };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                if (NativeMethods.GetForegroundWindow() == front) GameInFront?.Invoke(front);
            };
            timer.Start();
        }
    }

    private bool IsGame(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || NativeMethods.ProcessOf(hwnd) == _ownProcess || IsShell(NativeMethods.ClassOf(hwnd))) return false;
        return NativeMethods.Placement(hwnd) is { } placement && CoversMonitor(placement.Window, placement.Monitor);
    }
}
