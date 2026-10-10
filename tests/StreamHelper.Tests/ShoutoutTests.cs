using System.Net;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class ShoutoutLimitTests
{
    private static readonly DateTime T0 = new(2026, 10, 9, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Nothing_sent_yet_means_anyone_can_be_shouted_out()
    {
        var wait = new ShoutoutLimits().Check("42", T0);

        Assert.True(wait.Allowed);
        Assert.Equal("Отметить", ShoutoutLimits.MenuText(wait));
    }

    [Fact]
    public void After_a_shoutout_others_wait_two_minutes_and_the_same_person_an_hour()
    {
        var limits = new ShoutoutLimits();
        limits.Sent("42", T0);

        var other = limits.Check("43", T0.AddSeconds(36));
        Assert.Equal(ShoutoutBlock.Recent, other.Block);
        Assert.Equal("Отметить (можно через 1:24)", ShoutoutLimits.MenuText(other));

        var same = limits.Check("42", T0.AddMinutes(23).AddSeconds(10));
        Assert.Equal(ShoutoutBlock.SameUser, same.Block);
        Assert.Equal("Отметить (этого снова через 37 мин)", ShoutoutLimits.MenuText(same));

        Assert.True(limits.Check("43", T0.AddMinutes(2)).Allowed);
        Assert.Equal("Отметить (этого снова через 0:30)", ShoutoutLimits.MenuText(limits.Check("42", T0.AddMinutes(59).AddSeconds(30))));
        Assert.True(limits.Check("42", T0.AddHours(1)).Allowed);
    }

    [Fact]
    public void The_longer_of_the_two_waits_is_shown()
    {
        var limits = new ShoutoutLimits();
        limits.Sent("42", T0);
        limits.Sent("43", T0.AddMinutes(59).AddSeconds(50));

        var wait = limits.Check("42", T0.AddMinutes(59).AddSeconds(55));

        Assert.Equal(ShoutoutBlock.Recent, wait.Block);
        Assert.Equal("Отметить (можно через 1:55)", ShoutoutLimits.MenuText(wait));
    }
}

public class ShoutoutApiTests
{
    private static HttpClient NewHttp() => new() { Timeout = TimeSpan.FromSeconds(10) };

    private static SettingsStore Ready(TempDir dir, string scopes = "user:read:chat moderator:manage:shoutouts")
    {
        var settings = MakeFollower.Settings(dir);
        settings.Current.TwitchScopes = scopes;
        return settings;
    }

    [Fact]
    public void The_login_asks_for_the_shoutout_right()
    {
        Assert.Contains("moderator:manage:shoutouts", TwitchClient.Scopes);
        Assert.True(new AppSettings { TwitchScopes = TwitchClient.Scopes }.HasShoutoutScope);
        Assert.False(new AppSettings { TwitchScopes = "user:read:chat moderator:manage:banned_users" }.HasShoutoutScope);
    }

    [Fact]
    public async Task A_shoutout_is_a_post_from_the_channel_to_the_chatter_with_the_channel_as_moderator()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (204, "");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.ShoutoutAsync("42", CancellationToken.None);

        Assert.True(result.Success);
        var request = Assert.Single(da.Requests);
        Assert.StartsWith("POST /helix/chat/shoutouts?from_broadcaster_id=777&to_broadcaster_id=42&moderator_id=777 ", request);
        Assert.EndsWith("body=", request);
    }

    [Fact]
    public async Task Without_the_right_nothing_is_sent()
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        var client = new TwitchClient(Ready(dir, "user:read:chat moderator:manage:banned_users"), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.ShoutoutAsync("42", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("переподключить Twitch", result.Message);
        Assert.Empty(da.Requests);
    }

    [Theory]
    [InlineData(400, "The broadcaster is not streaming live or does not have one or more viewers.", "Отметить можно только во время стрима, когда на нём есть зрители.")]
    [InlineData(400, "The broadcaster may not give themselves a Shoutout.", "Себя отметить нельзя.")]
    [InlineData(403, "The broadcaster may not send the specified broadcaster a Shoutout.", "Twitch не разрешает отметить этого пользователя.")]
    [InlineData(403, "The user in moderator_id is not one of the broadcaster's moderators.", "Twitch не разрешил: подключи Twitch аккаунтом владельца канала.")]
    [InlineData(429, "The broadcaster exceeded the number of Shoutouts they may send within a given window.", "Отмечать можно раз в 2 минуты, подожди.")]
    [InlineData(429, "The broadcaster exceeded the number of Shoutouts they may send the same broadcaster within a given window.", "Этого уже отмечали за последний час.")]
    public async Task A_refusal_is_explained(int status, string message, string expected)
    {
        using var dir = new TempDir();
        using var da = new MockDa();
        da.Handler = (_, _) => (status, "{\"error\":\"x\",\"status\":" + status + ",\"message\":\"" + message + "\"}");
        var client = new TwitchClient(Ready(dir), NewHttp(), TwitchEndpoints.FromBase(da.BaseUrl));

        var result = await client.ShoutoutAsync("42", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(expected, result.Message);
    }

    [Fact]
    public void Other_refusals_keep_what_twitch_said()
    {
        Assert.Equal("Twitch отказал: odd", TwitchClient.DescribeShoutoutFailure(HttpStatusCode.BadRequest, "odd"));
        Assert.Equal("Twitch: HTTP 500.", TwitchClient.DescribeShoutoutFailure(HttpStatusCode.InternalServerError, ""));
    }
}
