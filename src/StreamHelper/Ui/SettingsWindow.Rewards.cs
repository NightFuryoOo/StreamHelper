using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private readonly ObservableCollection<RewardRow> _rewardRows = new();
    private readonly Dictionary<string, RewardInfo> _managedInfos = new();
    private List<RewardInfo> _allRewards = new();
    private HashSet<string> _managedIdSet = new();
    private bool _managedLoaded;
    private bool _rewardSectionBusy;

    private void InitRewards(AppSettings settings)
    {
        OnlyOwnRewardsCheck.IsChecked = settings.OnlyOwnRewards;
        RewardsShowList.ItemsSource = _rewardRows;
    }

    private async Task LoadRewardSectionAsync(bool force = false)
    {
        if (force)
        {
            _managedLoaded = false;
            _soundRewardsLoaded = false;
        }
        if (_rewardSectionBusy) return;
        _rewardSectionBusy = true;
        try
        {
            if (!_managedLoaded) await LoadManagedRewardsAsync();
            if (!_soundRewardsLoaded) await LoadSoundRewardsAsync();
        }
        finally
        {
            _rewardSectionBusy = false;
        }
    }

    private async Task RunRewardActionAsync(string logLabel, Button? busy, Func<Task> action)
    {
        if (busy != null) busy.IsEnabled = false;
        try
        {
            await action();
        }
        catch (AuthRequiredException ex)
        {
            RewardsSyncStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Write(logLabel + " failed: " + ex.Message);
            RewardsSyncStatus.Text = "Не получилось: " + ex.Message;
        }
        finally
        {
            if (busy != null) busy.IsEnabled = true;
        }
    }

    private async void OnSyncRewards(object sender, RoutedEventArgs e)
    {
        var settings = _services.Settings.Current;
        if (!settings.HasTwitchTokens)
        {
            RewardsSyncStatus.Text = ConnectTwitchFirst;
            return;
        }
        if (!settings.HasManageScope)
        {
            RewardsSyncStatus.Text = ReconnectTwitchFor("управление наградами");
            return;
        }

        await RunRewardActionAsync("Reward sync", RewardsSyncButton, async () =>
        {
            RewardsSyncStatus.Text = "Синхронизирую…";
            var result = await RewardSync.RunAsync(_services.Twitch, settings.RewardCopies, CancellationToken.None);
            foreach (var copy in result.Copies) settings.RewardCopies[copy.OriginalId] = copy.CopyId;
            settings.ManagedRewardIds = result.ManagedIds.ToList();
            _services.Settings.Save();
            RewardsSyncStatus.Text = SummarizeSync(result);
            _ = LoadRewardSectionAsync(force: true);
        });
    }

    private async void OnSaveRewardIcons(object sender, RoutedEventArgs e)
    {
        var settings = _services.Settings.Current;
        if (!settings.HasTwitchTokens)
        {
            RewardsSyncStatus.Text = ConnectTwitchFirst;
            return;
        }
        if (!settings.HasRedemptionScope)
        {
            RewardsSyncStatus.Text = ReconnectTwitchFor("награды за баллы");
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Куда сохранить иконки наград за баллы",
            InitialDirectory = System.IO.Directory.Exists(settings.RewardIconsFolder)
                ? settings.RewardIconsFolder
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dialog.ShowDialog(this) != true) return;
        var folder = dialog.FolderName;
        settings.RewardIconsFolder = folder;
        _services.Settings.Save();

        await RunRewardActionAsync("Saving reward icons", RewardIconsButton, async () =>
        {
            RewardsSyncStatus.Text = "Скачиваю иконки…";
            var result = await RewardIcons.SaveAsync(_services.Twitch, settings.HasManageScope, folder, CancellationToken.None);

            var lines = new List<string>();
            if (result.Saved.Count > 0)
            {
                lines.Add($"Сохранено наград: {result.Saved.Count}, у каждой своя папка с тремя размерами (28×28, 56×56, 112×112). Папка: {folder}");
            }
            if (result.Scaled > 0) lines.Add($"У {result.Scaled} из них Twitch отдал не все размеры, недостающие получены масштабированием.");
            if (result.WithoutCustomImage > 0)
            {
                var n = result.WithoutCustomImage;
                var word = n % 10 == 1 && n % 100 != 11 ? "награды" : "наград";
                lines.Add($"У {n} {word} стандартная иконка, сохранять нечего.");
            }
            lines.AddRange(result.Failures);
            RewardsSyncStatus.Text = lines.Count == 0 ? "Нет наград с собственной иконкой." : string.Join("\n", lines);
            if (result.Saved.Count > 0) OpenInBrowser(folder);
        });
    }

    private async Task LoadManagedRewardsAsync()
    {
        var settings = _services.Settings.Current;
        if (!settings.HasTwitchTokens || !settings.HasRedemptionScope)
        {
            _managedInfos.Clear();
            _allRewards.Clear();
            RefreshRewardRows(settings.HasTwitchTokens ? ReconnectTwitchFor("награды за баллы") : ConnectTwitchFirst);
            return;
        }

        _managedLoaded = true;
        if (_rewardRows.Count == 0) ShowLine(RewardsShowStatus, "Загружаю награды…");
        try
        {
            var all = await _services.Twitch.GetRewardsAsync(CancellationToken.None);
            var managedIds = settings.HasManageScope
                ? new HashSet<string>(await _services.Twitch.GetManageableRewardIdsAsync(CancellationToken.None))
                : new HashSet<string>(settings.ManagedRewardIds);
            if (settings.HasManageScope) settings.ManagedRewardIds = managedIds.ToList();
            _allRewards = all.ToList();
            _managedIdSet = managedIds;
            _managedInfos.Clear();
            if (settings.HasManageScope)
            {
                foreach (var reward in all.Where(r => managedIds.Contains(r.Id))) _managedInfos[reward.Id] = reward;
            }
            RefreshRewardRows();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _managedLoaded = false;
            Log.Write("Loading managed rewards failed: " + ex.Message);
            if (_rewardRows.Count == 0) ShowLine(RewardsShowStatus, "Не получилось загрузить награды: " + ex.Message);
        }
    }

    private int PendingOf(string rewardId) =>
        _services.Redemptions.Items.Count(r => r.RewardId == rewardId && r.Status == RedemptionStatus.Pending);

    private void RefreshRewardRows(string? problem = null)
    {
        var settings = _services.Settings.Current;
        _rewardRows.Clear();
        if (problem == null)
        {
            foreach (var row in RewardShowList.Build(_allRewards, _managedIdSet, settings, settings.HasManageScope, PendingOf)) _rewardRows.Add(row);
        }
        RewardsShowPanel.Visibility = _rewardRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RewardsTwitchNote.Visibility = _rewardRows.Any(r => r.TwitchMade) ? Visibility.Visible : Visibility.Collapsed;
        DeleteAllButton.Visibility = _managedInfos.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowLine(RewardsShowStatus, problem ?? (_rewardRows.Count > 0
            ? ""
            : settings.OnlyOwnRewards
                ? "Наград, созданных программой, пока нет: нажми «Синхронизировать награды»."
                : "На канале нет своих наград."));
    }

    private void OnRewardShownClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RewardRow row) return;
        RewardShowList.Apply(_services.Settings.Current, row, row.Shown);
        _services.Settings.Save();
    }

    private async void OnToggleRewardEnabledClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RewardRow reward || reward.Busy || !reward.CanManage) return;
        var enable = !reward.Enabled;
        reward.Busy = true;
        try
        {
            var result = await _services.Twitch.SetRewardEnabledAsync(reward.Id, enable, CancellationToken.None);
            if (!result.Success)
            {
                RewardsSyncStatus.Text = $"«{reward.Name}»: {result.Message}";
                return;
            }
            reward.Enabled = enable;
            var index = _allRewards.FindIndex(r => r.Id == reward.Id);
            if (index >= 0) _allRewards[index] = _allRewards[index] with { IsEnabled = enable };
            if (_managedInfos.TryGetValue(reward.Id, out var info)) _managedInfos[reward.Id] = info with { IsEnabled = enable };
        }
        catch (AuthRequiredException ex)
        {
            RewardsSyncStatus.Text = ex.Message;
        }
        catch (Exception ex)
        {
            Log.Write("Switching a reward failed: " + ex.Message);
            RewardsSyncStatus.Text = $"«{reward.Name}»: не получилось: {ex.Message}";
        }
        finally
        {
            reward.Busy = false;
        }
    }

    private void OnDeleteManagedClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RewardRow { CanManage: true } reward) reward.Confirming = true;
    }

    private void OnCancelDeleteManaged(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RewardRow reward) reward.Confirming = false;
    }

    private async void OnConfirmDeleteManaged(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is RewardRow reward) await DeleteManagedAsync(new[] { reward.Id });
    }

    private void OnDeleteAllCopies(object sender, RoutedEventArgs e)
    {
        if (_managedInfos.Count == 0) return;
        var pending = _managedInfos.Keys.Sum(PendingOf);
        DeleteAllText.Text = $"Удалить все награды, созданные программой ({_managedInfos.Count})?" +
                             (pending > 0 ? $" Необработанные заказы по ним ({pending}) Twitch засчитает как выполненные, баллы не вернутся." : "");
        DeleteAllPanel.Visibility = Visibility.Visible;
    }

    private void OnCancelDeleteAll(object sender, RoutedEventArgs e) => DeleteAllPanel.Visibility = Visibility.Collapsed;

    private async void OnConfirmDeleteAll(object sender, RoutedEventArgs e)
    {
        DeleteAllPanel.Visibility = Visibility.Collapsed;
        await DeleteManagedAsync(_managedInfos.Keys.ToList());
    }

    private Task DeleteManagedAsync(IReadOnlyList<string> ids) =>
        RunRewardActionAsync("Deleting rewards", null, async () =>
        {
            var settings = _services.Settings.Current;
            RewardsSyncStatus.Text = "Удаляю…";
            var rewards = ids.Where(_managedInfos.ContainsKey).Select(id => _managedInfos[id]).ToList();
            var result = await RewardCleanup.DeleteAsync(_services.Twitch, rewards, CancellationToken.None);
            RewardCleanup.ApplyLocally(settings, _services.Redemptions, result.DeletedIds.ToList());
            _services.RewardAlert.Forget(result.DeletedIds);
            _services.Settings.Save();

            var lines = new List<string>();
            if (result.DeletedIds.Count > 0) lines.Add($"Удалено наград: {result.DeletedIds.Count}.");
            lines.AddRange(result.Failures);
            RewardsSyncStatus.Text = lines.Count == 0 ? "Нечего удалять." : string.Join("\n", lines);

            await LoadRewardSectionAsync(force: true);
        });

    private static string SummarizeSync(RewardSyncResult result)
    {
        var lines = new List<string>();
        if (result.Created > 0)
        {
            lines.Add(result.CreatedVisible > 0
                ? $"Создано копий: {result.Created} (с «(копия)» в названии: {result.CreatedVisible})."
                : $"Создано копий: {result.Created}.");
        }
        if (result.AlreadyCopied > 0) lines.Add($"Уже были: {result.AlreadyCopied}.");
        if (result.SkippedByLimit > 0) lines.Add($"Не поместилось в лимит 50 наград на канале: {result.SkippedByLimit}.");
        lines.AddRange(result.Failures);
        return lines.Count == 0 ? "Нет наград для копирования." : string.Join("\n", lines);
    }

    private void OnOnlyOwnRewardsChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var settings = _services.Settings.Current;
        settings.OnlyOwnRewards = OnlyOwnRewardsCheck.IsChecked == true;
        _services.Settings.Save();
        if (_allRewards.Count > 0) RefreshRewardRows();
        if (settings.OnlyOwnRewards && settings.HasTwitchTokens && settings.ManagedRewardIds.Count == 0)
        {
            RewardsSyncStatus.Text = settings.HasManageScope
                ? "Созданных программой наград пока нет: нажми «Синхронизировать награды»."
                : ReconnectTwitchFor("управление наградами");
        }
    }
}
