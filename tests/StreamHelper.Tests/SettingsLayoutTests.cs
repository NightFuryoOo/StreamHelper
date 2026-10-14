using StreamHelper.Storage;
using StreamHelper.Sync;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class SettingsLayoutTests
{
    private static AppSettings Connected(string page = "")
    {
        var settings = new AppSettings { TwitchUserId = "1", TwitchLogin = "me", SettingsPage = page };
        settings.TwitchRefreshToken = "refresh";
        return settings;
    }

    [Theory]
    [InlineData("events", "events")]
    [InlineData("rewards", "rewards")]
    [InlineData("chat", "chat")]
    [InlineData("general", "general")]
    [InlineData("connections", "connections")]
    [InlineData("", "connections")]
    [InlineData("7", "connections")]
    public void The_settings_open_on_the_last_page_and_unknown_pages_fall_back_to_connections(string saved, string opened)
    {
        Assert.Equal(opened, SettingsWindow.StartPage(Connected(saved), profileNotice: false));
    }

    [Fact]
    public void Without_any_account_the_settings_open_on_connections_and_a_loaded_profile_opens_general()
    {
        Assert.Equal(SettingsWindow.ConnectionsPage, SettingsWindow.StartPage(new AppSettings { SettingsPage = "chat" }, profileNotice: false));
        Assert.Equal(SettingsWindow.GeneralPage, SettingsWindow.StartPage(Connected("chat"), profileNotice: true));
    }

    [Fact]
    public void The_open_page_is_kept_in_the_settings_file()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.File("settings.json"));
        store.Current.SettingsPage = SettingsWindow.RewardsPage;
        store.Save();

        Assert.Equal("rewards", new SettingsStore(dir.File("settings.json")).Current.SettingsPage);
    }

    [Fact]
    public void The_channel_line_says_what_works_on_the_chosen_channel()
    {
        var own = Connected();
        var moderated = Connected();
        moderated.TwitchChannelId = "2";
        moderated.TwitchChannelName = "Friend";
        var viewer = Connected();
        viewer.TwitchChannelId = "3";
        viewer.TwitchChannelAsViewer = true;

        Assert.Equal("свой канал", SettingsWindow.ChannelRole(own).Kind);
        Assert.Contains("Работает всё", SettingsWindow.ChannelRole(own).Note);
        Assert.Equal("модератор", SettingsWindow.ChannelRole(moderated).Kind);
        Assert.Contains("бан и мут", SettingsWindow.ChannelRole(moderated).Note);
        Assert.Equal("как зритель", SettingsWindow.ChannelRole(viewer).Kind);
        Assert.Equal("Только чат поверх игры и сообщения в чат.", SettingsWindow.ChannelRole(viewer).Note);
    }

    [Fact]
    public void The_connections_page_names_the_chosen_channel_and_its_role_in_one_line()
    {
        var own = Connected();
        var viewer = Connected();
        viewer.TwitchChannelId = "3";
        viewer.TwitchChannelName = "Monor22";
        viewer.TwitchChannelAsViewer = true;

        Assert.Equal("me · свой канал", SettingsWindow.ChannelSummaryLine(own));
        Assert.Equal("Monor22 · как зритель", SettingsWindow.ChannelSummaryLine(viewer));
    }

    [Theory]
    [InlineData("Big_Streamer", "", true)]
    [InlineData("Big_Streamer", "  ", true)]
    [InlineData("Big_Streamer", "big", true)]
    [InlineData("Big_Streamer", " STREAM ", true)]
    [InlineData("Котик_Стримит", "котик", true)]
    [InlineData("Big_Streamer", "small", false)]
    public void The_channel_search_matches_part_of_a_name_in_any_case(string label, string query, bool matches)
    {
        Assert.Equal(matches, SettingsWindow.MatchesSearch(label, query));
    }

    [Fact]
    public void The_folded_ping_block_sums_up_its_words_ignored_accounts_and_colour()
    {
        var settings = new AppSettings { PingWords = new() { "стрим*", "фурия" }, PingIgnoredChatters = new() { "nightbot" }, ChatPingHighlight = true };
        Assert.Equal("@ник и ещё 2 слова · 1 аккаунт не считается · выделяются цветом", SettingsWindow.PingSummary(settings));

        settings = new AppSettings { PingWords = new(), PingIgnoredChatters = new() { "a", "b", "c", "d", "e" }, ChatPingHighlight = false };
        Assert.Equal("только @ник · 5 аккаунтов не считаются · без цвета", SettingsWindow.PingSummary(settings));

        settings.PingIgnoredChatters = new();
        settings.PingWords = new() { "a", "b", "c", "d", "e", "f", "g", "h", "i", "j", "k", "l", "m", "n", "o", "p", "q", "r", "s", "t", "u" };
        Assert.Equal("@ник и ещё 21 слово · все аккаунты считаются · без цвета", SettingsWindow.PingSummary(settings));
    }

    [Fact]
    public void The_eye_explains_hidden_tabs_and_keeps_the_last_one()
    {
        Assert.Contains("видна", SettingsWindow.TabEyeTip(shown: true, lastShown: false));
        Assert.Contains("скрыта", SettingsWindow.TabEyeTip(shown: false, lastShown: false));
        Assert.Equal("Хотя бы одна вкладка должна остаться", SettingsWindow.TabEyeTip(shown: true, lastShown: true));
    }

    [Theory]
    [InlineData(SyncState.Ok, "Подключено", "Заказанные видео: вкл")]
    [InlineData(SyncState.NotConnected, "Не подключено", "")]
    [InlineData(SyncState.NeedsLogin, "Нужно подключить заново", "")]
    [InlineData(SyncState.Error, "Ошибка", "Заказанные видео: нет связи")]
    public void Donation_tiles_name_their_state_in_words(SyncState state, string tile, string video)
    {
        Assert.Equal(tile, SettingsWindow.TileState(state));
        Assert.Equal(video, SettingsWindow.VideoState(state));
    }

    [Fact]
    public void The_position_buttons_say_they_set_position_and_size_and_the_help_uses_the_same_words()
    {
        var help = string.Join("\n", HelpCatalog.Articles.Select(a => a.Text));

        Assert.Equal("Настроить положение и размеры", SettingsWindow.PositionText);
        Assert.Equal(3, help.Split("«" + SettingsWindow.PositionText + "»").Length - 1);
        Assert.DoesNotContain("Выбрать положение", help);
    }

    [Fact]
    public void A_connected_donation_service_offers_to_reconnect_like_twitch()
    {
        var settings = new AppSettings();
        Assert.All(DonationServices.All, s => Assert.False(DonationServices.IsConnected(settings, s)));
        Assert.Equal("Подключить", DonationServices.ConnectText(false));
        Assert.Equal("Переподключить", DonationServices.ConnectText(true));

        settings.AccessToken = "token";
        Assert.True(DonationServices.IsConnected(settings, DonationService.DonationAlerts));
        Assert.False(DonationServices.IsConnected(settings, DonationService.DonatePay));

        settings.DonatePayKey = "key";
        settings.DonateXToken = "token";
        Assert.True(DonationServices.IsConnected(settings, DonationService.DonatePay));
        Assert.True(DonationServices.IsConnected(settings, DonationService.DonateX));
        Assert.Equal("Сначала вставь ключ API в поле выше.", DonationServices.MissingSecretText(DonationService.DonatePay));
        Assert.Equal("Сначала вставь токен в поле выше.", DonationServices.MissingSecretText(DonationService.DonateX));
    }

    [Fact]
    public void Messages_point_to_the_connections_page_instead_of_the_old_tabs()
    {
        var settings = Connected();
        settings.TwitchChannelId = "2";
        settings.TwitchChannelName = "Friend";

        Assert.Equal("Пинги: только на своём канале. Сейчас выбран канал Friend (раздел «Чат»).", settings.OnlyOwnChannelText("Пинги"));
        Assert.Contains("раздел «Подключения»", DonationStatusHub.Combine(new[] { ("DonationAlerts", new SyncStatus(SyncState.NotConnected, "")), ("DonatePay", new SyncStatus(SyncState.NotConnected, "")) }).Message);
    }
}
