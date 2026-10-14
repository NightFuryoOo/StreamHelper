using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class SettingsWindow : Window
{
    public const string ConnectionsPage = "connections";
    public const string EventsPage = "events";
    public const string RewardsPage = "rewards";
    public const string ChatPage = "chat";
    public const string GeneralPage = "general";

    private readonly Services _services;
    private readonly List<Action> _unhook = new();
    private bool _loading = true;
    private string? _page;

    public SettingsWindow(Services services)
    {
        InitializeComponent();
        _services = services;
        UpdateBannerView.DataContext = services.Updates;
        VersionBlock.DataContext = services.Updates;

        var settings = services.Settings.Current;
        InitHotkeys(settings);
        InitToasts(settings);
        InitMuteBadge(settings);
        ShowCredits(!settings.CreditsCollapsed);
        InitNotifyChecks(settings);
        InitTabChecks(settings);
        InitPings(settings);
        InitMuteRewards();
        InitRewardSounds();
        InitChat(settings);
        InitRewards(settings);
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Close();
        };
        MaxHeight = Math.Max(400, SystemParameters.WorkArea.Height - 30);
        Height = Math.Min(Height, MaxHeight);

        InitStatuses();
        RefreshRewardAlert();
        Hook(() => services.RewardAlert.Failed += OnRewardAlertFailed, () => services.RewardAlert.Failed -= OnRewardAlertFailed);
        RefreshTwitchSummary();
        _ = LoadModeratedChannelsAsync();
        StartLiveWatch();
        Closed += (_, _) => OnClosedCleanup();

        var page = StartPage(settings, TakeProfileNotice());
        _loading = false;
        ApplyHotkeys();
        SelectPage(page);
        if (page == RewardsPage) _ = LoadRewardSectionAsync();
    }

    public static string StartPage(AppSettings settings, bool profileNotice)
    {
        if (profileNotice) return GeneralPage;
        if (!settings.HasAnyDonationSource && !settings.HasTwitchTokens) return ConnectionsPage;
        return settings.SettingsPage is EventsPage or RewardsPage or ChatPage or GeneralPage ? settings.SettingsPage : ConnectionsPage;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x231D1B, 0xEEE8E6, 0x4B3F3A);
    }

    private void OnClosedCleanup()
    {
        StopLiveWatch();
        foreach (var unhook in _unhook) unhook();
        StopPositioning();
        _twitchCts?.Cancel();
        if (_toastSaveTimer.IsEnabled)
        {
            _toastSaveTimer.Stop();
            _services.Settings.Save();
        }
        ApplyHotkeys();
    }

    private void Hook(Action subscribe, Action unsubscribe)
    {
        subscribe();
        _unhook.Add(unsubscribe);
    }

    private void WatchStatus(ISyncWorker source, Action<SyncStatus> apply)
    {
        apply(source.Status);
        Action<SyncStatus> handler = status => Dispatcher.InvokeAsync(() => apply(status));
        Hook(() => source.StatusChanged += handler, () => source.StatusChanged -= handler);
    }

    private void InitStatuses()
    {
        foreach (var service in DonationServices.All)
        {
            var current = service;
            WatchStatus(DonationServices.Poller(_services, current), status => ApplyTile(current, status));
        }
        WatchStatus(_services.DaMedia, ApplyVideoState);
        ShowStatus(DonationsStatusText, DonationsStatusDot, _services.DonationStatus.Combined);
        Action<SyncStatus> donations = status => Dispatcher.InvokeAsync(() => ShowStatus(DonationsStatusText, DonationsStatusDot, status));
        Hook(() => _services.DonationStatus.Changed += donations, () => _services.DonationStatus.Changed -= donations);
        WatchStatus(_services.FollowerPoller, status => ShowStatus(FollowersStatusText, FollowersStatusDot, status));
        WatchStatus(_services.SubscriptionListener, status => ShowStatus(SubsStatusText, SubsStatusDot, status));
        WatchStatus(_services.RewardListener, status => ShowStatus(RewardsStatusText, RewardsStatusDot, status));
        WatchStatus(_services.PingListener, status => ShowStatus(PingsStatusText, PingsStatusDot, PingListener.TabStatus(status, _services.Settings.Current)));
        WatchStatus(_services.PingListener, ApplyChatStatus);
        WatchStatus(_services.PingListener, status =>
        {
            var moments = PingListener.TabStatus(status, _services.Settings.Current);
            ShowStatus(MomentsStatusText, MomentsStatusDot, moments with { Message = MainViewModel.MomentStatusText(moments) });
        });
    }

    private static Brush Resource(string key) => (Brush)Application.Current.Resources[key];

    private static Brush StatusBrush(SyncState state) => Resource(DonationServices.BrushKey(state));

    private static void ShowStatus(TextBlock text, Shape dot, SyncStatus status)
    {
        text.Text = status.Message;
        dot.Fill = StatusBrush(status.State);
    }

    private static void ShowLine(TextBlock line, string text)
    {
        line.Text = text;
        line.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Percent(Slider slider) => $"{slider.Value:0}%";

    private static void SetPercent(Slider slider, TextBlock text, double fraction)
    {
        slider.Value = Math.Round(fraction * 100);
        text.Text = Percent(slider);
    }

    public const string PositionText = "Настроить положение и размеры";

    private static void TogglePositioning(IPositionable target, Button button)
    {
        if (target.IsPositioning)
        {
            target.EndPositioning();
            button.Content = PositionText;
        }
        else
        {
            target.BeginPositioning();
            button.Content = "Готово";
        }
    }

    private (IPositionable Target, Button Button)[] Positionables => new (IPositionable, Button)[]
    {
        (_services.Chat, ChatPositionButton), (_services.Toasts, ToastPositionButton), (_services.MuteBadge, MuteBadgePositionButton),
    };

    private void StopPositioning()
    {
        foreach (var (target, button) in Positionables)
        {
            if (!target.IsPositioning) continue;
            target.EndPositioning();
            button.Content = PositionText;
        }
    }

    private void Reveal(FrameworkElement element) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => element.BringIntoView()));

    private (RadioButton Nav, FrameworkElement Page)[] Pages => new (RadioButton, FrameworkElement)[]
    {
        (NavConnections, PageConnections), (NavEvents, PageEvents), (NavRewards, PageRewards), (NavChat, PageChat), (NavGeneral, PageGeneral),
    };

    public void GoToPage(string page)
    {
        if (Pages.Any(p => (string)p.Nav.Tag == page)) SelectPage(page);
    }

    private void OnOpenHelp(object sender, RoutedEventArgs e) => _services.OpenHelp();

    private void SelectPage(string page)
    {
        foreach (var (nav, _) in Pages)
        {
            if ((string)nav.Tag == page) nav.IsChecked = true;
        }
        ShowPage(page);
    }

    private void ShowPage(string page)
    {
        if (page != _page) StopPositioning();
        _page = page;
        foreach (var (nav, panel) in Pages) panel.Visibility = (string)nav.Tag == page ? Visibility.Visible : Visibility.Collapsed;
        if (page == RewardsPage && !_loading) _ = LoadRewardSectionAsync();
    }

    private void OnSectionChecked(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not RadioButton { Tag: string page }) return;
        ShowPage(page);
        _services.Settings.Current.SettingsPage = page;
        _services.Settings.Save();
    }

    private void InitNotifyChecks(AppSettings settings)
    {
        ToastCheck.IsChecked = settings.ShowToast;
        FollowersCheck.IsChecked = settings.NotifyFollowers;
        SubscribersCheck.IsChecked = settings.NotifySubscribers;
        BitsCheck.IsChecked = settings.NotifyBits;
        RewardsCheck.IsChecked = settings.NotifyRewards;
        PingsCheck.IsChecked = settings.NotifyPings;
        RaidsCheck.IsChecked = settings.NotifyRaids;
        StreaksCheck.IsChecked = settings.NotifyStreaks;
    }

    private void OnOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var settings = _services.Settings.Current;
        settings.ShowToast = ToastCheck.IsChecked == true;
        settings.NotifyFollowers = FollowersCheck.IsChecked == true;
        settings.NotifySubscribers = SubscribersCheck.IsChecked == true;
        settings.NotifyBits = BitsCheck.IsChecked == true;
        settings.NotifyRewards = RewardsCheck.IsChecked == true;
        settings.NotifyPings = PingsCheck.IsChecked == true;
        settings.NotifyRaids = RaidsCheck.IsChecked == true;
        settings.NotifyStreaks = StreaksCheck.IsChecked == true;
        _services.Settings.Save();
    }

    private void OnAddTestDonation(object sender, RoutedEventArgs e) => _services.AddTestDonation();

    private void OnAddTestFollower(object sender, RoutedEventArgs e) => _services.AddTestFollower();

    private void OnAddTestSubscriber(object sender, RoutedEventArgs e) => _services.AddTestSubscriber();

    private void OnAddTestBits(object sender, RoutedEventArgs e) => _services.AddTestBits();

    private void OnAddTestRedemption(object sender, RoutedEventArgs e) => _services.AddTestRedemption();

    private void OnAddTestPing(object sender, RoutedEventArgs e) => _services.AddTestPing();

    private void OnAddTestRaid(object sender, RoutedEventArgs e) => _services.AddTestRaid();

    private void OnAddTestStreak(object sender, RoutedEventArgs e) => _services.AddTestStreak();


    private void OnChooseVersion(object sender, RoutedEventArgs e) => new VersionWindow(_services.Updates) { Owner = this }.ShowDialog();

    private static void OpenInBrowser(string url) => BrowserLauncher.Open(url);

    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string url }) OpenInBrowser(url);
    }

    private void OnCollapseCredits(object sender, RoutedEventArgs e) => SetCreditsCollapsed(true);

    private void OnExpandCredits(object sender, RoutedEventArgs e) => SetCreditsCollapsed(false);

    private void SetCreditsCollapsed(bool collapsed)
    {
        _services.Settings.Current.CreditsCollapsed = collapsed;
        _services.Settings.Save();
        ShowCredits(!collapsed);
    }

    private void ShowCredits(bool expanded)
    {
        CreditsBar.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        CreditsHandle.Visibility = expanded ? Visibility.Collapsed : Visibility.Visible;
    }
}
