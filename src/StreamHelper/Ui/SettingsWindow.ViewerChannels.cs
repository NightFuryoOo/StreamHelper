using System;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private bool _viewerBusy;

    private void RemoveViewerChannel(SavedChannel channel)
    {
        var settings = _services.Settings.Current;
        settings.ForgetViewerChannel(channel.Id);
        if (settings.IsViewerChannel && settings.TwitchChannelId == channel.Id) Pick(null, false);
        else
        {
            _services.Settings.Save();
            RefreshChannelBlock();
        }
    }

    private void ShowViewerStatus(string text, bool warn)
    {
        ViewerStatusText.Text = text;
        ViewerStatusText.Foreground = Resource(warn ? "WarnBrush" : "MutedBrush");
        ViewerStatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnViewerChannelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = AddViewerChannelAsync();
    }

    private void OnAddViewerChannel(object sender, RoutedEventArgs e) => _ = AddViewerChannelAsync();

    private async Task AddViewerChannelAsync()
    {
        if (_viewerBusy) return;
        var settings = _services.Settings.Current;
        var login = ChatRules.ChannelLogin(ViewerChannelInput.Text);
        if (login == null)
        {
            ShowViewerStatus("Напиши ник канала (латинские буквы, цифры и _) или ссылку twitch.tv/ник.", true);
            return;
        }
        if (string.Equals(login, settings.TwitchLogin, StringComparison.OrdinalIgnoreCase))
        {
            Picked(null, false, "Это твой канал: выбран «Свой канал».");
            return;
        }
        var moderated = _moderated?.FirstOrDefault(c => string.Equals(c.Login, login, StringComparison.OrdinalIgnoreCase));
        if (moderated != null)
        {
            Picked(moderated, false, $"Ты модератор канала {moderated.Label}: он выбран в «Где ты модератор».");
            return;
        }

        _viewerBusy = true;
        ViewerAddButton.IsEnabled = false;
        ShowViewerStatus($"Ищу канал {login}…", false);
        TwitchUser? user = null;
        string? error = null;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            user = await _services.Twitch.GetUserByLoginAsync(login, cts.Token);
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthRequiredException or OperationCanceledException or System.Text.Json.JsonException)
        {
            Log.Write("Channel lookup failed: " + ex.Message);
            error = ex is OperationCanceledException ? "Twitch не ответил." : ex.Message;
        }
        finally
        {
            _viewerBusy = false;
            ViewerAddButton.IsEnabled = true;
        }
        if (error != null)
        {
            ShowViewerStatus("Не удалось найти канал: " + error, true);
            return;
        }
        if (user == null)
        {
            ShowViewerStatus($"Канала {login} на Twitch нет.", true);
            return;
        }
        if (user.Id == settings.TwitchUserId)
        {
            Picked(null, false, "Это твой канал: выбран «Свой канал».");
            return;
        }
        moderated = _moderated?.FirstOrDefault(c => c.Id == user.Id);
        if (moderated != null)
        {
            Picked(moderated, false, $"Ты модератор канала {moderated.Label}: он выбран в «Где ты модератор».");
            return;
        }
        settings.RememberViewerChannel(new SavedChannel(user.Id, user.Login, user.DisplayName));
        Picked(new ModeratedChannel(user.Id, user.Login, user.DisplayName), true, "");
        await RefreshLiveAsync();
    }

    private void Picked(ModeratedChannel? channel, bool asViewer, string note)
    {
        ViewerChannelInput.Clear();
        ShowViewerStatus(note, false);
        _services.Settings.Save();
        Pick(channel, asViewer);
    }
}
