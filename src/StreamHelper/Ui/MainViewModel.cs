using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly DispatcherTimer _undoTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private readonly SyncStatus[] _statuses =
    {
        new(SyncState.NotConnected, "Не подключено"),
        new(SyncState.NotConnected, "Не подключено"),
        new(SyncState.NotConnected, "Не подключено"),
        new(SyncState.NotConnected, "Не подключено"),
        new(SyncState.NotConnected, "Не подключено"),
        new(SyncState.NotConnected, "Не подключено"),
    };
    private int _selectedTab;
    private string _undoText = "";
    private bool _hasUndo;
    private bool _canUndo;
    private Action? _undoAction;
    private readonly DispatcherTimer _rewardConfirmTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    private bool _rewardsBusy;
    private bool _confirmingRewardsReject;

    public MainViewModel(
        DonationStore donations, FollowerStore followers, SubscriberStore subscribers, RedemptionStore redemptions,
        PingStore pings, MomentStore moments, int selectedTab)
    {
        Donations = donations;
        Followers = followers;
        Subscribers = subscribers;
        Redemptions = redemptions;
        Pings = pings;
        Moments = moments;
        _selectedTab = selectedTab is >= 0 and <= 5 ? selectedTab : 0;
        _undoTimer.Tick += (_, _) => ClearUndo();
        donations.PropertyChanged += OnUnseenChanged;
        followers.PropertyChanged += OnUnseenChanged;
        subscribers.PropertyChanged += OnUnseenChanged;
        redemptions.PropertyChanged += OnUnseenChanged;
        pings.PropertyChanged += OnUnseenChanged;
        moments.PropertyChanged += OnUnseenChanged;

        PingSelection = new SelectionTracker<ChatPing>(pings.Items);
        FollowerSelection = new SelectionTracker<Follower>(followers.Items);
        SubscriberSelection = new SelectionTracker<Subscriber>(subscribers.Items);
        DonationSelection = new SelectionTracker<Donation>(donations.Items);
        MomentSelection = new SelectionTracker<ChannelMoment>(moments.Items);
        RewardSelection = new SelectionTracker<Redemption>(redemptions.Items);
        RewardSelection.PropertyChanged += OnRewardSelectionChanged;
        _rewardConfirmTimer.Tick += (_, _) => CancelRewardsRejectConfirm();
    }

    public DonationStore Donations { get; }
    public FollowerStore Followers { get; }
    public SubscriberStore Subscribers { get; }
    public RedemptionStore Redemptions { get; }
    public PingStore Pings { get; }
    public MomentStore Moments { get; }

    public int TotalUnseen =>
        Donations.UnseenCount + Followers.UnseenCount + Subscribers.UnseenCount + Redemptions.UnseenCount + Pings.UnseenCount +
        Moments.UnseenCount;

    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab == value) return;
            _selectedTab = value;
            Raise();
            Raise(nameof(IsDonationsTab));
            Raise(nameof(IsFollowersTab));
            Raise(nameof(IsSubscribersTab));
            Raise(nameof(IsRewardsTab));
            Raise(nameof(IsPingsTab));
            Raise(nameof(IsMomentsTab));
            Raise(nameof(StatusText));
        }
    }

    public bool IsDonationsTab
    {
        get => _selectedTab == 0;
        set
        {
            if (value) SelectedTab = 0;
        }
    }

    public bool IsFollowersTab
    {
        get => _selectedTab == 1;
        set
        {
            if (value) SelectedTab = 1;
        }
    }

    public bool IsSubscribersTab
    {
        get => _selectedTab == 2;
        set
        {
            if (value) SelectedTab = 2;
        }
    }

    public bool IsRewardsTab
    {
        get => _selectedTab == 3;
        set
        {
            if (value) SelectedTab = 3;
        }
    }

    public bool IsPingsTab
    {
        get => _selectedTab == 4;
        set
        {
            if (value) SelectedTab = 4;
        }
    }

    public bool IsMomentsTab
    {
        get => _selectedTab == 5;
        set
        {
            if (value) SelectedTab = 5;
        }
    }


    public string StatusText => _statuses[_selectedTab].Message;

    public string DonationsEmptyText => EmptyText(_statuses[0], "Новые донаты появятся здесь.");

    public string FollowersEmptyText => EmptyText(_statuses[1], "Новые фолловеры появятся здесь.");

    public string SubscribersEmptyText => EmptyText(_statuses[2], "Новые подписки появятся здесь.\nСобытия приходят только пока приложение запущено.");

    public string RewardsEmptyText => EmptyText(_statuses[3], "Новые награды за баллы появятся здесь.\nСобытия приходят только пока приложение запущено.");

    public string PingsEmptyText => EmptyText(_statuses[4], "Сообщения, где тебя упомянули (@ник), появятся здесь.\nСобытия приходят только пока приложение запущено.");

    public string MomentsEmptyText => EmptyText(_statuses[5], "Рейды на твой канал и стрики зрителей появятся здесь.\nСобытия приходят только пока приложение запущено.");

    public SelectionTracker<ChatPing> PingSelection { get; }
    public SelectionTracker<Follower> FollowerSelection { get; }
    public SelectionTracker<Subscriber> SubscriberSelection { get; }
    public SelectionTracker<Donation> DonationSelection { get; }
    public SelectionTracker<ChannelMoment> MomentSelection { get; }
    public SelectionTracker<Redemption> RewardSelection { get; }

    public bool RewardsIdle => !_rewardsBusy;

    public bool RewardsBusy
    {
        get => _rewardsBusy;
        set
        {
            if (_rewardsBusy == value) return;
            _rewardsBusy = value;
            Raise();
            Raise(nameof(RewardsIdle));
        }
    }

    public bool ConfirmingRewardsReject
    {
        get => _confirmingRewardsReject;
        private set
        {
            if (_confirmingRewardsReject == value) return;
            _confirmingRewardsReject = value;
            Raise();
            Raise(nameof(ShowRewardsBulkActions));
            Raise(nameof(ShowRewardsBulkConfirm));
        }
    }

    public bool ShowRewardsBulkActions => !_confirmingRewardsReject;

    public bool ShowRewardsBulkConfirm => _confirmingRewardsReject;

    public string UndoText
    {
        get => _undoText;
        private set => Set(ref _undoText, value);
    }

    public bool HasUndo
    {
        get => _hasUndo;
        private set => Set(ref _hasUndo, value);
    }

    public bool CanUndo
    {
        get => _canUndo;
        private set => Set(ref _canUndo, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void ApplyDonationStatus(SyncStatus status) => Apply(0, status, nameof(DonationsEmptyText));

    public void ApplyFollowerStatus(SyncStatus status) => Apply(1, status, nameof(FollowersEmptyText));

    public void ApplySubscriberStatus(SyncStatus status) => Apply(2, status, nameof(SubscribersEmptyText));

    public void ApplyRewardStatus(SyncStatus status) => Apply(3, status, nameof(RewardsEmptyText));

    public void ApplyPingStatus(SyncStatus status) => Apply(4, status, nameof(PingsEmptyText));

    public void ApplyMomentStatus(SyncStatus status) => Apply(5, status with { Message = MomentStatusText(status) }, nameof(MomentsEmptyText));

    public static string MomentStatusText(SyncStatus status) =>
        status.Message.StartsWith("Пинги", StringComparison.Ordinal) ? "Рейды и стрики" + status.Message["Пинги".Length..] : status.Message;

    public void ShowUndo(string text, Action restore)
    {
        _undoAction = restore;
        UndoText = text;
        CanUndo = true;
        HasUndo = true;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    public void ShowNotice(string text)
    {
        _undoAction = null;
        UndoText = text;
        CanUndo = false;
        HasUndo = true;
        _undoTimer.Stop();
        _undoTimer.Start();
    }

    public void DeleteSelectedPings() =>
        DeleteSelected(Pings, PingSelection, ping => $"Удалён пинг: {ping.Name}", "Удалено пингов");

    public void DeleteSelectedFollowers() =>
        DeleteSelected(Followers, FollowerSelection, follower => $"Удалён фолловер: {follower.Name}", "Удалено фолловеров");

    public void DeleteSelectedSubscribers() =>
        DeleteSelected(Subscribers, SubscriberSelection, subscriber => $"Удалено: {subscriber.Name} ({subscriber.KindText})", "Удалено подписок");

    public void DeleteSelectedMoments() =>
        DeleteSelected(Moments, MomentSelection, moment => $"Удалено: {moment.Name} ({moment.KindText})", "Удалено записей");

    public void DeleteSelectedDonations() =>
        DeleteSelected(Donations, DonationSelection, donation => $"Удалено: {donation.DisplayName} · {donation.AmountText}", "Удалено донатов");

    public void BeginRewardsRejectConfirm()
    {
        if (!RewardSelection.HasAny) return;
        ConfirmingRewardsReject = true;
        _rewardConfirmTimer.Stop();
        _rewardConfirmTimer.Start();
    }

    public void CancelRewardsRejectConfirm()
    {
        _rewardConfirmTimer.Stop();
        ConfirmingRewardsReject = false;
    }

    private void DeleteSelected<T>(EventStore<T> store, SelectionTracker<T> selection, Func<T, string> one, string many)
        where T : class, ISeenItem, ISelectable
    {
        var picked = selection.Picked();
        if (picked.Count == 0) return;
        foreach (var item in picked) item.Selected = false;
        var removed = store.RemoveRange(picked);
        if (removed.Count == 0) return;
        ShowUndo(removed.Count == 1 ? one(removed[0].Item) : $"{many}: {removed.Count}", () => store.RestoreRange(removed));
    }

    public void Undo()
    {
        _undoAction?.Invoke();
        ClearUndo();
    }

    private void Apply(int tab, SyncStatus status, string emptyTextProperty)
    {
        _statuses[tab] = status;
        Raise(emptyTextProperty);
        if (tab == _selectedTab)
        {
            Raise(nameof(StatusText));
        }
    }

    private void OnRewardSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SelectionTracker<Redemption>.Count)) CancelRewardsRejectConfirm();
    }

    private void OnUnseenChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DonationStore.UnseenCount)) Raise(nameof(TotalUnseen));
    }

    private void ClearUndo()
    {
        _undoTimer.Stop();
        _undoAction = null;
        HasUndo = false;
        CanUndo = false;
        UndoText = "";
    }

    private static string EmptyText(SyncStatus status, string whenConnected) =>
        status.State == SyncState.Ok ? "Пока пусто.\n" + whenConnected : status.Message;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
