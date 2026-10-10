using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper;

public partial class App : Application
{
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showSignal;
    private HttpClient? _http;
    private Services? _services;
    private TrayIcon? _tray;
    private string[] _args = Array.Empty<string>();
    private readonly string? _exePath = Environment.ProcessPath;

    private const string RestartFlag = "--restarted";

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var args = e.Args;
        _args = args;
        var demo = Array.IndexOf(args, "--demo") >= 0;
        var showAtStart = demo || Array.IndexOf(args, "--show") >= 0;
        AppPaths.Init(ReadOption(args, "--data"));
        if (!BecomeTheOnlyInstance(Array.IndexOf(args, RestartFlag) >= 0))
        {
            _showSignal!.Set();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args2) =>
        {
            Log.Write("Unhandled UI exception: " + args2.Exception);
            args2.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args2) =>
        {
            Log.Write("Unobserved task exception: " + args2.Exception);
            args2.SetObserved();
        };

        var imported = Profile.ApplyPending(AppPaths.Directory, AppVersion.Current, DateTime.Now);
        if (imported != null) Log.Write(imported.Success ? "Profile loaded." : "Profile load failed: " + imported.Error);

        var services = CreateServices(demo);
        _services = services;
        var settings = services.Settings;
        CreateSyncWorkers(services);
        CreateTray(services);

        services.Hotkey.Pressed += action => Dispatcher.InvokeAsync(() => OnHotkey(services, action));
        foreach (var failed in services.Hotkey.RegisterAll(settings.Current))
        {
            Log.Write($"Hotkey registration failed for {failed}: combination is taken by another program.");
        }

        var showSignal = _showSignal!;
        Task.Run(() =>
        {
            while (true)
            {
                showSignal.WaitOne();
                Dispatcher.InvokeAsync(services.Overlay.ShowOverlay);
            }
        });

        foreach (var worker in services.Workers) worker.Start();
        services.Chat.Apply();
        services.RewardAlert.MutedChanged += _ => services.MuteBadge.Apply(services.RewardAlert.Muted);
        services.RewardAlert.MutedChanged += muted => _ = services.RewardMute.SyncAsync();
        _ = services.RewardMute.SyncAsync();
        services.MuteBadge.Apply(services.RewardAlert.Muted);
        _ = services.RefreshManagedRewardsAsync();
        if (_exePath != null) _ = UpdateInstaller.CleanUpSoonAsync(_exePath);
        if (UpdateInstaller.TakeMarker(AppPaths.Directory) is { } marker && marker.Version == AppVersion.Current)
        {
            if (marker.Chosen) services.Toasts.Show("Установлена версия " + AppVersion.Current, "Выбрана в настройках", null);
            else services.Toasts.Show("StreamHelper обновлён", "Версия " + AppVersion.Current, null);
        }
        services.Updates.Start();

        if (showAtStart) services.Overlay.ShowOverlay();
        if (imported != null)
        {
            services.ProfileNotice = ProfileTexts.Imported(imported, settings.Current);
            services.OpenSettings();
        }
        else if (!settings.Current.HasAnyDonationSource)
        {
            services.OpenSettings();
        }
    }

    private bool BecomeTheOnlyInstance(bool restarted)
    {
        var instanceKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.Directory.ToLowerInvariant())))[..16];
        var mutexName = "Local\\StreamHelper." + instanceKey;
        bool isFirst;
        if (restarted)
        {
            _instanceMutex = new Mutex(false, mutexName);
            isFirst = WaitForPreviousInstance(_instanceMutex);
        }
        else
        {
            _instanceMutex = new Mutex(true, mutexName, out isFirst);
        }
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\StreamHelper.Show." + instanceKey);
        return isFirst;
    }

    private Services CreateServices(bool demo)
    {
        var settings = new SettingsStore(AppPaths.SettingsFile);
        var donations = new DonationStore(AppPaths.DonationsFile);
        var followers = new FollowerStore(AppPaths.FollowersFile);
        var subscribers = new SubscriberStore(AppPaths.SubscribersFile);
        var redemptions = new RedemptionStore(AppPaths.RedemptionsFile);
        var pings = new PingStore(AppPaths.PingsFile);
        var moments = new MomentStore(AppPaths.MomentsFile);
        if (demo) SeedDemo(donations, followers, subscribers, redemptions, pings, moments);

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("StreamHelper/" + AppVersion.Current);
        var twitchEndpoints = TwitchEndpointsFromEnvironment();
        var twitch = new TwitchClient(settings, _http, twitchEndpoints);
        var viewModel = new MainViewModel(donations, followers, subscribers, redemptions, pings, moments, settings.Current.SelectedTab);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName != nameof(MainViewModel.SelectedTab)) return;
            settings.Current.SelectedTab = viewModel.SelectedTab;
            settings.Save();
        };
        var chatFeed = new ChatFeed();
        var sevenTv = new SevenTvCatalog(new SevenTvClient(_http, Environment.GetEnvironmentVariable("STREAMHELPER_7TV_URL")), () => settings.Current.ChannelId);
        var emotes = new ChatEmotes(sevenTv, new EmoteImageCache(_http), twitchEndpoints);
        var votes = new VoteWatcher(settings, twitch);

        var services = new Services
        {
            Settings = settings,
            Donations = donations,
            Followers = followers,
            Subscribers = subscribers,
            Redemptions = redemptions,
            Pings = pings,
            Moments = moments,
            Client = new DonationAlertsClient(settings, _http, EndpointsFromEnvironment()),
            Twitch = twitch,
            Hotkey = new HotkeyService(),
            Toasts = new ToastService(settings),
            ChatFeed = chatFeed,
            Chat = new ChatOverlayService(settings, chatFeed, new ChatBadgeCatalog(twitch), twitch, twitch, emotes, twitch, votes),
            Votes = votes,
            RewardAlert = new RewardAlertService(settings),
            RewardMute = new RewardMuteSwitch(settings, twitch),
            MuteBadge = new MuteBadgeService(settings),
            ViewModel = viewModel,
            Updates = new UpdateService(
                settings, _http, Environment.GetEnvironmentVariable("STREAMHELPER_GITHUB_API"), AppVersion.Value, _exePath,
                UpdateInstaller.IsInstallable(_exePath, AppContext.BaseDirectory, "StreamHelper"), AppPaths.Directory, Restart),
        };
        services.Overlay = new MainWindow(services);
        services.HandleDonations = OnDonations;
        services.HandleFollowers = OnFollowers;
        services.HandleSubscribers = OnSubscribers;
        services.HandleRedemptions = OnRedemptions;
        services.HandlePings = OnPings;
        services.HandleMoments = OnMoments;
        services.RestartApp = Restart;
        return services;
    }

    private void CreateSyncWorkers(Services services)
    {
        var settings = services.Settings;
        var viewModel = services.ViewModel;
        var twitch = services.Twitch;
        var eventSubUrl = TwitchEndpointsFromEnvironment().EventSubUrl;

        services.Poller = new DonationPoller(settings, services.Client, OnUi<Donation>(OnDonations));
        services.DonatePayPoller = new DonatePayPoller(
            settings, new DonatePayClient(settings, _http!, Environment.GetEnvironmentVariable("STREAMHELPER_DONATEPAY_URL")), OnUi<Donation>(OnDonations));
        services.DonateXPoller = new DonateXPoller(
            settings, new DonateXClient(settings, _http!, Environment.GetEnvironmentVariable("STREAMHELPER_DONATEX_URL")), OnUi<Donation>(OnDonations));
        services.DonationStatus = new DonationStatusHub(
            (DonationSources.DonationAlerts, services.Poller), (DonationSources.DonatePay, services.DonatePayPoller),
            (DonationSources.DonateX, services.DonateXPoller));
        services.DonationStatus.Changed += status => Dispatcher.InvokeAsync(() => viewModel.ApplyDonationStatus(status));
        viewModel.ApplyDonationStatus(services.DonationStatus.Combined);

        services.FollowerPoller = new FollowerPoller(settings, twitch, OnUi<Follower>(OnFollowers));
        Track(services.FollowerPoller, viewModel.ApplyFollowerStatus);
        services.SubscriptionListener = new SubscriptionListener(settings, twitch, eventSubUrl, OnUi<Subscriber>(OnSubscribers));
        Track(services.SubscriptionListener, viewModel.ApplySubscriberStatus);
        services.RewardListener = new RewardListener(
            settings, twitch, eventSubUrl,
            OnUi<Redemption>(items => OnRedemptions(items.Where(r => settings.Current.AllowsReward(r.RewardId)).ToList())));
        Track(services.RewardListener, viewModel.ApplyRewardStatus);
        services.PingListener = new PingListener(
            settings, twitch, eventSubUrl, OnUi<ChatPing>(OnPings), services.ChatFeed.Push,
            moment => Dispatcher.InvokeAsync(() => OnMoments(new[] { moment })));
        Track(services.PingListener, viewModel.ApplyPingStatus);
        Track(services.PingListener, viewModel.ApplyMomentStatus);
    }

    private void CreateTray(Services services)
    {
        var viewModel = services.ViewModel;
        var tray = new TrayIcon(
            () => services.Overlay.Toggle(),
            () => services.Chat.ToggleVisible(),
            services.OpenSettings,
            () =>
            {
                services.Overlay.SaveBounds();
                Shutdown();
            });
        _tray = tray;
        tray.SetUnseen(viewModel.TotalUnseen);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.TotalUnseen)) tray.SetUnseen(viewModel.TotalUnseen);
        };
    }

    private Func<IReadOnlyList<T>, Task> OnUi<T>(Action<IReadOnlyList<T>> handle) =>
        items => Dispatcher.InvokeAsync(() => handle(items)).Task;

    private void Track(ISyncWorker worker, Action<SyncStatus> apply)
    {
        worker.StatusChanged += status => Dispatcher.InvokeAsync(() => apply(status));
        apply(worker.Status);
    }

    private static bool WaitForPreviousInstance(Mutex mutex)
    {
        try
        {
            return mutex.WaitOne(TimeSpan.FromSeconds(60));
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    private bool Restart()
    {
        var exe = _exePath;
        if (string.IsNullOrEmpty(exe)) return false;
        var start = new System.Diagnostics.ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in _args.Where(a => a != RestartFlag)) start.ArgumentList.Add(arg);
        start.ArgumentList.Add(RestartFlag);
        try
        {
            System.Diagnostics.Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Write("Restart failed: " + ex.Message);
            return false;
        }
        Log.Write("Restarting.");
        _services?.Overlay.SaveBounds();
        Shutdown();
        return true;
    }

    private static void OnHotkey(Services services, HotkeyAction action)
    {
        var tab = HotkeyActions.TabIndex(action);
        if (tab >= 0) services.Overlay.ToggleTab(tab);
        else if (action == HotkeyAction.Settings) services.ToggleSettings();
        else if (action == HotkeyAction.HideToasts) services.Toasts.HideNow();
        else if (action == HotkeyAction.ChatInteract) services.Chat.ToggleInteractive();
        else if (action == HotkeyAction.ChatToggle) services.Chat.ToggleVisible();
        else if (action == HotkeyAction.RewardSoundsMute) services.RewardAlert.ToggleMuted();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_services != null)
        {
            foreach (var worker in _services.Workers) worker.Stop();
        }
        _services?.Hotkey.Dispose();
        _services?.Toasts.Close();
        _services?.Chat.Close();
        _services?.RewardAlert.Stop();
        _services?.MuteBadge.Close();
        _services?.Updates.Stop();
        _tray?.Dispose();
        _http?.Dispose();
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch
        {
        }
        base.OnExit(e);
    }

    private void Deliver<T>(
        Func<Services, EventStore<T>> store, IEnumerable<T> items, Func<AppSettings, bool> notify,
        Func<IReadOnlyList<T>, ToastText> toast, Action<IReadOnlyList<T>>? onAdded = null)
        where T : class, ISeenItem =>
        Deliver(store, items, (settings, _) => notify(settings), toast, onAdded);

    private void Deliver<T>(
        Func<Services, EventStore<T>> store, IEnumerable<T> items, Func<AppSettings, T, bool> notify,
        Func<IReadOnlyList<T>, ToastText> toast, Action<IReadOnlyList<T>>? onAdded = null)
        where T : class, ISeenItem
    {
        var services = _services;
        if (services == null) return;

        var added = store(services).AddRange(items);
        if (added.Count == 0) return;
        onAdded?.Invoke(added);

        var settings = services.Settings.Current;
        var shown = added.Where(item => notify(settings, item)).ToList();
        if (shown.Count == 0 || !settings.ShowToast || services.Overlay.IsOpen) return;
        var text = toast(shown);
        services.Toasts.Show(text.Title, text.Line, text.Message);
    }

    private void OnDonations(IReadOnlyList<Donation> items) =>
        Deliver(s => s.Donations, items, _ => true, EventToasts.Donations);

    private void OnFollowers(IReadOnlyList<Follower> items) =>
        Deliver(s => s.Followers, items, s => s.NotifyFollowers, EventToasts.Followers);

    private void OnSubscribers(IReadOnlyList<Subscriber> items) =>
        Deliver(s => s.Subscribers, items, s => s.NotifySubscribers, EventToasts.Subscribers);

    private void OnPings(IReadOnlyList<ChatPing> items) =>
        Deliver(s => s.Pings, items, s => s.NotifyPings, EventToasts.Pings);

    private void OnMoments(IReadOnlyList<ChannelMoment> items) =>
        Deliver(s => s.Moments, items, EventToasts.Notifies, EventToasts.Moments);

    private void OnRedemptions(IReadOnlyList<Redemption> items)
    {
        var services = _services;
        if (services == null) return;

        var settings = services.Settings.Current;
        var (listed, soundOnly) = RedemptionRouting.Split(settings, items);
        PlaySounds(services, soundOnly);
        foreach (var item in listed) item.CanManage = settings.ManagedRewardIds.Contains(item.RewardId);
        Deliver(s => s.Redemptions, listed, s => s.NotifyRewards, EventToasts.Redemptions, added => PlaySounds(services, added));
    }

    private static void PlaySounds(Services services, IEnumerable<Redemption> redemptions)
    {
        foreach (var rewardId in redemptions.Select(r => r.RewardId).Distinct()) services.RewardAlert.PlayOrdered(rewardId);
    }

    private static DaEndpoints EndpointsFromEnvironment()
    {
        var baseUrl = Environment.GetEnvironmentVariable("STREAMHELPER_DA_URL");
        if (string.IsNullOrWhiteSpace(baseUrl)) return DaEndpoints.Default;
        baseUrl = baseUrl.TrimEnd('/');
        return new DaEndpoints(baseUrl + "/oauth/authorize", baseUrl + "/oauth/token", baseUrl + "/api/v1/alerts/donations");
    }

    private static TwitchEndpoints TwitchEndpointsFromEnvironment()
    {
        var baseUrl = Environment.GetEnvironmentVariable("STREAMHELPER_TWITCH_URL");
        return string.IsNullOrWhiteSpace(baseUrl) ? TwitchEndpoints.Default : TwitchEndpoints.FromBase(baseUrl);
    }

    private static string? ReadOption(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void SeedDemo(
        DonationStore store, FollowerStore followers, SubscriberStore subscribers, RedemptionStore redemptions, PingStore pings,
        MomentStore moments)
    {
        var now = DateTime.UtcNow;
        if (moments.Items.Count == 0)
        {
            moments.AddRange(new[]
            {
                new ChannelMoment { Key = "mo-1", Kind = MomentKind.Streak, Login = "anna_k", DisplayName = "Anna_K", StreakCount = 5, ChannelPoints = 450, Message = "Пятый стрим подряд!", AtUtc = now.AddMinutes(-20), Seen = true },
                new ChannelMoment { Key = "mo-2", Kind = MomentKind.Raid, Login = "big_streamer", DisplayName = "Big_Streamer", Viewers = 128, AtUtc = now.AddMinutes(-4) },
            });
        }
        if (pings.Items.Count == 0)
        {
            pings.AddRange(new[]
            {
                new ChatPing { Key = "pg-1", Login = "anna_k", DisplayName = "Anna_K", Message = "@streamer эй, посмотри на это!", AtUtc = now.AddMinutes(-14), Seen = true },
                new ChatPing { Key = "pg-2", Login = "test_viewer", DisplayName = "Test_Viewer", Message = "@streamer ты тут? Ответь, пожалуйста", AtUtc = now.AddMinutes(-3) },
            });
        }
        if (redemptions.Items.Count == 0)
        {
            redemptions.AddRange(new[]
            {
                new Redemption { Key = "rd-1", Login = "anna_k", DisplayName = "Anna_K", RewardTitle = "Нарисовать эмодзи", Cost = 2500, UserInput = "Котик в шляпе, синий фон", AtUtc = now.AddMinutes(-25), Seen = true },
                new Redemption { Key = "rd-2", Login = "test_viewer", DisplayName = "Test_Viewer", RewardTitle = "Заказать трек", Cost = 500, UserInput = "Сыграй что-нибудь из Hollow Knight", AtUtc = now.AddMinutes(-6) },
                new Redemption { Key = "rd-3", Login = "mark", DisplayName = "Mark", RewardTitle = "Показать вебкамеру крупно", Cost = 100, AtUtc = now.AddMinutes(-1) },
            });
        }
        if (subscribers.Items.Count == 0)
        {
            subscribers.AddRange(new[]
            {
                new Subscriber { Key = "demo-1", Kind = SubscriptionKind.Resub, Login = "old_friend", DisplayName = "Old_Friend", Tier = "2000", Months = 7, StreakMonths = 4, Message = "Спасибо за стримы!", AtUtc = now.AddMinutes(-30), Seen = true },
                new Subscriber { Key = "demo-2", Kind = SubscriptionKind.New, Login = "newbie", DisplayName = "Newbie", Tier = "1000", AtUtc = now.AddMinutes(-8) },
                new Subscriber { Key = "demo-3", Kind = SubscriptionKind.Gift, IsAnonymous = true, Tier = "1000", GiftTotal = 5, AtUtc = now.AddMinutes(-2) },
            });
        }
        if (followers.Items.Count == 0)
        {
            followers.AddRange(new[]
            {
                new Follower { UserId = "1", Login = "nastya_plays", DisplayName = "Nastya_Plays", FollowedAtUtc = now.AddMinutes(-50), Seen = true },
                new Follower { UserId = "2", Login = "xx_viewer_xx", DisplayName = "xX_Viewer_Xx", FollowedAtUtc = now.AddMinutes(-9) },
                new Follower { UserId = "3", Login = "lisa", DisplayName = "Lisa", FollowedAtUtc = now.AddMinutes(-1) },
            });
        }
        if (store.Items.Count > 0) return;
        store.AddRange(new[]
        {
            new Donation { Id = 1, Username = "Ivan", Amount = 500, Currency = "RUB", Message = "Заказ: логотип для канала, синий, минимализм. Ссылка на референс ниже.", CreatedAtUtc = now.AddMinutes(-95) },
            new Donation { Id = 2, Username = "Anna_K", Amount = 1500, Currency = "RUB", Message = "Хочу эмоуты для твича, 5 штук", CreatedAtUtc = now.AddMinutes(-40), Seen = true },
            new Donation { Id = 3, Username = "Mark", Amount = 10, Currency = "USD", Message = "", CreatedAtUtc = now.AddMinutes(-12) },
            new Donation { Id = 4, Username = "Очень длинное имя зрителя которое не помещается", Amount = 250.5m, Currency = "RUB", Message = "Баннер для шапки профиля", CreatedAtUtc = now.AddMinutes(-3) },
        });
        store.Items[3].Done = true;
    }
}
