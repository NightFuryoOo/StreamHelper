using System.ComponentModel;

namespace StreamHelper.Models;

public sealed class RewardRow : INotifyPropertyChanged
{
    private bool _enabled;
    private bool _shown;
    private bool _busy;
    private bool _confirming;
    private bool _renaming;
    private string _title;
    private string _editText = "";

    public RewardRow(string id, string title, long cost, bool enabled, bool own, bool canManage, int pendingCount, bool shown)
    {
        Id = id;
        _title = title;
        Cost = cost;
        Own = own;
        CanManage = canManage;
        PendingCount = pendingCount;
        _enabled = enabled;
        _shown = shown;
    }

    public string Id { get; }

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value;
            Raise(nameof(Title));
            Raise(nameof(Name));
            Raise(nameof(DisplayText));
            Raise(nameof(ConfirmText));
            Raise(nameof(SwitchName));
            Raise(nameof(DeleteName));
            Raise(nameof(RenameName));
        }
    }

    public long Cost { get; }
    public bool Own { get; }
    public bool TwitchMade => !Own;
    public bool CanManage { get; }
    public int PendingCount { get; }

    public string Name => string.IsNullOrWhiteSpace(Title.Replace("​", "")) ? "Награда без названия" : Title.Replace("​", "");

    public string DisplayText =>
        $"{Name}{(Own ? "" : " (Создано Twitch)")} · {Redemption.FormatCost(Cost)}{(_enabled ? "" : " · выключена")}" +
        (PendingCount > 0 ? $" · необработанных заказов: {PendingCount}" : "");

    public string ConfirmText => PendingCount > 0
        ? $"Удалить «{Name}»? Необработанные заказы ({PendingCount}) Twitch засчитает как выполненные, баллы не вернутся."
        : $"Удалить «{Name}»?";

    public string SwitchName => _enabled ? $"Выключить «{Name}» на канале" : $"Включить «{Name}» на канале";

    public string DeleteName => $"Удалить «{Name}»";

    public string RenameName => $"Переименовать «{Name}»";

    public bool Renaming
    {
        get => _renaming;
        set
        {
            if (_renaming == value) return;
            _renaming = value;
            Raise(nameof(Renaming));
            Raise(nameof(NotRenaming));
        }
    }

    public bool NotRenaming => !_renaming;

    public string EditText
    {
        get => _editText;
        set
        {
            if (_editText == value) return;
            _editText = value;
            Raise(nameof(EditText));
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Raise(nameof(Enabled));
            Raise(nameof(DisplayText));
            Raise(nameof(SwitchName));
        }
    }

    public bool Shown
    {
        get => _shown;
        set
        {
            if (_shown == value) return;
            _shown = value;
            Raise(nameof(Shown));
        }
    }

    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            Raise(nameof(Busy));
            Raise(nameof(NotBusy));
        }
    }

    public bool NotBusy => !_busy;

    public bool Confirming
    {
        get => _confirming;
        set
        {
            if (_confirming == value) return;
            _confirming = value;
            Raise(nameof(Confirming));
            Raise(nameof(NotConfirming));
        }
    }

    public bool NotConfirming => !_confirming;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
