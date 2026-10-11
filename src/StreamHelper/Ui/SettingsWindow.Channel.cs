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
    private sealed record ChannelPick(ModeratedChannel? Channel, bool AsViewer);

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
        ModeratedChoices.Children.Clear();
        ViewerChoices.Children.Clear();
        _faceSlots.Clear();
        ChannelChoices.Children.Add(ChannelChoice($"Свой канал · {settings.TwitchLogin}", new ChannelPick(null, false), settings.IsOwnChannel, withFace: false));

        var moderated = (_moderated ?? Array.Empty<ModeratedChannel>()).ToList();
        var chosenModerated = !settings.IsOwnChannel && !settings.IsViewerChannel;
        if (chosenModerated && moderated.All(c => c.Id != settings.TwitchChannelId))
        {
            moderated.Insert(0, new ModeratedChannel(settings.TwitchChannelId, settings.TwitchChannelLogin, settings.TwitchChannelName));
        }
        foreach (var channel in moderated)
        {
            ModeratedChoices.Children.Add(ChannelChoice(channel.Label, new ChannelPick(channel, false), chosenModerated && channel.Id == settings.TwitchChannelId));
        }
        ModeratedLabel.Visibility = moderated.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var viewers = settings.ViewerChannels.Where(c => moderated.All(m => m.Id != c.Id)).ToList();
        if (settings.IsViewerChannel && viewers.All(c => c.Id != settings.TwitchChannelId))
        {
            viewers.Insert(0, new SavedChannel(settings.TwitchChannelId, settings.TwitchChannelLogin, settings.TwitchChannelName));
        }
        foreach (var channel in viewers) ViewerChoices.Children.Add(ViewerChoice(channel, settings.IsViewerChannel && channel.Id == settings.TwitchChannelId));
        _buildingChannels = false;

        var (text, warn) = ChatRules.ChannelStatus(settings, _moderated, _channelsLoading, _channelError);
        ChannelStatusText.Text = text;
        ChannelStatusText.Foreground = Resource(warn ? "WarnBrush" : "MutedBrush");
        ChannelStatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ChannelRefreshButton.IsEnabled = settings.HasModeratedChannelsScope && !_channelsLoading;
        ViewerAddButton.IsEnabled = !_viewerBusy;
    }

    private RadioButton ChannelChoice(string text, ChannelPick pick, bool selected, bool withFace = true)
    {
        var id = withFace ? pick.Channel?.Id : null;
        var onAir = id != null && _live.ContainsKey(id);
        var choice = new RadioButton
        {
            Style = (Style)FindResource("ChoiceChip"),
            Content = id != null ? ChannelContent(text, id, onAir) : text,
            GroupName = "channel",
            IsChecked = selected,
            Tag = pick,
        };
        if (id != null) choice.Padding = new Thickness(4, 3, 10, 3);
        System.Windows.Automation.AutomationProperties.SetName(choice, onAir ? text + " · в эфире" : text);
        if (onAir)
        {
            choice.ToolTip = "В эфире";
            choice.ToolTipOpening += (_, _) =>
            {
                if (_live.TryGetValue(id!, out var stream)) choice.ToolTip = ChannelLive.Describe(stream, DateTime.UtcNow);
            };
        }
        choice.Checked += OnChannelChecked;
        return choice;
    }

    private FrameworkElement ViewerChoice(SavedChannel channel, bool selected)
    {
        var choice = ChannelChoice(channel.Label, new ChannelPick(new ModeratedChannel(channel.Id, channel.Login, channel.Name), true), selected);
        choice.Margin = new Thickness(0, 6, 0, 0);
        var remove = new Button
        {
            Style = (Style)FindResource("CrossButton"),
            Margin = new Thickness(2, 6, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Убрать канал",
        };
        System.Windows.Automation.AutomationProperties.SetName(remove, "Убрать канал " + channel.Label);
        remove.Click += (_, _) => RemoveViewerChannel(channel);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(choice);
        row.Children.Add(remove);
        return row;
    }

    private void OnChannelChecked(object sender, RoutedEventArgs e)
    {
        if (_buildingChannels || (sender as FrameworkElement)?.Tag is not ChannelPick pick) return;
        Pick(pick.Channel, pick.AsViewer);
    }

    private void Pick(ModeratedChannel? channel, bool asViewer)
    {
        if (_services.ChangeChannel(channel, asViewer)) RefreshTwitchSummary();
        else RefreshChannelBlock();
    }

    private async Task LoadModeratedChannelsAsync()
    {
        var settings = _services.Settings.Current;
        if (!settings.HasTwitchTokens || !settings.HasModeratedChannelsScope || _channelsLoading)
        {
            RefreshChannelBlock();
            await RefreshLiveAsync();
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
        if (_moderated?.FirstOrDefault(c => settings.IsViewerChannel && c.Id == settings.TwitchChannelId) is { } promoted) Pick(promoted, false);
        else RefreshChannelBlock();
        await RefreshLiveAsync();
    }

    private void OnRefreshChannels(object sender, RoutedEventArgs e) => _ = LoadModeratedChannelsAsync();
}
