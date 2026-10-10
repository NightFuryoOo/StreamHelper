using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private IReadOnlyList<ModeratedChannel>? _moderated;
    private string? _channelError;
    private bool _channelsLoading;
    private bool _buildingChannels;

    private void RefreshChannelBlock()
    {
        var settings = _services.Settings.Current;
        ChannelBlock.Visibility = settings.HasTwitchTokens ? Visibility.Visible : Visibility.Collapsed;
        if (!settings.HasTwitchTokens) return;

        _buildingChannels = true;
        ChannelChoices.Children.Clear();
        AddChannelChoice($"Свой канал · {settings.TwitchLogin}", null, settings.IsOwnChannel);
        var others = (_moderated ?? Array.Empty<ModeratedChannel>()).ToList();
        if (!settings.IsOwnChannel && others.All(c => c.Id != settings.TwitchChannelId))
        {
            others.Insert(0, new ModeratedChannel(settings.TwitchChannelId, settings.TwitchChannelLogin, settings.TwitchChannelName));
        }
        foreach (var channel in others) AddChannelChoice(channel.Label, channel, !settings.IsOwnChannel && channel.Id == settings.TwitchChannelId);
        _buildingChannels = false;

        var (text, warn) = ChatRules.ChannelStatus(settings, _moderated, _channelsLoading, _channelError);
        ChannelStatusText.Text = text;
        ChannelStatusText.Foreground = Resource(warn ? "WarnBrush" : "MutedBrush");
        ChannelStatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChannelRefreshButton.IsEnabled = settings.HasModeratedChannelsScope && !_channelsLoading;
    }

    private void AddChannelChoice(string text, ModeratedChannel? channel, bool selected)
    {
        var choice = new RadioButton
        {
            Style = (Style)FindResource("ChoiceChip"),
            Content = text,
            GroupName = "channel",
            IsChecked = selected,
            Tag = channel,
        };
        System.Windows.Automation.AutomationProperties.SetName(choice, text);
        choice.Checked += OnChannelChecked;
        ChannelChoices.Children.Add(choice);
    }

    private void OnChannelChecked(object sender, RoutedEventArgs e)
    {
        if (_buildingChannels) return;
        var channel = (sender as FrameworkElement)?.Tag as ModeratedChannel;
        if (_services.ChangeChannel(channel)) RefreshTwitchSummary();
        else RefreshChannelBlock();
    }

    private async Task LoadModeratedChannelsAsync()
    {
        var settings = _services.Settings.Current;
        if (!settings.HasTwitchTokens || !settings.HasModeratedChannelsScope || _channelsLoading)
        {
            RefreshChannelBlock();
            return;
        }
        _channelsLoading = true;
        _channelError = null;
        RefreshChannelBlock();
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _moderated = await _services.Twitch.GetModeratedChannelsAsync(cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthRequiredException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Log.Write("Moderated channels failed: " + ex.Message);
            _channelError = ex is OperationCanceledException ? "Twitch не ответил." : ex.Message;
        }
        finally
        {
            _channelsLoading = false;
        }
        RefreshChannelBlock();
    }

    private void OnRefreshChannels(object sender, RoutedEventArgs e) => _ = LoadModeratedChannelsAsync();
}