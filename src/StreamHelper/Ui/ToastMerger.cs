using System;
using System.Collections.Generic;
using System.Linq;

namespace StreamHelper.Ui;

public interface IToastSink
{
    object Show(ToastText text);

    bool Update(object toast, ToastText text);
}

public sealed class ToastMerger
{
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(3);

    private readonly IToastSink _sink;
    private readonly Func<DateTime> _now;
    private readonly Dictionary<Type, (List<object> Items, object Toast, DateTime Last)> _open = new();

    public ToastMerger(IToastSink sink, Func<DateTime>? now = null)
    {
        _sink = sink;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public void Show<T>(IReadOnlyList<T> items, Func<IReadOnlyList<T>, ToastText> text) where T : class
    {
        if (items.Count == 0) return;
        var now = _now();
        if (_open.TryGetValue(typeof(T), out var open) && now - open.Last < Window)
        {
            var all = open.Items.Cast<T>().Concat(items).ToList();
            if (_sink.Update(open.Toast, text(all)))
            {
                _open[typeof(T)] = (all.Cast<object>().ToList(), open.Toast, now);
                return;
            }
        }
        var toast = _sink.Show(text(items));
        _open[typeof(T)] = (items.Cast<object>().ToList(), toast, now);
    }
}
