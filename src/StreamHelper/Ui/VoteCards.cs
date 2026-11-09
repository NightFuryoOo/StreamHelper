using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using StreamHelper.Models;
using StreamHelper.Sync;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public static class VoteCardText
{
    public static string Header(ChannelVote vote) => (vote.Kind == VoteKind.Poll ? "Опрос" : "Предикт") + ": " + vote.Title;

    public static string Status(ChannelVote vote, DateTime now)
    {
        switch (vote.Stage)
        {
            case VoteStage.Active:
                if (vote.EndsUtc is not { } ends) return "";
                var left = ends - now;
                if (left <= TimeSpan.Zero) return vote.Kind == VoteKind.Poll ? "подводим итог…" : "ставки закрываются…";
                return (vote.Kind == VoteKind.Poll ? "осталось " : "ставки ещё ") + Clock(left);
            case VoteStage.Locked:
                return "Ставки закрыты, выбери победителя";
            default:
                return vote.IsCanceled ? "Отменён, баллы возвращены" : "Итог";
        }
    }

    public static string Clock(TimeSpan left)
    {
        var seconds = (int)Math.Ceiling(left.TotalSeconds);
        return $"{seconds / 60}:{seconds % 60:00}";
    }

    public static IReadOnlyList<int> Percents(ChannelVote vote)
    {
        var weights = vote.Options.Select(o => vote.Kind == VoteKind.Poll ? o.Votes : o.Points).ToList();
        var total = weights.Sum();
        return weights.Select(w => total <= 0 ? 0 : (int)Math.Round(w * 100.0 / total, MidpointRounding.AwayFromZero)).ToList();
    }

    public static string Detail(ChannelVote vote, VoteOption option, int percent) =>
        vote.Kind == VoteKind.Poll
            ? $"{option.Votes} {Plural(option.Votes, "голос", "голоса", "голосов")} · {percent}%"
            : $"{Redemption.FormatCost(option.Points)} · {option.Users} чел. · {percent}%";

    public static IReadOnlySet<string> Winners(ChannelVote vote)
    {
        if (vote.Stage != VoteStage.Ended || vote.IsCanceled) return new HashSet<string>();
        if (vote.Kind == VoteKind.Prediction) return vote.WinnerId is { } id ? new HashSet<string> { id } : new HashSet<string>();
        var best = vote.Options.Count == 0 ? 0 : vote.Options.Max(o => o.Votes);
        return best <= 0 ? new HashSet<string>() : vote.Options.Where(o => o.Votes == best).Select(o => o.Id).ToHashSet();
    }

    public static string Leader(ChannelVote vote)
    {
        if (vote.Options.Count == 0) return "";
        var percents = Percents(vote);
        var winners = Winners(vote);
        if (vote.Stage == VoteStage.Ended && winners.Count == 0) return "";
        var best = 0;
        for (var i = 1; i < vote.Options.Count; i++)
        {
            if (winners.Count > 0 ? winners.Contains(vote.Options[i].Id) && !winners.Contains(vote.Options[best].Id) : percents[i] > percents[best]) best = i;
        }
        if (winners.Count == 0 && percents[best] == 0) return "";
        return $"{(winners.Count > 0 ? "✓ " : "")}{vote.Options[best].Title} {percents[best]}%";
    }

    public static string ShortStatus(ChannelVote vote, DateTime now)
    {
        switch (vote.Stage)
        {
            case VoteStage.Active:
                if (vote.EndsUtc is not { } ends) return "";
                var left = ends - now;
                return left <= TimeSpan.Zero ? "…" : Clock(left);
            case VoteStage.Locked:
                return "ставки закрыты";
            default:
                return vote.IsCanceled ? "отменён" : "итог";
        }
    }

    public static bool StillShown(ChannelVote? vote, DateTime now) =>
        vote != null && (vote.Stage != VoteStage.Ended || (vote.EndedUtc is { } ended && now - ended < VoteWatcher.ShowEndedFor));

    public static string Plural(long n, string one, string few, string many) => Words.Plural(n, one, few, many);
}

public abstract class VoteModelBase : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null, params string[] also)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
        foreach (var other in also) Raise(other);
    }

    protected void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class VoteRowModel : VoteModelBase
{
    private static readonly Brush PollBar = Solid(Color.FromRgb(0xA9, 0x70, 0xFF));
    private static readonly Brush BlueBar = Solid(Color.FromRgb(0x38, 0x7A, 0xFF));
    private static readonly Brush PinkBar = Solid(Color.FromRgb(0xF5, 0x00, 0x9B));

    private string _label = "";
    private string _detail = "";
    private double _percent;
    private bool _winner;
    private bool _dim;
    private bool _showWin;
    private bool _armed;
    private Brush _bar = PollBar;

    public VoteRowModel(string id) => Id = id;

    public string Id { get; }

    public string Label
    {
        get => _label;
        private set => Set(ref _label, value);
    }

    public string Detail
    {
        get => _detail;
        private set => Set(ref _detail, value);
    }

    public double Percent
    {
        get => _percent;
        private set => Set(ref _percent, value);
    }

    public Brush BarBrush
    {
        get => _bar;
        private set => Set(ref _bar, value);
    }

    public bool IsWinner
    {
        get => _winner;
        private set => Set(ref _winner, value, nameof(IsWinner), nameof(Weight));
    }

    public FontWeight Weight => _winner ? FontWeights.Bold : FontWeights.Normal;

    public double RowOpacity => _dim ? 0.55 : 1;

    public bool ShowWin
    {
        get => _showWin;
        set => Set(ref _showWin, value);
    }

    public bool Armed
    {
        get => _armed;
        set => Set(ref _armed, value, nameof(Armed), nameof(WinText), nameof(WinName));
    }

    public string WinText => _armed ? "Точно?" : "Победил";

    public string WinName => (_armed ? "Точно? " : "") + $"Победил «{_label.TrimStart('✓', ' ')}»";

    public void Update(ChannelVote vote, VoteOption option, int percent, bool winner, bool anyWinner)
    {
        Label = (winner ? "✓ " : "") + option.Title;
        Detail = VoteCardText.Detail(vote, option, percent);
        Percent = percent;
        BarBrush = vote.Kind == VoteKind.Poll ? PollBar : option.Color == "PINK" ? PinkBar : BlueBar;
        IsWinner = winner;
        var dim = vote.Stage == VoteStage.Ended && (vote.IsCanceled || (anyWinner && !winner));
        if (dim != _dim)
        {
            _dim = dim;
            Raise(nameof(RowOpacity));
        }
        Raise(nameof(WinName));
    }

}

public abstract class FoldCardModel : VoteModelBase
{
    private bool _collapsed;
    private bool _keepBody;

    protected FoldCardModel(bool collapsed) => _collapsed = collapsed;

    public bool Collapsed
    {
        get => _collapsed;
        set
        {
            if (_collapsed == value) return;
            _collapsed = value;
            foreach (var name in FoldNames()) Raise(name);
        }
    }

    protected virtual IEnumerable<string> FoldNames() =>
        new[] { nameof(Collapsed), nameof(Expanded), nameof(BodyShown), nameof(FoldGlyph), nameof(FoldName), nameof(CardPadding) };

    public bool Expanded => !_collapsed;

    public bool BodyShown => !_collapsed || _keepBody;

    public void KeepBody(bool keep)
    {
        if (_keepBody == keep) return;
        _keepBody = keep;
        Raise(nameof(BodyShown));
    }

    public string FoldGlyph => _collapsed ? "▸" : "▾";

    public string FoldName => _collapsed ? "Развернуть" : "Свернуть";

    public Thickness CardPadding => _collapsed ? new Thickness(8, 3, 10, 3) : new Thickness(8, 6, 10, 8);

    public virtual void Disarm()
    {
    }
}

public sealed class VoteCardModel : FoldCardModel
{
    private ChannelVote _vote;
    private string _header = "";
    private string _status = "";
    private string _error = "";
    private bool _busy;
    private bool _interactive;
    private bool _cancelArmed;
    private string _leader = "";
    private string _short = "";

    public VoteCardModel(ChannelVote vote, DateTime now, bool interactive, bool collapsed = false) : base(collapsed)
    {
        _vote = vote;
        _interactive = interactive;
        Update(vote, now, interactive);
    }

    protected override IEnumerable<string> FoldNames() =>
        base.FoldNames().Concat(new[] { nameof(ShowLeader), nameof(ShownStatus), nameof(TitleWrapping) });

    public string Leader
    {
        get => _leader;
        private set => Set(ref _leader, value, nameof(Leader), nameof(ShowLeader));
    }

    public bool ShowLeader => Collapsed && _leader.Length > 0;

    public string ShortStatus
    {
        get => _short;
        private set => Set(ref _short, value, nameof(ShortStatus), nameof(ShownStatus));
    }

    public string ShownStatus => Collapsed ? _short : Status;

    public TextWrapping TitleWrapping => Collapsed ? TextWrapping.NoWrap : TextWrapping.Wrap;

    public ChannelVote Vote => _vote;

    public VoteKind Kind => _vote.Kind;

    public ObservableCollection<VoteRowModel> Rows { get; } = new();

    public string Header
    {
        get => _header;
        private set => Set(ref _header, value);
    }

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value, nameof(Status), nameof(ShownStatus));
    }

    public string Error
    {
        get => _error;
        set => Set(ref _error, value, nameof(Error), nameof(HasError));
    }

    public bool HasError => _error.Length > 0;

    public bool Busy
    {
        get => _busy;
        set => Set(ref _busy, value, nameof(Busy), nameof(NotBusy));
    }

    public bool NotBusy => !_busy;

    public bool ShowEnd => _interactive && Kind == VoteKind.Poll && _vote.Stage == VoteStage.Active;

    public bool ShowLock => _interactive && Kind == VoteKind.Prediction && _vote.Stage == VoteStage.Active;

    public bool ShowCancel => _interactive && Kind == VoteKind.Prediction && _vote.Stage != VoteStage.Ended;

    public bool ShowButtons => ShowEnd || ShowLock || ShowCancel;

    public bool CancelArmed
    {
        get => _cancelArmed;
        set => Set(ref _cancelArmed, value, nameof(CancelArmed), nameof(CancelText));
    }

    public string CancelText => _cancelArmed ? "Точно? Отменить" : "Отменить";

    public void Update(ChannelVote vote, DateTime now, bool interactive)
    {
        var stageChanged = vote.Stage != _vote.Stage || vote.Id != _vote.Id;
        _vote = vote;
        _interactive = interactive;
        if (stageChanged) Disarm();
        Header = VoteCardText.Header(vote);
        Leader = VoteCardText.Leader(vote);
        Status = VoteCardText.Status(vote, now);
        ShortStatus = VoteCardText.ShortStatus(vote, now);
        var percents = VoteCardText.Percents(vote);
        var winners = VoteCardText.Winners(vote);
        var ids = vote.Options.Select(o => o.Id).ToList();
        if (!Rows.Select(r => r.Id).SequenceEqual(ids))
        {
            Rows.Clear();
            foreach (var id in ids) Rows.Add(new VoteRowModel(id));
        }
        for (var i = 0; i < vote.Options.Count; i++)
        {
            Rows[i].Update(vote, vote.Options[i], percents[i], winners.Contains(vote.Options[i].Id), winners.Count > 0);
            Rows[i].ShowWin = interactive && vote.Kind == VoteKind.Prediction && vote.Stage == VoteStage.Locked;
            if (!Rows[i].ShowWin) Rows[i].Armed = false;
        }
        Raise(nameof(Kind));
        Raise(nameof(ShowEnd));
        Raise(nameof(ShowLock));
        Raise(nameof(ShowCancel));
        Raise(nameof(ShowButtons));
    }

    public void Tick(DateTime now)
    {
        Status = VoteCardText.Status(_vote, now);
        ShortStatus = VoteCardText.ShortStatus(_vote, now);
    }

    public void SetInteractive(bool interactive, DateTime now)
    {
        if (!interactive) Disarm();
        Update(_vote, now, interactive);
    }

    public override void Disarm()
    {
        CancelArmed = false;
        foreach (var row in Rows) row.Armed = false;
    }
}
