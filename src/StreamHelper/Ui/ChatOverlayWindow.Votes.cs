using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class ChatOverlayWindow
{
    private readonly ObservableCollection<VoteCardModel> _voteCards = new();
    private readonly DispatcherTimer _voteClock = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _voteDisarm = new() { Interval = BanConfirmWindow };
    private static readonly TimeSpan ReplayFor = TimeSpan.FromSeconds(10);
    private readonly System.Collections.Generic.Dictionary<VoteKind, (ChannelVote Vote, DateTime Until)> _replays = new();
    private IVoteApi? _voteApi;
    private VoteWatcher? _voteWatcher;
    private ChannelVote? _livePoll;
    private ChannelVote? _livePrediction;

    private void InitVotes(IVoteApi votes, VoteWatcher watcher)
    {
        _voteApi = votes;
        _voteWatcher = watcher;
        VoteCards.ItemsSource = _voteCards;
        _voteClock.Tick += (_, _) => TickVotes();
        _voteDisarm.Tick += (_, _) =>
        {
            _voteDisarm.Stop();
            foreach (var card in _voteCards) card.Disarm();
        };
        Panel.MouseRightButtonUp += (_, e) =>
        {
            if (!_interactive || e.Handled) return;
            e.Handled = true;
            ShowChannelMenu();
        };
        Closed += (_, _) =>
        {
            _voteClock.Stop();
            _voteDisarm.Stop();
        };
    }

    public void ShowVotes(ChannelVote? poll, ChannelVote? prediction)
    {
        _livePoll = poll;
        _livePrediction = prediction;
        PlaceAll();
    }

    public void ReplayVote(ChannelVote vote)
    {
        _replays[vote.Kind] = (vote, DateTime.UtcNow + ReplayFor);
        PlaceAll();
    }

    private void PlaceAll()
    {
        var now = DateTime.UtcNow;
        Place(VoteKind.Poll, Shown(VoteKind.Poll, _livePoll, now), now);
        Place(VoteKind.Prediction, Shown(VoteKind.Prediction, _livePrediction, now), now);
        if (_voteCards.Count > 0) _voteClock.Start();
        else _voteClock.Stop();
    }

    private ChannelVote? Shown(VoteKind kind, ChannelVote? live, DateTime now)
    {
        if (VoteCardText.StillShown(live, now)) return live;
        if (!_replays.TryGetValue(kind, out var replay)) return null;
        if (replay.Until > now) return replay.Vote;
        _replays.Remove(kind);
        return null;
    }

    private void Place(VoteKind kind, ChannelVote? vote, DateTime now)
    {
        var card = _voteCards.FirstOrDefault(c => c.Kind == kind);
        if (vote == null)
        {
            if (card != null) _voteCards.Remove(card);
            return;
        }
        if (card != null && card.Vote.Id == vote!.Id)
        {
            card.Update(vote, now, _interactive);
            return;
        }
        if (card != null) _voteCards.Remove(card);
        var fresh = new VoteCardModel(vote!, now, _interactive);
        if (kind == VoteKind.Poll) _voteCards.Insert(0, fresh);
        else _voteCards.Add(fresh);
    }

    private void TickVotes()
    {
        PlaceAll();
        var now = DateTime.UtcNow;
        foreach (var card in _voteCards) card.Tick(now);
    }

    private void SetVotesInteractive(bool on)
    {
        var now = DateTime.UtcNow;
        foreach (var card in _voteCards) card.SetInteractive(on, now);
    }

    private static VoteCardModel? CardOf(object sender)
    {
        for (DependencyObject? node = sender as DependencyObject; node != null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is FrameworkElement { DataContext: VoteCardModel card }) return card;
        }
        return null;
    }

    private async void OnVoteEnd(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { Kind: VoteKind.Poll } card || _voteApi == null) return;
        await RunVoteAsync(card, ct => _voteApi.EndPollAsync(card.Vote.Id, ct));
    }

    private async void OnVoteLock(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { Kind: VoteKind.Prediction } card || _voteApi == null) return;
        await RunVoteAsync(card, ct => _voteApi.ChangePredictionAsync(card.Vote.Id, PredictionChange.Lock, null, ct));
    }

    private async void OnVoteCancel(object sender, RoutedEventArgs e)
    {
        if (CardOf(sender) is not { Kind: VoteKind.Prediction } card || _voteApi == null) return;
        if (!card.CancelArmed)
        {
            card.Disarm();
            card.CancelArmed = true;
            RestartDisarm();
            return;
        }
        card.Disarm();
        await RunVoteAsync(card, ct => _voteApi.ChangePredictionAsync(card.Vote.Id, PredictionChange.Cancel, null, ct));
    }

    private async void OnVoteWin(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not VoteRowModel row || CardOf(sender) is not { } card || _voteApi == null) return;
        if (!row.Armed)
        {
            card.Disarm();
            row.Armed = true;
            RestartDisarm();
            return;
        }
        card.Disarm();
        await RunVoteAsync(card, ct => _voteApi.ChangePredictionAsync(card.Vote.Id, PredictionChange.Resolve, row.Id, ct));
    }

    private void RestartDisarm()
    {
        _voteDisarm.Stop();
        _voteDisarm.Start();
    }

    private async Task RunVoteAsync(VoteCardModel card, Func<CancellationToken, Task<VoteResult>> call)
    {
        if (card.Busy) return;
        card.Busy = true;
        card.Error = "";
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await call(cts.Token);
            if (!result.Success) card.Error = result.Message;
            else if (result.Vote != null) _voteWatcher?.Show(result.Vote);
            else _voteWatcher?.PollSoon();
        }
        catch (AuthRequiredException ex)
        {
            card.Error = ex.Message;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Write("Changing a poll or prediction failed: " + ex.Message);
            card.Error = "Не получилось: " + ex.Message;
        }
        finally
        {
            card.Busy = false;
        }
    }

    private static MenuItem Item(string header, bool enabled = true)
    {
        var item = new MenuItem { Header = ChatRules.MenuText(header), IsEnabled = enabled };
        System.Windows.Automation.AutomationProperties.SetName(item, header);
        return item;
    }

    private void AddChannelItems(ContextMenu menu)
    {
        if (menu.Items.Count > 0) menu.Items.Add(new Separator());
        var settings = _settings.Current;
        var label = Item(ChatRules.ChannelHeader(settings), false);
        label.FontSize = 11;
        menu.Items.Add(label);
        if (!settings.IsOwnChannel)
        {
            menu.Items.Add(Item("Опрос… (только на своём канале)", false));
            menu.Items.Add(Item("Предикт… (только на своём канале)", false));
            return;
        }
        var poll = Item("Опрос…");
        poll.Click += (_, _) => Dispatcher.InvokeAsync(() => OpenVoteDialog(VoteKind.Poll));
        menu.Items.Add(poll);
        var prediction = Item("Предикт…");
        prediction.Click += (_, _) => Dispatcher.InvokeAsync(() => OpenVoteDialog(VoteKind.Prediction));
        menu.Items.Add(prediction);
    }

    private void ShowChannelMenu()
    {
        var menu = new ContextMenu { PlacementTarget = Panel, Placement = PlacementMode.MousePoint };
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        AddChannelItems(menu);
        menu.IsOpen = true;
    }

    private void OpenVoteDialog(VoteKind kind)
    {
        if (_voteApi == null || _voteWatcher == null) return;
        _idle.Stop();
        var previous = NativeMethods.GetForegroundWindow();
        VoteDialog dialog;
        try
        {
            dialog = VoteDialog.Ask(this, kind, _voteApi, _settings, _voteWatcher);
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != _handle && NativeMethods.Exists(previous)) NativeMethods.SetForegroundWindow(previous);
            RestartIdle();
        }
        if (dialog.Created != null) _voteWatcher.Show(dialog.Created);
        else if (dialog.Started) _voteWatcher.PollSoon();
        else if (dialog.Replay != null) ReplayVote(dialog.Replay);
    }
}
