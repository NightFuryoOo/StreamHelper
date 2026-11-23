using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public interface IDaAlertChannel
{
    event Action<DaAlertEvent>? AlertCame;
    event Action<DaAlertShow>? AlertShown;
    bool IsConnected { get; }
    Task<DaCommandResult> SkipAsync(long alertId, string alertType);
    Task<DaCommandResult> RepeatAsync(long alertId, string alertType);
}

public sealed record DaQueuedAlert(long Id, string Type, string Name, DateTime ReceivedUtc);

public sealed class DaPauseState
{
    public bool Paused { get; set; }
    public DateTime? SinceUtc { get; set; }
    public List<DaQueuedAlert> Queue { get; set; } = new();
}

public enum DaPauseMode
{
    Off,
    Paused,
    Resuming,
}

public sealed record DaPauseResult(bool Success, string Message);

public sealed class DaAlertPause
{
    public const string FileName = "da-pause.json";
    public const string NoChannel = "Пауза не поставлена: нет связи с виджетом DA. Нужна ссылка виджета на плитке DonationAlerts в «Подключениях».";
    public static readonly TimeSpan Recent = TimeSpan.FromMinutes(20);
    public static readonly IReadOnlySet<string> PausedTypes = new HashSet<string>(StringComparer.Ordinal) { DaMediaFeed.SubAlertType, DaMediaFeed.GiftAlertType };

    private static readonly TimeSpan ShowGrace = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IDaAlertChannel _channel;
    private readonly string _file;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private readonly List<Seen> _seen = new();
    private readonly HashSet<(long Id, string Type)> _ourSkips = new();
    private DaPauseState _state;
    private DaPauseMode _mode;
    private CancellationTokenSource? _resume;
    private Awaited? _awaited;

    internal TimeSpan StartWait { get; set; } = TimeSpan.FromSeconds(30);

    public DaAlertPause(IDaAlertChannel channel, string file, Func<DateTime>? now = null)
    {
        _channel = channel;
        _file = file;
        _now = now ?? (() => DateTime.UtcNow);
        _state = Load(file);
        _mode = _state.Paused ? DaPauseMode.Paused : DaPauseMode.Off;
        channel.AlertCame += OnCame;
        channel.AlertShown += OnShown;
    }

    public event Action? Changed;

    public event Action<string>? Failed;

    public DaPauseMode Mode
    {
        get
        {
            lock (_gate) return _mode;
        }
    }

    public IReadOnlyList<DaQueuedAlert> Queue
    {
        get
        {
            lock (_gate) return _state.Queue.ToList();
        }
    }

    public string MenuText
    {
        get
        {
            lock (_gate)
            {
                if (_mode != DaPauseMode.Paused) return "Пауза подписок DA";
                return "Продолжить подписки DA" + (_state.Queue.Count > 0 ? $" (ждут: {_state.Queue.Count})" : "");
            }
        }
    }

    public string? StartupNotice
    {
        get
        {
            lock (_gate)
            {
                if (_mode != DaPauseMode.Paused) return null;
                return "Подписки DA всё ещё на паузе с прошлого запуска." + Waits(_state.Queue.Count);
            }
        }
    }

    public static string PausedText(int waiting) => "Подписки DA на паузе." + Waits(waiting);

    public static string ResumedText(int waiting) =>
        waiting > 0 ? $"Подписки DA снова идут. Ждавшие покажу по очереди: {waiting}." : "Подписки DA снова идут.";

    private static string Waits(int count) => count > 0 ? $" Ждут: {count}." : "";

    public DaPauseResult Toggle() => Mode == DaPauseMode.Paused ? Resume() : Pause();

    private DaPauseResult Pause()
    {
        if (!_channel.IsConnected) return new DaPauseResult(false, NoChannel);
        List<DaQueuedAlert> inWidget;
        int waiting;
        lock (_gate)
        {
            StopResume();
            var now = _now();
            inWidget = Pending(now);
            var queue = new List<DaQueuedAlert>();
            Merge(queue, inWidget);
            Merge(queue, _state.Queue);
            _state = new DaPauseState { Paused = true, SinceUtc = now, Queue = queue };
            _mode = DaPauseMode.Paused;
            _ourSkips.Clear();
            foreach (var alert in inWidget) _ourSkips.Add((alert.Id, alert.Type));
            waiting = queue.Count;
            Save();
        }
        foreach (var alert in inWidget) Skip(alert.Id, alert.Type);
        Log.Write($"DA subs paused: {waiting} waiting, {inWidget.Count} taken off the widget.");
        Changed?.Invoke();
        return new DaPauseResult(true, PausedText(waiting));
    }

    private DaPauseResult Resume()
    {
        int waiting;
        lock (_gate)
        {
            _state.Paused = false;
            _state.SinceUtc = null;
            _ourSkips.Clear();
            waiting = _state.Queue.Count;
            if (waiting == 0) _mode = DaPauseMode.Off;
            else
            {
                _mode = DaPauseMode.Resuming;
                var cts = new CancellationTokenSource();
                _resume = cts;
                _ = Task.Run(() => RepeatQueueAsync(cts.Token));
            }
            Save();
        }
        Log.Write($"DA subs resumed: {waiting} to repeat.");
        Changed?.Invoke();
        return new DaPauseResult(true, ResumedText(waiting));
    }

    private async Task RepeatQueueAsync(CancellationToken ct)
    {
        try
        {
            while (true)
            {
                DaQueuedAlert next;
                Awaited awaited;
                lock (_gate)
                {
                    if (ct.IsCancellationRequested) return;
                    if (_state.Queue.Count == 0)
                    {
                        _mode = DaPauseMode.Off;
                        _resume = null;
                        Save();
                        break;
                    }
                    next = _state.Queue[0];
                    awaited = new Awaited(next.Id, next.Type);
                    _awaited = awaited;
                }
                var result = await _channel.RepeatAsync(next.Id, next.Type);
                if (!result.Success)
                {
                    int left;
                    lock (_gate)
                    {
                        if (ct.IsCancellationRequested) return;
                        _mode = DaPauseMode.Off;
                        _resume = null;
                        _awaited = null;
                        left = _state.Queue.Count;
                        Save();
                    }
                    Log.Write($"DA subs: repeating alert {next.Id} failed, {left} left.");
                    Changed?.Invoke();
                    Failed?.Invoke($"Не получилось продолжить подписки DA. {result.Message} Не показано: {left}.");
                    return;
                }
                await Task.WhenAny(awaited.Done.Task, Task.Delay(StartWait, ct));
                lock (_gate)
                {
                    if (ct.IsCancellationRequested) return;
                    _awaited = null;
                    if (_state.Queue.Count > 0 && _state.Queue[0] == next) _state.Queue.RemoveAt(0);
                    Save();
                }
                Changed?.Invoke();
            }
            Log.Write("DA subs: all paused alerts were repeated.");
            Changed?.Invoke();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void StopResume()
    {
        _resume?.Cancel();
        _resume = null;
        _awaited = null;
    }

    private void Skip(long id, string type) => _ = SkipQuietlyAsync(id, type);

    private async Task SkipQuietlyAsync(long id, string type)
    {
        try
        {
            var result = await _channel.SkipAsync(id, type);
            if (!result.Success) Log.Write($"DA subs pause: skipping alert {id} answered: {result.Message}");
        }
        catch (Exception ex)
        {
            Log.Write($"DA subs pause: skipping alert {id} failed: {ex.Message}");
        }
    }

    private void OnCame(DaAlertEvent alert)
    {
        if (alert.Shown) return;
        var changed = false;
        var skip = false;
        lock (_gate)
        {
            var now = _now();
            _seen.RemoveAll(s => now - s.ReceivedUtc > Recent);
            var known = _seen.FindLastIndex(s => s.Id == alert.Id && s.Type == alert.Type);
            if (known < 0 || _seen[known].Done)
            {
                if (known >= 0) _seen.RemoveAt(known);
                _seen.Add(new Seen(alert.Id, alert.Type, NameOf(alert), now));
            }
            if (_mode == DaPauseMode.Paused && PausedTypes.Contains(alert.Type))
            {
                if (!_state.Queue.Any(q => q.Id == alert.Id && q.Type == alert.Type))
                {
                    _state.Queue.Add(new DaQueuedAlert(alert.Id, alert.Type, NameOf(alert), now));
                    Save();
                    changed = true;
                }
                _ourSkips.Add((alert.Id, alert.Type));
                skip = true;
            }
        }
        if (skip) Skip(alert.Id, alert.Type);
        if (changed) Changed?.Invoke();
    }

    private void OnShown(DaAlertShow show)
    {
        var changed = false;
        var skipAgain = false;
        TaskCompletionSource? done = null;
        lock (_gate)
        {
            var now = _now();
            var seen = _seen.FindLast(s => s.Id == show.Id && s.Type == show.Type);
            if (seen != null)
            {
                if (show.Action == "start")
                {
                    seen.StartedUtc = now;
                    seen.ShowUntilUtc = now + TimeSpan.FromMilliseconds(Math.Max(0, show.DurationMs)) + ShowGrace;
                }
                else if (show.Action is "end" or "skip") seen.Done = true;
            }
            var key = (show.Id, show.Type);
            if (_awaited is { } awaited && awaited.Id == show.Id && awaited.Type == show.Type) done = awaited.Done;
            else if (_mode == DaPauseMode.Paused && PausedTypes.Contains(show.Type))
            {
                if (show.Action == "start")
                {
                    if (!_state.Queue.Any(q => q.Id == show.Id && q.Type == show.Type))
                    {
                        _state.Queue.Insert(0, new DaQueuedAlert(show.Id, show.Type, seen?.Name ?? "", now));
                        changed = true;
                    }
                    _ourSkips.Add(key);
                    skipAgain = true;
                }
                else if (show.Action == "skip" && !_ourSkips.Remove(key) || show.Action == "end")
                {
                    changed = _state.Queue.RemoveAll(q => q.Id == show.Id && q.Type == show.Type) > 0;
                }
                if (changed) Save();
            }
            else if (_mode == DaPauseMode.Resuming && show.Action == "skip")
            {
                changed = _state.Queue.RemoveAll(q => q.Id == show.Id && q.Type == show.Type) > 0;
                if (changed) Save();
            }
        }
        done?.TrySetResult();
        if (skipAgain) Skip(show.Id, show.Type);
        if (changed) Changed?.Invoke();
    }

    private List<DaQueuedAlert> Pending(DateTime now)
    {
        var showing = _seen.Where(s => !s.Done && s.StartedUtc != null && now < s.ShowUntilUtc);
        var waiting = _seen.Where(s => !s.Done && s.StartedUtc == null && now - s.ReceivedUtc <= Recent);
        return showing.Concat(waiting).Where(s => PausedTypes.Contains(s.Type)).Select(s => new DaQueuedAlert(s.Id, s.Type, s.Name, s.ReceivedUtc)).ToList();
    }

    private static void Merge(List<DaQueuedAlert> queue, IEnumerable<DaQueuedAlert> more)
    {
        foreach (var alert in more)
        {
            if (!queue.Any(q => q.Id == alert.Id && q.Type == alert.Type)) queue.Add(alert);
        }
    }

    private static string NameOf(DaAlertEvent alert) => alert.Name.Length > 0 ? alert.Name : alert.Username;

    private static DaPauseState Load(string file)
    {
        try
        {
            if (File.Exists(file)) return JsonSerializer.Deserialize<DaPauseState>(File.ReadAllText(file)) ?? new DaPauseState();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Write("Reading the DA pause state failed: " + ex.Message);
        }
        return new DaPauseState();
    }

    private void Save()
    {
        try
        {
            if (!_state.Paused && _state.Queue.Count == 0)
            {
                if (File.Exists(_file)) File.Delete(_file);
                return;
            }
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_state, Json));
            File.Move(tmp, _file, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Saving the DA pause state failed: " + ex.Message);
        }
    }

    private sealed class Seen
    {
        public Seen(long id, string type, string name, DateTime receivedUtc)
        {
            Id = id;
            Type = type;
            Name = name;
            ReceivedUtc = receivedUtc;
        }

        public long Id { get; }
        public string Type { get; }
        public string Name { get; }
        public DateTime ReceivedUtc { get; }
        public DateTime? StartedUtc { get; set; }
        public DateTime ShowUntilUtc { get; set; }
        public bool Done { get; set; }
    }

    private sealed class Awaited
    {
        public Awaited(long id, string type)
        {
            Id = id;
            Type = type;
        }

        public long Id { get; }
        public string Type { get; }
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
