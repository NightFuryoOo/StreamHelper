using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed class PingListener : EventSubListener<ChatPing>
{
    public static readonly string[] PingTypes = { EventSubParser.ChatMessageType };

    private static readonly EventSubFeature Feature = new(
        "Пинги",
        TwitchClient.ChatScope,
        PingTypes,
        "Для пингов в чате переподключи Twitch в настройках, вкладка «Twitch»: нужно новое право.");

    private readonly SettingsStore _settings;
    private readonly Action<ChatMessage>? _onChat;

    public PingListener(
        SettingsStore settings, IEventSubApi api, string url, Func<IReadOnlyList<ChatPing>, Task> deliver,
        Action<ChatMessage>? onChat = null)
        : base(settings, api, url, Feature, deliver)
    {
        _settings = settings;
        _onChat = onChat;
    }

    protected override ChatPing? Select(EventSubMessage message)
    {
        if (message.Chat is not { } chat) return null;
        try
        {
            _onChat?.Invoke(chat);
        }
        catch (Exception ex)
        {
            Log.Write("Chat window failed: " + ex.Message);
        }
        var settings = _settings.Current;
        var ignored = (IEnumerable<string>?)settings.PingIgnoredChatters ?? AppSettings.DefaultPingIgnored;
        if (!PingDetector.IsPing(chat, settings.TwitchUserId, settings.TwitchLogin, ignored, settings.PingWords)) return null;

        return new ChatPing
        {
            Key = chat.MessageId.Length > 0 ? chat.MessageId : message.MessageId,
            Login = chat.ChatterLogin,
            DisplayName = chat.ChatterName,
            Message = chat.Text,
            AtUtc = chat.AtUtc,
        };
    }
}