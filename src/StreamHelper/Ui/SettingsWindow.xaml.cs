using System;
using System.Collections.Generic;
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
    private const int RewardsSection = 5;

    private readonly Services _services;
    private readonly List<Action> _unhook = new();
    private bool _loading = true;

    public SettingsWindow(Services services)
    {
        InitializeComponent();
        _services = services;

        var settings = services.Settings.Current;
        InitHotkeys(settings);
        InitToasts(settings);
        InitMuteBadge(settings);
        ShowCredits(!settings.CreditsCollapsed);
        InitNotifyChecks(settings);
        InitPings(settings);
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
        Closed += (_, _) => OnClosedCleanup();

        var section = settings.SettingsSection is >= 0 and <= 7 ? settings.SettingsSection : 0;
        if (!settings.HasAnyDonationSource && !settings.HasTwitchTokens) section = 1;
        if (TakeProfileNotice()) section = 0;
        _loading = false;
        ApplyHotkeys();
        SelectSection(section);
        if (section == RewardsSection) _ = LoadRewardSectionAsync();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x231D1B, 0xEEE8E6, 0x4B3F3A);
    }

    private void OnClosedCleanup()
    {
        foreach (var unhook in _unhook) unhook();
        _services.Chat.EndPositioning();
        _twitchCts?.Cancel();
        _services.Toasts.EndPositioning();
        _services.MuteBadge.EndPositioning();
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
        WatchStatus(_services.FollowerPoller, status => ShowStatus(FollowersStatusText, FollowersStatusDot, status));
        WatchStatus(_services.SubscriptionListener, status => ShowStatus(SubsStatusText, SubsStatusDot, status));
        WatchStatus(_services.RewardListener, status => ShowStatus(RewardsStatusText, RewardsStatusDot, status));
        WatchStatus(_services.PingListener, status => ShowStatus(PingsStatusText, PingsStatusDot, status));
        WatchStatus(_services.PingListener, ApplyChatStatus);
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

    private static void TogglePositioning(IPositionable target, Button button)
    {
        if (target.IsPositioning)
        {
            target.EndPositioning();
            button.Content = "Выбрать положение";
        }
        else
        {
            target.BeginPositioning();
            button.Content = "Готово";
        }
    }

    private void Reveal(FrameworkElement element) =>
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(() => element.BringIntoView()));

    private RadioButton[] SectionTabs => new[] { TabGeneral, TabDonations, TabTwitch, TabFollowers, TabSubscribers, TabRewards, TabPings, TabChat };

    private FrameworkElement[] SectionPanels =>
        new FrameworkElement[] { SectionGeneral, SectionDonations, SectionTwitch, SectionFollowers, SectionSubscribers, SectionRewards, SectionPings, SectionChat };

    private void SelectSection(int index)
    {
        SectionTabs[index].IsChecked = true;
        ShowSection(index);
    }

    private void ShowSection(int index)
    {
        var panels = SectionPanels;
        for (var i = 0; i < panels.Length; i++) panels[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        if (index == RewardsSection && !_loading) _ = LoadRewardSectionAsync();
    }

    private void OnSectionChecked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out var index)) return;
        ShowSection(index);
        _services.Settings.Current.SettingsSection = index;
        _services.Settings.Save();
    }

    private void InitNotifyChecks(AppSettings settings)
    {
        ToastCheck.IsChecked = settings.ShowToast;
        FollowersCheck.IsChecked = settings.NotifyFollowers;
        SubscribersCheck.IsChecked = settings.NotifySubscribers;
        RewardsCheck.IsChecked = settings.NotifyRewards;
        PingsCheck.IsChecked = settings.NotifyPings;
    }

    private void OnOptionsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var settings = _services.Settings.Current;
        settings.ShowToast = ToastCheck.IsChecked == true;
        settings.NotifyFollowers = FollowersCheck.IsChecked == true;
        settings.NotifySubscribers = SubscribersCheck.IsChecked == true;
        settings.NotifyRewards = RewardsCheck.IsChecked == true;
        settings.NotifyPings = PingsCheck.IsChecked == true;
        _services.Settings.Save();
    }

    private void OnAddTestDonation(object sender, RoutedEventArgs e) => _services.AddTestDonation();

    private void OnAddTestFollower(object sender, RoutedEventArgs e) => _services.AddTestFollower();

    private void OnAddTestSubscriber(object sender, RoutedEventArgs e) => _services.AddTestSubscriber();

    private void OnAddTestRedemption(object sender, RoutedEventArgs e) => _services.AddTestRedemption();

    private void OnAddTestPing(object sender, RoutedEventArgs e) => _services.AddTestPing();

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
