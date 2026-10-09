using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private readonly ObservableCollection<RewardSoundChoice> _soundRewards = new();
    private readonly ObservableCollection<RewardSoundChoice> _soundAvailable = new();
    private List<RewardInfo> _soundCandidates = new();
    private RewardSoundChoice? _soundPicked;
    private bool _soundRewardsLoaded;
    private bool _syncingSound;

    private void InitRewardSounds()
    {
        RewardSoundList.ItemsSource = _soundRewards;
        RewardSoundAvailableList.ItemsSource = _soundAvailable;
    }

    private static string SoundLabel(RewardSound? sound) =>
        sound == null ? "" : string.IsNullOrWhiteSpace(sound.Name) ? sound.File : sound.Name;

    private async Task LoadSoundRewardsAsync()
    {
        var settings = _services.Settings.Current;
        if (!settings.HasTwitchTokens)
        {
            ShowLine(RewardSoundListStatus, "Сначала подключи Twitch.");
            return;
        }

        _soundRewardsLoaded = true;
        ShowLine(RewardSoundListStatus, "Загружаю список наград…");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var rewards = await _services.Twitch.GetRewardsAsync(cts.Token);
            var present = rewards.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var copyIds = settings.RewardCopies
                .Where(pair => present.Contains(pair.Key))
                .Select(pair => pair.Value)
                .ToHashSet(StringComparer.Ordinal);
            _soundCandidates = rewards
                .Where(r => !copyIds.Contains(r.Id))
                .OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            var changed = false;
            foreach (var id in settings.RewardSounds.Keys)
            {
                if (!settings.SoundRewardIds.Contains(id))
                {
                    settings.SoundRewardIds.Add(id);
                    changed = true;
                }
            }
            if (changed) _services.Settings.Save();

            RebuildSoundLists(_soundPicked?.Id);
        }
        catch (OperationCanceledException)
        {
            _soundRewardsLoaded = false;
            ShowLine(RewardSoundListStatus, "Не дождался ответа Twitch, открой вкладку ещё раз.");
        }
        catch (Exception ex)
        {
            _soundRewardsLoaded = false;
            Log.Write("Loading rewards for sounds failed: " + ex.Message);
            ShowLine(RewardSoundListStatus, ex.Message);
        }
    }

    private void RebuildSoundLists(string? pickedId)
    {
        var settings = _services.Settings.Current;
        var listed = settings.SoundRewardIds.ToHashSet(StringComparer.Ordinal);

        _soundRewards.Clear();
        _soundAvailable.Clear();
        _soundPicked = null;
        foreach (var reward in _soundCandidates)
        {
            if (!listed.Contains(reward.Id))
            {
                _soundAvailable.Add(new RewardSoundChoice(reward.Id, reward.Title, ""));
                continue;
            }
            var choice = new RewardSoundChoice(reward.Id, reward.Title, SoundLabel(_services.RewardAlert.SoundOf(reward.Id)));
            if (reward.Id == pickedId)
            {
                choice.Picked = true;
                _soundPicked = choice;
            }
            _soundRewards.Add(choice);
        }

        RewardSoundAddButton.IsEnabled = _soundAvailable.Count > 0;
        if (_soundAvailable.Count == 0) RewardSoundAddPanel.Visibility = Visibility.Collapsed;
        ShowLine(RewardSoundListStatus,
            _soundCandidates.Count == 0 ? "На канале нет пользовательских наград."
            : _soundRewards.Count == 0 ? "Добавь награды, для которых нужен звук."
            : "");
        RefreshRewardAlert();
    }

    private void OnRewardSoundAddToggle(object sender, RoutedEventArgs e) =>
        RewardSoundAddPanel.Visibility = RewardSoundAddPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    private void OnRewardSoundAddPick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RewardSoundChoice choice) return;
        var settings = _services.Settings.Current;
        if (!settings.SoundRewardIds.Contains(choice.Id)) settings.SoundRewardIds.Add(choice.Id);
        _services.Settings.Save();
        RewardSoundAddPanel.Visibility = Visibility.Collapsed;
        ShowLine(RewardAlertStatus, "");
        RebuildSoundLists(choice.Id);
    }

    private void OnRewardSoundPicked(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RewardSoundChoice choice) return;
        _soundPicked = choice;
        var sound = _services.RewardAlert.SoundOf(choice.Id);
        var missing = sound != null && _services.RewardAlert.PathOf(choice.Id) == null;
        ShowLine(RewardAlertStatus, missing ? "Файл пропал, выбери заново." : "");
        RefreshRewardAlert();
    }

    private void RefreshRewardAlert()
    {
        var id = _soundPicked?.Id;
        var sound = id == null ? null : _services.RewardAlert.SoundOf(id);
        var playable = id != null && _services.RewardAlert.PathOf(id) != null;
        if (_soundPicked != null) _soundPicked.SoundName = SoundLabel(sound);

        RewardAlertChooseButton.IsEnabled = id != null;
        RewardAlertTestButton.IsEnabled = playable;
        RewardAlertClearButton.IsEnabled = id != null;
        RewardAlertVolumeSlider.IsEnabled = sound != null;

        _syncingSound = true;
        try
        {
            SetPercent(RewardAlertVolumeSlider, RewardAlertVolumeText, RewardAlertService.ClampVolume(sound?.Volume ?? 0.7));
        }
        finally
        {
            _syncingSound = false;
        }
    }

    private void OnRewardAlertFailed(string reason) => Dispatcher.InvokeAsync(() => ShowLine(RewardAlertStatus, reason));

    private void OnRewardAlertChoose(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Звук: {_soundPicked?.Name}",
            Filter = "Звуки|" + string.Join(";", RewardAlertService.Extensions.Select(x => "*" + x)) + "|Все файлы|*.*",
        };
        if (_soundPicked == null || dialog.ShowDialog(this) != true) return;
        var result = _services.RewardAlert.Choose(_soundPicked.Id, dialog.FileName);
        ShowLine(RewardAlertStatus, result.Success ? "" : result.Error);
        RefreshRewardAlert();
    }

    private void OnRewardAlertClear(object sender, RoutedEventArgs e)
    {
        if (_soundPicked == null) return;
        var id = _soundPicked.Id;
        _services.RewardAlert.Clear(id);
        _services.Settings.Current.SoundRewardIds.Remove(id);
        _services.Settings.Save();
        ShowLine(RewardAlertStatus, "");
        RebuildSoundLists(null);
    }

    private void OnRewardAlertTest(object sender, RoutedEventArgs e)
    {
        ShowLine(RewardAlertStatus, "");
        if (_soundPicked == null || !_services.RewardAlert.Play(_soundPicked.Id)) ShowLine(RewardAlertStatus, "Звук не выбран или файл пропал.");
    }

    private void OnRewardAlertVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _syncingSound) return;
        RewardAlertVolumeText.Text = Percent(RewardAlertVolumeSlider);
        if (_soundPicked != null) _services.RewardAlert.SetVolume(_soundPicked.Id, RewardAlertVolumeSlider.Value / 100);
    }
}
