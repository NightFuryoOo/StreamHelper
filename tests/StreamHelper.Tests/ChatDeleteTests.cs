using System.Net;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Tests;

public class ChatDeleteTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir, string scopes = "user:read:chat moderator:manage:chat_messages")
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return settings;
    }

    [Fact]
    public void The_login_asks_for_the_right_to_delete_messages()
    {
        Assert.Contains("moderator:manage:chat_messages", TwitchClient.Scopes);
        Assert.True(new AppSettings { TwitchScopes = TwitchClient.Scopes }.HasDeleteMessagesScope);
        Assert.False(new AppSettings { TwitchScopes = "user:read:chat" }.HasDeleteMessagesScope);
    }

    [Fact]
    public async Task One_message_is_deleted_by_its_id_in_the_chosen_channel()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (204, "");
        var settings = Ready(dir);
        settings.Current.SelectChannel("555", "big_streamer", "Big_Streamer");
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.DeleteChatMessageAsync("abc-123", CancellationToken.None);

        Assert.True(result.Success);
        Assert.StartsWith("DELETE /helix/moderation/chat?broadcaster_id=555&moderator_id=777&message_id=abc-123 ", Assert.Single(da.Requests));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Without_a_message_id_nothing_is_sent_because_twitch_would_clear_the_whole_chat(string id)
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (204, "");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.DeleteChatMessageAsync(id, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Empty(da.Requests);
    }

    [Fact]
    public async Task Without_the_right_nothing_is_sent()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var client = new TwitchClient(Ready(dir, "user:read:chat"), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.Contains("переподключить Twitch", (await client.DeleteChatMessageAsync("abc", CancellationToken.None)).Message);
        Assert.Empty(da.Requests);
    }

    [Theory]
    [InlineData(400, "You may not delete another moderator's messages.", "Сообщения других модераторов удалять нельзя.")]
    [InlineData(400, "You may not delete the broadcaster's messages.", "Сообщения стримера удалять нельзя.")]
    [InlineData(404, "", "Сообщение уже удалено или ему больше 6 часов.")]
    [InlineData(403, "", "Twitch не разрешил: подключи Twitch аккаунтом владельца канала.")]
    [InlineData(429, "", "Слишком часто, подожди немного.")]
    public async Task A_refusal_is_explained(int status, string message, string expected)
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (status, "{\"message\":\"" + message + "\"}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.Equal(expected, (await client.DeleteChatMessageAsync("abc", CancellationToken.None)).Message);
    }

    [Fact]
    public void On_someone_elses_channel_a_403_means_you_are_not_a_moderator()
    {
        Assert.Equal("Twitch не разрешил: ты не модератор канала Big.", TwitchClient.DescribeDeleteFailure(HttpStatusCode.Forbidden, "", "Big"));
    }
}
