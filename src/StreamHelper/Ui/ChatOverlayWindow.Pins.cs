using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using StreamHelper.Api;
using StreamHelper.Storage;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public partial class ChatOverlayWindow
{
    private const double PinFontSize = 13;

    private PinCardModel? _pin;
    private EmoteAnimator? _pinAnimator;
    private bool _pinBusy;

    public event Action<PinnedMessage?>? PinChanged;

    private EmoteAnimator PinAnimator => _pinAnimator ??= new EmoteAnimator { Shows = image => image.IsVisible };

    private bool CanPin => !_settings.Current.IsViewerChannel;

    public PinnedMessage? ShownPin => _pin?.Pinned;

    public void ShowPin(PinnedMessage? pinned)
    {
        var now = DateTime.UtcNow;
        if (pinned == null || PinCardText.Expired(pinned, now))
        {
            HidePin();
            return;
        }
        var fresh = _pin == null || _pin.MessageId != pinned.Message.MessageId || _pin.Pinned.Message.Text != pinned.Message.Text;
        if (_pin == null)
        {
            _pin = new PinCardModel(pinned, now, _interactive && CanPin, _settings.Current.ChatVotesCollapsed);
            PinCard.DataContext = _pin;
        }
        else _pin.Update(pinned, now);
        PinCard.Visibility = Visibility.Visible;
        if (fresh) FillPinText();
        UpdateVoteClock();
    }

    private void HidePin()
    {
        if (_pin == null) return;
        _pin = null;
        PinCard.Visibility = Visibility.Collapsed;
        PinCard.DataContext = null;
        PinText.Inlines.Clear();
        PinLine.Inlines.Clear();
        _pinAnimator?.Clear();
        UpdateVoteClock();
    }

    private void TickPin(DateTime now)
    {
        if (_pin == null) return;
        if (PinCardText.Expired(_pin.Pinned, now)) HidePin();
        else _pin.Tick(now);
    }

    private void SetPinInteractive(bool on)
    {
        if (_pin != null) _pin.ShowUnpin = on && CanPin;
    }

    private ChatMessage PinSource(ChatMessage pinned) =>
        _lines.FirstOrDefault(line => line.Message.MessageId == pinned.MessageId)?.Message ?? pinned;

    private void FillPinText()
    {
        if (_pin == null) return;
        var message = PinSource(_pin.Pinned.Message);
        WritePin(message);
        if (!_emotes.IsComplete(message)) _ = FillLatePinAsync(message);
    }

    private async Task FillLatePinAsync(ChatMessage message)
    {
        await _emotes.CompleteAsync(message);
        if (_pin?.MessageId == message.MessageId) WritePin(message);
    }

    private void WritePin(ChatMessage message)
    {
        _pinAnimator?.Clear();
        Fill(PinText, pictures: true);
        Fill(PinLine, pictures: false);

        void Fill(TextBlock target, bool pictures)
        {
            target.Inlines.Clear();
            var name = NameOf(message);
            var (r, g, b) = ChatColors.Readable(message.Color, message.ChatterLogin.Length > 0 ? message.ChatterLogin : name);
            target.Inlines.Add(new Run(name) { FontWeight = FontWeights.Bold, Foreground = Frozen(new SolidColorBrush(Color.FromRgb(r, g, b))) });
            target.Inlines.Add(new Run(": "));
            if (!pictures)
            {
                target.Inlines.Add(new Run(message.Text));
                return;
            }
            foreach (var inline in _emotes.Inlines(message, PinFontSize, PinAnimator)) target.Inlines.Add(inline);
        }
    }

    private MenuItem PinItem(ContextMenu menu, ChatLine line)
    {
        if (!_settings.Current.HasDeleteMessagesScope) return Item("Закрепить (нужно переподключить Twitch)", false);
        if (_pin?.MessageId == line.Message.MessageId)
        {
            var unpin = Item("Открепить");
            unpin.Click += async (_, _) => await UnpinAsync(line.Message.MessageId, line);
            return unpin;
        }
        var pin = Item("Закрепить");
        foreach (var choice in PinCardText.Choices)
        {
            var item = Item(choice.Text);
            var seconds = choice.Seconds;
            item.Click += async (_, _) =>
            {
                menu.IsOpen = false;
                await PinAsync(line, seconds);
            };
            pin.Items.Add(item);
        }
        return pin;
    }

    private async Task PinAsync(ChatLine line, int? seconds)
    {
        if (_pinBusy) return;
        _pinBusy = true;
        ModerationResult result;
        try
        {
            result = await _moderation.PinAsync(line.Message.MessageId, seconds, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write("Pinning a chat message failed: " + ex.Message);
            result = new ModerationResult(false, "Ошибка: " + ex.Message);
        }
        finally
        {
            _pinBusy = false;
        }
        if (result.Success)
        {
            var now = DateTime.UtcNow;
            var pinned = new PinnedMessage(line.Message, _settings.Current.TwitchLogin, "", now, seconds is { } s ? now.AddSeconds(s) : null);
            ShowPin(pinned);
            PinChanged?.Invoke(pinned);
        }
        else ShowShout(line, result.Message, ErrorBrush);
        RestartIdle();
    }

    private async void OnUnpin(object sender, RoutedEventArgs e)
    {
        if (_pin is { } card) await UnpinAsync(card.MessageId, null);
    }

    private async Task UnpinAsync(string messageId, ChatLine? line)
    {
        var card = _pin?.MessageId == messageId ? _pin : null;
        if (_pinBusy || card?.Busy == true) return;
        _pinBusy = true;
        if (card != null)
        {
            card.Busy = true;
            card.Error = "";
        }
        ModerationResult result;
        try
        {
            result = await _moderation.UnpinAsync(messageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write("Unpinning a chat message failed: " + ex.Message);
            result = new ModerationResult(false, "Ошибка: " + ex.Message);
        }
        finally
        {
            _pinBusy = false;
            if (card != null) card.Busy = false;
        }
        if (result.Success)
        {
            if (_pin?.MessageId == messageId) HidePin();
            PinChanged?.Invoke(null);
        }
        else if (line != null) ShowShout(line, result.Message, ErrorBrush);
        else if (card != null) card.Error = result.Message;
        RestartIdle();
    }
}
