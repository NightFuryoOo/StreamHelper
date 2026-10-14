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
    private bool _channelPickerOpen;

    public static (string Kind, string Note) ChannelRole(AppSettings settings) =>
        settings.IsOwnChannel ? ("свой канал", "Работает всё: чат, уведомления, подписки, награды, опросы и предикты.")
        : settings.IsViewerChannel ? ("как зритель", "Только чат поверх игры и сообщения в чат.")
        : ("модератор", "Чат поверх игры, бан и мут, удаление сообщений и «Отметить». Уведомления, подписки и награды — только на своём канале.");

    public static bool MatchesSearch(string label, string query) =>
        query.Trim().Length == 0 || label.Contains(query.Trim(), StringComparison.CurrentCultureIgnoreCase);

    private void RefreshChannelBlock()
    {
        var settings = _services.Settings.Current;
        ChannelBlock.Visibility = settings.HasTwitchTokens ? Visibility.Visible : Visibility.Collapsed;
        ChannelSummary.Visibility = ChannelBlock.Visibility;
        if (!settings.HasTwitchTokens) return;

        _buildingChannels = true;
        ChannelChoices.Children.Clear();
        ModeratedChoices.Children.Clear();
        ViewerChoices.Children.Clear();
        _faceSlots.Clear();
        ShowCurrentChannel(settings);
        ChannelChoices.Children.Add(ChannelChoice($"Свой канал · {settings.TwitchLogin}", new ChannelPick(null, false), settings.IsOwnChannel, withFace: false));

        var query = ChannelSearchBox.Text;
        var moderated = (_moderated ?? Array.Empty<ModeratedChannel>()).ToList();
        var chosenModerated = !settings.IsOwnChannel && !settings.IsViewerChannel;
        if (chosenModerated && moderated.All(c => c.Id != settings.TwitchChannelId))
        {
            moderated.Insert(0, new ModeratedChannel(settings.TwitchChannelId, settings.TwitchChannelLogin, settings.TwitchChannelName));
        }
        foreach (var channel in moderated.Where(c => MatchesSearch(c.Label, query)))
        {
            ModeratedChoices.Children.Add(ChannelChoice(channel.Label, new ChannelPick(channel, false), chosenModerated && channel.Id == settings.TwitchChannelId));
        }
        ModeratedLabel.Text = moderated.Count > 0 ? $"Где ты модератор · {moderated.Count}" : "Где ты модератор";
        ModeratedLabel.Visibility = moderated.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        var viewers = settings.ViewerChannels.Where(c => moderated.All(m => m.Id != c.Id)).ToList();
        if (settings.IsViewerChannel && viewers.All(c => c.Id != settings.TwitchChannelId))
        {
            viewers.Insert(0, new SavedChannel(settings.TwitchChannelId, settings.TwitchChannelLogin, settings.TwitchChannelName));
        }
        foreach (var channel in viewers.Where(c => MatchesSearch(c.Label, query)))
        {
            ViewerChoices.Children.Add(ViewerChoice(channel, settings.IsViewerChannel && channel.Id == settings.TwitchChannelId));
        }
        _buildingChannels = false;
        ChannelPicker.Visibility = _channelPickerOpen ? Visibility.Visible : Visibility.Collapsed;
        ChannelPickerChevron.Angle = _channelPickerOpen ? 180 : 0;

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
        _channelPickerOpen = false;
        if (_services.ChangeChannel(channel, asViewer)) RefreshTwitchSummary();
        else RefreshChannelBlock();
    }

    private void ShowCurrentChannel(AppSettings settings)
    {
        var (kind, note) = ChannelRole(settings);
        var name = settings.ChannelLabel;
        CurrentChannelName.Text = name;
        CurrentChannelKind.Text = kind;
        CurrentChannelNote.Text = note;
        ChannelSummaryText.Text = ChannelSummaryLine(settings);
        var id = settings.IsOwnChannel ? settings.TwitchUserId : settings.TwitchChannelId;
        var avatar = new ChannelAvatar(name, 30, 14, FaceBack);
        if (id.Length > 0 && _live.ContainsKey(id))
        {
            avatar.Root.Children.Add(new System.Windows.Shapes.Ellipse
            {
                Width = 11,
                Height = 11,
                Fill = Freezables.LiveDot,
                Stroke = DotRing,
                StrokeThickness = 2,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -2, -2, 0),
                ToolTip = "В эфире",
            });
        }
        CurrentChannelFace.Child = avatar.Root;
        if (id.Length == 0) return;
        ShowFace(id, avatar);
        _faceSlots.Add((id, avatar));
    }

    public static string ChannelSummaryLine(AppSettings settings) => $"{settings.ChannelLabel} · {ChannelRole(settings).Kind}";

    private void OnGoToChannelChoice(object sender, RoutedEventArgs e)
    {
        SelectPage(ChatPage);
        Reveal(ChannelBlock);
    }

    private void OnToggleChannelPicker(object sender, RoutedEventArgs e)
    {
        _channelPickerOpen = !_channelPickerOpen;
        ChannelPicker.Visibility = _channelPickerOpen ? Visibility.Visible : Visibility.Collapsed;
        ChannelPickerChevron.Angle = _channelPickerOpen ? 180 : 0;
        if (_channelPickerOpen) Reveal(ChannelPicker);
    }

    private void OnChannelSearchChanged(object sender, TextChangedEventArgs e)
    {
        ChannelSearchHint.Visibility = ChannelSearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_loading) RefreshChannelBlock();
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
