using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private const string ConnectTwitchFirst = "Сначала подключи Twitch во вкладке «Twitch».";

    private CancellationTokenSource? _twitchCts;

    private static string ReconnectTwitchFor(string right) => $"Переподключи Twitch во вкладке «Twitch»: нужно право на {right}.";

    private void RefreshTwitchSummary()
    {
        var settings = _services.Settings.Current;
        var chatRights = !settings.HasModerationScope && !settings.HasShoutoutScope ? "бана, мута и отметок"
            : !settings.HasModerationScope ? "бана и мута"
            : !settings.HasShoutoutScope ? "отметок"
            : "";
        ChatModerationText.Text = $"Для {chatRights} переподключи Twitch (вкладка «Twitch»): нужно новое право.";
        ChatModerationText.Visibility = chatRights.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!settings.HasTwitchTokens)
        {
            TwitchStatusText.Text = "Twitch не подключён.";
            TwitchStatusDot.Fill = Resource("MutedBrush");
            TwitchRightsText.Visibility = Visibility.Collapsed;
            RefreshChannelBlock();
            return;
        }

        TwitchStatusText.Text = settings.ConnectedText;
        TwitchStatusDot.Fill = Resource("OkBrush");
        var missing = new List<string>();
        if (!settings.HasSubscriptionScope) missing.Add("подписки");
        if (!settings.HasRedemptionScope) missing.Add("награды за баллы");
        if (!settings.HasManageScope) missing.Add("управление наградами");
        if (!settings.HasChatScope) missing.Add("чат (пинги)");
        if (!settings.HasPollScope) missing.Add("опросы");
        if (!settings.HasPredictionScope) missing.Add("предикты");
        if (!settings.HasShoutoutScope) missing.Add("отметки");
        if (!settings.HasModeratedChannelsScope) missing.Add("каналы, где ты модератор");
        RefreshChannelBlock();
        if (missing.Count == 0)
        {
            TwitchRightsText.Visibility = Visibility.Collapsed;
            return;
        }
        TwitchRightsText.Text = $"Не хватает прав: {string.Join(", ", missing)}. Нажми «Подключить Twitch» ещё раз.";
        TwitchRightsText.Visibility = Visibility.Visible;
    }

    private void ShowTwitchError(string message)
    {
        TwitchStatusText.Text = message;
        TwitchStatusDot.Fill = Resource("DangerBrush");
    }

    private void RestartTwitchSync() => _services.RestartTwitchSync();

    private async void OnTwitchConnect(object sender, RoutedEventArgs e)
    {
        TwitchConnectButton.IsEnabled = false;
        _twitchCts?.Cancel();
        var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        _twitchCts = cts;
        try
        {
            TwitchStatusText.Text = "Запрашиваю код у Twitch…";
            var info = await _services.Twitch.StartDeviceFlowAsync(cts.Token);
            TwitchCodeBox.Text = info.UserCode;
            TwitchCodePanel.Visibility = Visibility.Visible;
            TwitchStatusText.Text = "Подтверди вход на странице Twitch (она открылась в браузере) и введи код.";
            OpenInBrowser(info.VerificationUri);

            await _services.Twitch.CompleteDeviceFlowAsync(info, cts.Token);
            TwitchCodePanel.Visibility = Visibility.Collapsed;
            _twitchCts = null;
            _moderated = null;
            RefreshTwitchSummary();
            RestartTwitchSync();
            _ = _services.RefreshManagedRewardsAsync();
            _ = LoadModeratedChannelsAsync();
        }
        catch (OperationCanceledException)
        {
            ShowTwitchError("Подключение Twitch отменено или истекло время ожидания.");
        }
        catch (Exception ex)
        {
            Log.Write("Twitch connect failed: " + ex.Message);
            ShowTwitchError(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_twitchCts, cts)) _twitchCts = null;
            TwitchConnectButton.IsEnabled = true;
        }
    }

    private void OnCopyTwitchCode(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(TwitchCodeBox.Text);
        }
        catch (Exception ex)
        {
            Log.Write("Clipboard failed: " + ex.Message);
        }
    }

    private void OnTwitchDisconnect(object sender, RoutedEventArgs e)
    {
        _twitchCts?.Cancel();
        _services.ChangeChannel(null);
        _moderated = null;
        var settings = _services.Settings.Current;
        settings.TwitchAccessToken = "";
        settings.TwitchRefreshToken = "";
        settings.TwitchUserId = "";
        settings.TwitchLogin = "";
        _services.Settings.Save();
        TwitchCodePanel.Visibility = Visibility.Collapsed;
        RefreshTwitchSummary();
        RestartTwitchSync();
    }
}
