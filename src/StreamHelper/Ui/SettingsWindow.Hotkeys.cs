using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private Key? _capturedKey;

    private TextBox[] HotkeyBoxes =>
        new[] { HotkeyDonationsBox, HotkeyFollowersBox, HotkeySubscribersBox, HotkeyRewardsBox, HotkeyPingsBox, HotkeyMomentsBox, HotkeySettingsBox, HotkeyHideToastsBox, HotkeyChatBox, HotkeyChatToggleBox, HotkeyChatWriteBox, HotkeyRewardSoundsBox };

    private void InitHotkeys(AppSettings settings)
    {
        foreach (var box in HotkeyBoxes)
        {
            box.TextChanged += (_, _) => UpdateResetCross(box);
            box.Text = HotkeyService.Describe(settings.GetHotkey(ActionOf(box)));
            UpdateResetCross(box);
            box.GotKeyboardFocus += (_, _) => _services.Hotkey.UnregisterAll();
            box.LostKeyboardFocus += (_, _) => ApplyHotkeys();
        }
    }

    private static void UpdateResetCross(TextBox box)
    {
        if (box.Parent is not Grid cell) return;
        var set = box.Text != HotkeyService.Describe(0, 0);
        foreach (var button in cell.Children.OfType<Button>()) button.Visibility = set ? Visibility.Visible : Visibility.Collapsed;
    }

    private static HotkeyAction ActionOf(FrameworkElement element) => Enum.Parse<HotkeyAction>((string)element.Tag);

    private TextBox BoxFor(HotkeyAction action) => HotkeyBoxes.First(b => ActionOf(b) == action);

    private void OnHotkeyKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        CaptureHotkey(box, e, ActionOf(box).ToString(), HotkeyHint);
    }

    private bool CaptureHotkey(TextBox box, KeyEventArgs e, string hotkeyKey, TextBlock hint)
    {
        e.Handled = true;
        if (e.IsRepeat) return false;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.None or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed)
        {
            return false;
        }

        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (virtualKey == 0) return false;

        var modifiers = (uint)Keyboard.Modifiers;
        var settings = _services.Settings.Current;
        if (settings.FindHotkeyOwner(modifiers, virtualKey, hotkeyKey) is { } owner)
        {
            ShowLine(hint, $"Это сочетание уже назначено: {HotkeyTitle(owner)}.");
            return false;
        }

        if (!_services.Hotkey.IsFree(modifiers, virtualKey))
        {
            ShowLine(hint, "Эта клавиша занята другой программой, выбери другую.");
            return false;
        }

        settings.SetHotkey(hotkeyKey, modifiers, virtualKey);
        _services.Settings.Save();
        box.Text = HotkeyService.Describe(modifiers, virtualKey);
        _capturedKey = key;

        ShowLine(hint, modifiers == 0 && key is not (>= Key.F1 and <= Key.F24)
            ? "Эта клавиша будет перехвачена во всех программах: пока приложение запущено, она не будет работать как обычно."
            : "");
        return true;
    }

    private string HotkeyTitle(string hotkeyKey)
    {
        if (Enum.TryParse<HotkeyAction>(hotkeyKey, out var action)) return "«" + HotkeyActions.Title(action) + "»";
        if (!hotkeyKey.StartsWith(AppSettings.RewardHotkeyPrefix, StringComparison.Ordinal)) return hotkeyKey;
        var id = hotkeyKey[AppSettings.RewardHotkeyPrefix.Length..];
        var reward = _soundCandidates.FirstOrDefault(r => r.Id == id) ?? _soundCandidates.FirstOrDefault(r => _services.RewardAlert.KeyOf(r.Id) == id);
        var name = reward != null ? Sync.RewardRename.Clean(reward.Title) : SoundLabel(_services.RewardAlert.SoundOf(id));
        return "звук награды «" + name + "»";
    }

    private void OnHotkeyKeyUp(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (_capturedKey != key) return;
        _capturedKey = null;
        e.Handled = true;
        Keyboard.ClearFocus();
    }

    private void OnResetHotkey(object sender, RoutedEventArgs e)
    {
        Keyboard.ClearFocus();
        var action = ActionOf((FrameworkElement)sender);
        _services.Settings.Current.SetHotkey(action, 0, 0);
        _services.Settings.Save();
        BoxFor(action).Text = HotkeyService.Describe(0, 0);
        HotkeyHint.Visibility = Visibility.Collapsed;
        ApplyHotkeys();
    }

    private void ApplyHotkeys()
    {
        var failed = _services.Hotkey.RegisterAll(_services.Settings.Current);
        if (failed.Count > 0)
        {
            ShowHotkeyHint("Заняты другой программой, сейчас не работают: " + string.Join(", ", failed.Select(HotkeyTitle)) + ". Выбери другие.");
        }
    }

    private void ShowHotkeyHint(string text) => ShowLine(HotkeyHint, text);
}
