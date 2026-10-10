using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public partial class ChatOverlayWindow
{
    private IChatSendApi? _chatSend;
    private bool _typing;
    private bool _sending;
    private IntPtr _typingReturnTo;
    private ChatMessage? _replyTo;
    private readonly DispatcherTimer _typingWatch = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public event Action<bool>? TypingChanged;

    public bool IsTyping => _typing;

    private void InitCompose(IChatSendApi chatSend)
    {
        _chatSend = chatSend;
        Deactivated += (_, _) =>
        {
            if (_typing) EndTyping(returnFocus: false);
        };
        _typingWatch.Tick += (_, _) =>
        {
            if (_typing && NativeMethods.GetForegroundWindow() != _handle) EndTyping(returnFocus: false);
        };
        PreviewMouseLeftButtonDown += (_, e) =>
        {
            if (_typing && !IsInside(ComposeBox, e.OriginalSource as DependencyObject) && !IsInside(MentionBox, e.OriginalSource as DependencyObject)) EndTyping(returnFocus: true);
        };
        ComposeText.SelectionChanged += (_, _) => UpdateMentions();
        Closed += (_, _) => _typingWatch.Stop();
        UpdateCompose();
    }

    private void UpdateCompose()
    {
        var shown = !_positioning && (_typing || (_settings.Current.ChatTypingMode && _interactive));
        ComposeBox.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        ReplyBar.Visibility = _replyTo != null ? Visibility.Visible : Visibility.Collapsed;
        if (_replyTo != null) ReplyText.Text = "Ответ для " + NameOf(_replyTo);
        ComposeHint.Text = _replyTo != null ? "Ответить…" : "Написать в чат…";
        ComposeHint.Visibility = ComposeText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetTypingMode(bool on)
    {
        _settings.Current.ChatTypingMode = on;
        _settings.Save();
        if (!on && _typing) EndTyping(returnFocus: true);
        UpdateCompose();
        UpdatePausedStrip();
        _ = DrainAsync();
    }

    private static bool IsInside(DependencyObject container, DependencyObject? element)
    {
        for (var current = element; current != null; current = current is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                 ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, container)) return true;
        }
        return false;
    }

    public void BeginTyping()
    {
        if (_positioning || !IsVisible || _fadingOut || _handle == IntPtr.Zero) return;
        if (!_typing)
        {
            _typing = true;
            var previous = NativeMethods.GetForegroundWindow();
            _typingReturnTo = previous == _handle ? IntPtr.Zero : previous;
            _idle.Stop();
            SetScroll(0);
            ShowComposeError("");
            UpdateCompose();
            UpdatePausedStrip();
            NativeMethods.RemoveExStyle(_handle, NativeMethods.WsExNoActivate);
            TypingChanged?.Invoke(true);
            _ = DrainAsync();
        }
        Activate();
        NativeMethods.SetForegroundWindow(_handle);
        _typingWatch.Stop();
        _typingWatch.Start();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            if (!_typing) return;
            ComposeText.Focus();
            Keyboard.Focus(ComposeText);
            ComposeText.CaretIndex = ComposeText.Text.Length;
        }));
    }

    public void EndTyping(bool returnFocus)
    {
        if (!_typing) return;
        _typing = false;
        _typingWatch.Stop();
        HideMentions();
        Keyboard.ClearFocus();
        if (_handle != IntPtr.Zero) NativeMethods.AddExStyle(_handle, NativeMethods.WsExNoActivate);
        var back = _typingReturnTo;
        _typingReturnTo = IntPtr.Zero;
        UpdateCompose();
        UpdatePausedStrip();
        TypingChanged?.Invoke(false);
        if (returnFocus && back != IntPtr.Zero && NativeMethods.Exists(back)) NativeMethods.SetForegroundWindow(back);
        RestartIdle();
    }

    private void ReplyTo(ChatMessage message)
    {
        _replyTo = message;
        UpdateCompose();
        BeginTyping();
    }

    private void ForgetReply()
    {
        _replyTo = null;
        UpdateCompose();
    }

    private void OnCancelReply(object sender, RoutedEventArgs e)
    {
        ForgetReply();
        if (_typing) ComposeText.Focus();
    }

    private void OnComposeMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_typing) BeginTyping();
    }

    private void OnComposeTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        ComposeHint.Visibility = ComposeText.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateMentions();
    }

    private async void OnComposeKeyDown(object sender, KeyEventArgs e)
    {
        if (HandleMentionKey(e))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            ComposeText.Clear();
            ShowComposeError("");
            ForgetReply();
            EndTyping(returnFocus: true);
            return;
        }
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        await SendTypedAsync();
    }

    private async Task SendTypedAsync()
    {
        var text = ComposeText.Text.Trim();
        if (text.Length == 0 || _sending || _chatSend == null) return;
        _sending = true;
        ShowComposeError("");
        ChatSendResult result;
        try
        {
            result = await _chatSend.SendChatMessageAsync(text, _replyTo?.MessageId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write("Sending a chat message failed: " + ex.Message);
            result = new ChatSendResult(false, "Ошибка: " + ex.Message);
        }
        finally
        {
            _sending = false;
        }
        if (result.Success)
        {
            ComposeText.Clear();
            ForgetReply();
        }
        else ShowComposeError(result.Message);
    }

    private void ShowComposeError(string text)
    {
        ComposeError.Text = text;
        ComposeError.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}