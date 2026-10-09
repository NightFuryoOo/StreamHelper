using System;
using System.Windows;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private void InitChat(AppSettings settings)
    {
        SetPercent(ChatOpacitySlider, ChatOpacityText, ChatPlacement.ClampOpacity(settings.ChatOpacity));
        ChatFontSlider.Value = ChatPlacement.ClampFontSize(settings.ChatFontSize);
        SetPercent(ChatCellSlider, ChatCellText, ChatPlacement.ClampCellOpacity(settings.ChatCellOpacity));
        SetPercent(ChatOutlineSlider, ChatOutlineText, ChatPlacement.ClampOutlineOpacity(settings.ChatOutlineOpacity));
        ChatFontText.Text = $"{ChatFontSlider.Value:0} px";
        var muteMinutes = ChatPlacement.ClampMuteMinutes(settings.ChatMuteMinutes);
        ChatMuteSlider.Value = muteMinutes;
        ChatMuteText.Text = ChatPlacement.DescribeDuration(muteMinutes * 60);
    }

    private void ApplyChatStatus(SyncStatus status)
    {
        ChatStatusText.Text = status.State == SyncState.Ok ? $"Подключено · {_services.Settings.Current.TwitchLogin}" : status.Message;
        ChatStatusDot.Fill = StatusBrush(status.State);
    }

    private void OnChatSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        ChatOpacityText.Text = Percent(ChatOpacitySlider);
        ChatCellText.Text = Percent(ChatCellSlider);
        ChatOutlineText.Text = Percent(ChatOutlineSlider);
        ChatFontText.Text = $"{ChatFontSlider.Value:0} px";
        var settings = _services.Settings.Current;
        settings.ChatOpacity = ChatPlacement.ClampOpacity(ChatOpacitySlider.Value / 100);
        settings.ChatCellOpacity = ChatPlacement.ClampCellOpacity(ChatCellSlider.Value / 100);
        settings.ChatOutlineOpacity = ChatPlacement.ClampOutlineOpacity(ChatOutlineSlider.Value / 100);
        settings.ChatFontSize = ChatPlacement.ClampFontSize(ChatFontSlider.Value);
        _services.Settings.Save();
        _services.Chat.Apply();
    }

    private void OnChatMuteChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading) return;
        var minutes = ChatPlacement.ClampMuteMinutes((int)Math.Round(ChatMuteSlider.Value));
        ChatMuteText.Text = ChatPlacement.DescribeDuration(minutes * 60);
        _services.Settings.Current.ChatMuteMinutes = minutes;
        _services.Settings.Save();
        _services.Chat.Apply();
    }

    private void OnChatPosition(object sender, RoutedEventArgs e) => TogglePositioning(_services.Chat, ChatPositionButton);

    private void OnChatResetPosition(object sender, RoutedEventArgs e) => _services.Chat.ResetPosition();
}
