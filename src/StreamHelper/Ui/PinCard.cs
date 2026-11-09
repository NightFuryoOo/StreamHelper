using System;
using System.Collections.Generic;
using System.Linq;
using StreamHelper.Api;

namespace StreamHelper.Ui;

public static class PinCardText
{
    public sealed record Choice(string Text, int? Seconds);

    public static readonly IReadOnlyList<Choice> Choices = new[]
    {
        new Choice("1 мин", 60),
        new Choice("5 мин", 300),
        new Choice("10 мин", 600),
        new Choice("30 мин", 1800),
        new Choice("До конца стрима", null),
    };

    public static string Title(PinnedMessage pinned) =>
        pinned.PinnedByLabel.Length > 0 ? "Закреплено · " + pinned.PinnedByLabel : "Закреплено";

    public static string Status(PinnedMessage pinned, DateTime now) =>
        pinned.EndsUtc is { } ends ? "ещё " + VoteCardText.Clock(Left(ends, now)) : "до конца стрима";

    public static string ShortStatus(PinnedMessage pinned, DateTime now) =>
        pinned.EndsUtc is { } ends ? VoteCardText.Clock(Left(ends, now)) : "до конца";

    public static bool Expired(PinnedMessage? pinned, DateTime now) => pinned?.EndsUtc is { } ends && ends <= now;

    public static bool SamePin(PinnedMessage? a, PinnedMessage? b) =>
        a == null || b == null
            ? a == null && b == null
            : a.Message.MessageId == b.Message.MessageId && a.EndsUtc == b.EndsUtc && a.PinnedByLabel == b.PinnedByLabel && a.Message.Text == b.Message.Text;

    private static TimeSpan Left(DateTime ends, DateTime now) => ends - now > TimeSpan.Zero ? ends - now : TimeSpan.Zero;
}

public sealed class PinCardModel : FoldCardModel
{
    private PinnedMessage _pinned;
    private string _status = "";
    private string _short = "";
    private string _error = "";
    private bool _busy;
    private bool _canUnpin;

    public PinCardModel(PinnedMessage pinned, DateTime now, bool canUnpin, bool collapsed) : base(collapsed)
    {
        _pinned = pinned;
        _canUnpin = canUnpin;
        Tick(now);
    }

    protected override IEnumerable<string> FoldNames() => base.FoldNames().Concat(new[] { nameof(ShownStatus) });

    public PinnedMessage Pinned => _pinned;

    public string MessageId => _pinned.Message.MessageId;

    public string Title => PinCardText.Title(_pinned);

    public string Status
    {
        get => _status;
        private set => Set(ref _status, value, nameof(Status), nameof(ShownStatus));
    }

    public string ShortStatus
    {
        get => _short;
        private set => Set(ref _short, value, nameof(ShortStatus), nameof(ShownStatus));
    }

    public string ShownStatus => Collapsed ? _short : _status;

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

    public bool ShowUnpin
    {
        get => _canUnpin;
        set => Set(ref _canUnpin, value);
    }

    public void Update(PinnedMessage pinned, DateTime now)
    {
        if (pinned.Message.MessageId != _pinned.Message.MessageId) Error = "";
        _pinned = pinned;
        Raise(nameof(Title));
        Raise(nameof(MessageId));
        Tick(now);
    }

    public void Tick(DateTime now)
    {
        Status = PinCardText.Status(_pinned, now);
        ShortStatus = PinCardText.ShortStatus(_pinned, now);
    }
}
