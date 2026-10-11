using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace StreamHelper.Ui;

public partial class ChatOverlayWindow
{
    private readonly SemaphoreSlim _emoteLoads = new(6);
    private EmotePickerWindow? _picker;

    private bool EmotesShown => _picker is { IsVisible: true };

    private IntPtr PickerHandle => _picker?.Handle ?? IntPtr.Zero;

    private async void OnEmoteButtonClick(object sender, RoutedEventArgs e)
    {
        if (EmotesShown)
        {
            HideEmotes(focusInput: true);
            return;
        }
        if (!_typing) BeginTyping();
        HideMentions();
        _picker ??= CreatePicker();
        await _picker.ShowNearAsync(this);
    }

    private EmotePickerWindow CreatePicker()
    {
        var picker = new EmotePickerWindow(_emotes, _settings, InsertEmote) { Owner = this };
        picker.Escaped += () => HideEmotes(focusInput: true);
        picker.Deactivated += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(CheckTypingFocus));
        Closed += (_, _) => picker.Close();
        return picker;
    }

    private void HideEmotes(bool focusInput)
    {
        if (!EmotesShown) return;
        _picker!.HidePicker();
        if (!focusInput || !_typing) return;
        Activate();
        NativeMethods.SetForegroundWindow(_handle);
        ComposeText.Focus();
    }

    private void CheckTypingFocus()
    {
        if (!_typing) return;
        var front = NativeMethods.GetForegroundWindow();
        if (front != _handle && (front != PickerHandle || PickerHandle == IntPtr.Zero)) EndTyping(returnFocus: false);
    }

    private void InsertEmote(string name)
    {
        var (text, caret) = EmoteChoices.InsertAt(ComposeText.Text, ComposeText.CaretIndex, name);
        if (text.Length > ComposeText.MaxLength) return;
        ComposeText.SetText(text, caret);
        _settings.Current.ChatRecentEmotes = EmoteChoices.Remember(_settings.Current.ChatRecentEmotes, name);
        _settings.Save();
        HideMentions();
    }

    private async Task LoadEmoteAsync(Image image, string url)
    {
        await _emoteLoads.WaitAsync();
        try
        {
            var frames = await _emotes.ImageAsync(url);
            if (frames != null) image.Source = frames.Frames[0];
        }
        catch (Exception ex)
        {
            Storage.Log.Write("Emote picture failed: " + ex.Message);
        }
        finally
        {
            _emoteLoads.Release();
        }
    }
}
