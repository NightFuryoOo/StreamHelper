using System.Net;
using System.Text.Json;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Tests;

public class ChatSendTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir, string scopes = "user:read:chat user:write:chat")
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return settings;
    }

    private static JsonElement Body(string request) => JsonDocument.Parse(request[(request.IndexOf("body=", StringComparison.Ordinal) + 5)..]).RootElement;

    [Fact]
    public void The_login_asks_for_the_right_to_write_in_chat()
    {
        Assert.Contains("user:write:chat", TwitchClient.Scopes);
        Assert.True(new AppSettings { TwitchScopes = TwitchClient.Scopes }.HasChatWriteScope);
        Assert.False(new AppSettings { TwitchScopes = "user:read:chat" }.HasChatWriteScope);
        Assert.Equal("Чат: написать сообщение", HotkeyActions.Title(HotkeyAction.ChatWrite));
        Assert.Equal(-1, HotkeyActions.TabIndex(HotkeyAction.ChatWrite));
        Assert.Contains(nameof(AppSettings.ChatTypingMode), Profile.SharedSettings);
        Assert.False(new AppSettings().ChatTypingMode);
    }

    [Fact]
    public async Task A_message_goes_to_the_chosen_channel_from_the_signed_in_user_trimmed()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (200, "{\"data\":[{\"message_id\":\"m-1\",\"is_sent\":true,\"drop_reason\":null}]}");
        var settings = Ready(dir);
        settings.Current.SelectChannel("555", "big_streamer", "Big_Streamer");
        var client = new TwitchClient(settings, NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.SendChatMessageAsync("  привет, чат  ", null, CancellationToken.None);

        Assert.True(result.Success);
        var request = Assert.Single(da.Requests);
        Assert.StartsWith("POST /helix/chat/messages ", request);
        var body = Body(request);
        Assert.Equal("555", body.GetProperty("broadcaster_id").GetString());
        Assert.Equal("777", body.GetProperty("sender_id").GetString());
        Assert.Equal("привет, чат", body.GetProperty("message").GetString());
        Assert.False(body.TryGetProperty("reply_parent_message_id", out _));
    }

    [Fact]
    public async Task A_reply_names_the_message_it_answers()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (200, "{\"data\":[{\"message_id\":\"m-2\",\"is_sent\":true}]}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.True((await client.SendChatMessageAsync("ответ", "parent-9", CancellationToken.None)).Success);

        var body = Body(Assert.Single(da.Requests));
        Assert.Equal("parent-9", body.GetProperty("reply_parent_message_id").GetString());
        Assert.Equal("777", body.GetProperty("broadcaster_id").GetString());
    }

    [Fact]
    public async Task Without_the_right_empty_or_too_long_text_nothing_is_sent()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var noRight = new TwitchClient(Ready(dir, "user:read:chat"), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.Contains("переподключи Twitch", (await noRight.SendChatMessageAsync("hi", null, CancellationToken.None)).Message);
        Assert.Equal(new ChatSendResult(false, ""), await client.SendChatMessageAsync("   ", null, CancellationToken.None));
        Assert.Contains("до 500 знаков", (await client.SendChatMessageAsync(new string('a', 501), null, CancellationToken.None)).Message);
        Assert.Empty(da.Requests);
    }

    [Theory]
    [InlineData("msg_duplicate", "", "Twitch не пропустил: такое же сообщение уже было только что.")]
    [InlineData("msg_slowmode", "", "В чате медленный режим: подожди немного.")]
    [InlineData("msg_subsonly", "", "Чат только для подписчиков.")]
    [InlineData("msg_followersonly", "", "Чат только для фолловеров.")]
    [InlineData("msg_banned", "", "Тебя забанили в этом чате.")]
    [InlineData("something_new", "You cannot do that.", "Twitch не пропустил: You cannot do that.")]
    public async Task A_dropped_message_says_why(string code, string message, string expected)
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (200, "{\"data\":[{\"message_id\":\"\",\"is_sent\":false,\"drop_reason\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\"}}]}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.SendChatMessageAsync("hi", null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expected, result.Message);
    }

    [Theory]
    [InlineData(403, "Twitch не разрешил писать в этот чат.")]
    [InlineData(429, "Слишком часто: подожди немного.")]
    [InlineData(422, "Слишком длинное сообщение: Twitch принимает до 500 знаков.")]
    [InlineData(500, "Twitch: HTTP 500.")]
    public async Task A_refused_request_is_explained(int status, string expected)
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (status, "{\"message\":\"x\"}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        Assert.Equal(expected, (await client.SendChatMessageAsync("hi", null, CancellationToken.None)).Message);
    }

    [Fact]
    public void An_odd_answer_is_not_taken_for_success()
    {
        Assert.False(TwitchClient.ParseChatSend("{\"data\":[]}").Success);
        Assert.False(TwitchClient.ParseChatSend("not json").Success);
        Assert.Equal("Twitch отказал: bad", TwitchClient.DescribeChatSendFailure(HttpStatusCode.BadRequest, "bad"));
    }
}
