using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public sealed class ToastService : IPositionable
{
    private readonly SettingsStore _settings;
    private readonly List<ToastWindow> _toasts = new();
    private ToastWindow? _positioning;

    public ToastService(SettingsStore settings) => _settings = settings;

    public bool IsPositioning => _positioning != null;

    public event Action<double>? ScaleChanged;

    public int Count => _toasts.Count;

    public void Show(string title, string line, string? message)
    {
        while (_toasts.Count >= ToastStack.MaxToasts)
        {
            var oldest = _toasts[0];
            _toasts.RemoveAt(0);
            oldest.CloseNow();
        }
        var toast = new ToastWindow(_settings.Current, title, line, message) { Stacked = true };
        toast.Closed += (_, _) =>
        {
            if (_toasts.Remove(toast)) Relayout();
        };
        _toasts.Add(toast);
        toast.Present(Relayout);
    }

    public void HideNow()
    {
        var all = _toasts.ToList();
        _toasts.Clear();
        foreach (var toast in all) toast.CloseNow();
    }

    private void Relayout()
    {
        if (_toasts.Count == 0) return;
        var work = SystemParameters.WorkArea;
        var workArea = new ScreenBounds(work.Left, work.Top, work.Right, work.Bottom);
        var screens = new ScreenBounds(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight);
        var heights = _toasts.Select(t => t.StackHeight).ToList();
        var settings = _settings.Current;
        var (left, top) = ToastPlacement.Resolve(settings.ToastLeft, settings.ToastTop, _toasts[0].Width, heights[0], workArea, screens);
        var tops = ToastStack.Tops(top, heights, workArea);
        for (var i = 0; i < _toasts.Count; i++) _toasts[i].MoveTo(left, tops[i]);
    }

    public void ShowSample() =>
        Show("Награда: Заказать трек", "Test_Viewer · 500 баллов", "[тест] Пример уведомления");

    public void BeginPositioning()
    {
        foreach (var toast in _toasts.ToList()) toast.Dismiss();
        EndPositioning();
        var window = new ToastWindow(
            _settings.Current, "Положение и размер", "Перетащи окно, потяни за край",
            "Потом нажми «Готово» в настройках.", positioning: true);
        window.Moved += RememberPosition;
        window.ScaleChanging += scale => ScaleChanged?.Invoke(scale);
        window.ResizeFinished += RememberGeometry;
        _positioning = window;
        window.Present();
    }

    public void EndPositioning()
    {
        _positioning?.Close();
        _positioning = null;
    }

    public void ApplyAppearance()
    {
        _positioning?.ApplyAppearance();
        foreach (var toast in _toasts) toast.ApplyAppearance();
        Relayout();
    }

    public void ResetPosition()
    {
        var settings = _settings.Current;
        settings.ToastLeft = null;
        settings.ToastTop = null;
        _settings.Save();
        _positioning?.MoveToConfiguredPlace();
    }

    public void Close()
    {
        HideNow();
        EndPositioning();
    }

    private void RememberPosition(double left, double top)
    {
        var settings = _settings.Current;
        settings.ToastLeft = left;
        settings.ToastTop = top;
        _settings.Save();
    }

    private void RememberGeometry(double scale, double left, double top)
    {
        var settings = _settings.Current;
        settings.ToastScale = ToastPlacement.ClampScale(scale);
        settings.ToastLeft = left;
        settings.ToastTop = top;
        _settings.Save();
    }
}
