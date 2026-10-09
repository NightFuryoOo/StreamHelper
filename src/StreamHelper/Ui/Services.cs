using System;
using System.Collections.Generic;
using System.Windows;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public sealed class Services
{
    private SettingsWindow? _settingsWindow;

    public required SettingsStore Settings { get; init; }
    public required DonationStore Donations { get; init; }
    public required FollowerStore Followers { get; init; }
    public required SubscriberStore Subscribers { get; init; }
    public required RedemptionStore Redemptions { get; init; }
    public required PingStore Pings { get; init; }
    public required DonationAlertsClient Client { get; init; }
    public required TwitchClient Twitch { get; init; }
    public required HotkeyService Hotkey { get; init; }
    public required ToastService Toasts { get; init; }
    public required ChatFeed ChatFeed { get; init; }
    public required ChatOverlayService Chat { get; init; }
    public required RewardAlertService RewardAlert { get; init; }
    public required MuteBadgeService MuteBadge { get; init; }
    public required MainViewModel ViewModel { get; init; }

    public DonationPoller Poller { get; set; } = null!;
    public DonatePayPoller DonatePayPoller { get; set; } = null!;
    public DonateXPoller DonateXPoller { get; set; } = null!;
    public DonationStatusHub DonationStatus { get; set; } = null!;
    public FollowerPoller FollowerPoller { get; set; } = null!;
    public SubscriptionListener SubscriptionListener { get; set; } = null!;
    public RewardListener RewardListener { get; set; } = null!;
    public PingListener PingListener { get; set; } = null!;
    public MainWindow Overlay { get; set; } = null!;

    public IReadOnlyList<ISyncWorker> Workers =>
        new ISyncWorker[] { Poller, DonatePayPoller, DonateXPoller, FollowerPoller, SubscriptionListener, RewardListener, PingListener };
    public Action<IReadOnlyList<Donation>> HandleDonations { get; set; } = _ => { };
    public Action<IReadOnlyList<Follower>> HandleFollowers { get; set; } = _ => { };
    public Action<IReadOnlyList<Subscriber>> HandleSubscribers { get; set; } = _ => { };
    public Action<IReadOnlyList<Redemption>> HandleRedemptions { get; set; } = _ => { };
    public Action<IReadOnlyList<ChatPing>> HandlePings { get; set; } = _ => { };
    public Func<bool> RestartApp { get; set; } = () => false;
    public ProfileNotice? ProfileNotice { get; set; }

    private static readonly (string Name, decimal Amount, string Message)[] TestSamples =
    {
        ("Тестовый зритель", 500, "Нужен логотип для канала, синий, минимализм"),
        ("Test_Anna", 1500, "Хочу эмоуты для твича, 5 штук"),
        ("Mark", 250, "Баннер для шапки профиля"),
        ("Очень_Длинное_Имя_Зрителя", 100, ""),
    };

    private static readonly string[] TestFollowerNames = { "TestFollower", "Nastya_Plays", "xX_Viewer_Xx", "Lisa" };

    public void AddTestDonation()
    {
        var sample = TestSamples[Random.Shared.Next(TestSamples.Length)];
        HandleDonations(new[]
        {
            new Donation
            {
                Id = -DateTime.UtcNow.Ticks,
                Username = sample.Name,
                Amount = sample.Amount,
                Currency = "RUB",
                Message = sample.Message.Length == 0 ? "" : "[тест] " + sample.Message,
                CreatedAtUtc = DateTime.UtcNow,
            },
        });
    }

    public void AddTestFollower()
    {
        var name = TestFollowerNames[Random.Shared.Next(TestFollowerNames.Length)];
        HandleFollowers(new[]
        {
            new Follower
            {
                UserId = "test-" + DateTime.UtcNow.Ticks,
                Login = name.ToLowerInvariant(),
                DisplayName = name,
                FollowedAtUtc = DateTime.UtcNow,
            },
        });
    }

    private static readonly (string Name, string Reward, long Cost, string Input)[] TestRedemptions =
    {
        ("Test_Viewer", "Заказать трек", 500, "Сыграй что-нибудь из Hollow Knight"),
        ("Anna_K", "Нарисовать эмодзи", 2500, "Котик в шляпе, синий фон"),
        ("Mark", "Показать вебкамеру крупно", 100, ""),
    };

    public void AddTestRedemption()
    {
        var sample = TestRedemptions[Random.Shared.Next(TestRedemptions.Length)];
        var stamp = DateTime.UtcNow;
        HandleRedemptions(new[]
        {
            new Redemption
            {
                Key = "test-" + stamp.Ticks,
                Login = sample.Name.ToLowerInvariant(),
                DisplayName = sample.Name,
                RewardTitle = sample.Reward,
                Cost = sample.Cost,
                UserInput = sample.Input.Length == 0 ? "" : "[тест] " + sample.Input,
                AtUtc = stamp,
            },
        });
    }

    public async System.Threading.Tasks.Task RefreshManagedRewardsAsync()
    {
        var settings = Settings.Current;
        if (!settings.HasTwitchTokens || !settings.HasManageScope) return;
        try
        {
            var ids = await Twitch.GetManageableRewardIdsAsync(System.Threading.CancellationToken.None);
            settings.ManagedRewardIds = new List<string>(ids);
            Settings.Save();
        }
        catch (Exception ex)
        {
            Log.Write("Refreshing managed rewards failed: " + ex.Message);
        }
    }

    private static readonly (string Login, string Name, string Text)[] TestPings =
    {
        ("test_viewer", "Test_Viewer", "{0}, привет! Как дела?"),
        ("anna_k", "Anna_K", "Эй, {0}, посмотри на этот момент в игре"),
        ("mark", "Mark", "{0} ты тут? Ответь, пожалуйста"),
    };

    public void AddTestPing()
    {
        var sample = TestPings[Random.Shared.Next(TestPings.Length)];
        var login = Settings.Current.TwitchLogin;
        var mention = "@" + (string.IsNullOrWhiteSpace(login) ? "streamer" : login);
        var stamp = DateTime.UtcNow;
        HandlePings(new[]
        {
            new ChatPing
            {
                Key = "test-" + stamp.Ticks,
                Login = sample.Login,
                DisplayName = sample.Name,
                Message = "[тест] " + string.Format(sample.Text, mention),
                AtUtc = stamp,
            },
        });
    }

    public void AddTestSubscriber()
    {
        var stamp = DateTime.UtcNow;
        var key = "test-" + stamp.Ticks;
        var subscriber = Random.Shared.Next(3) switch
        {
            0 => new Subscriber { Key = key, Kind = SubscriptionKind.New, Login = "test_sub", DisplayName = "Test_Sub", Tier = "1000", AtUtc = stamp },
            1 => new Subscriber
            {
                Key = key, Kind = SubscriptionKind.Resub, Login = "old_friend", DisplayName = "Old_Friend", Tier = "2000",
                Months = 7, StreakMonths = 4, Message = "[тест] Спасибо за стримы!", AtUtc = stamp,
            },
            _ => new Subscriber { Key = key, Kind = SubscriptionKind.Gift, IsAnonymous = true, Tier = "1000", GiftTotal = 5, AtUtc = stamp },
        };
        HandleSubscribers(new[] { subscriber });
    }

    public void ToggleSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Close();
            return;
        }
        OpenSettings();
    }

    public void OpenSettings()
    {
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }
}
