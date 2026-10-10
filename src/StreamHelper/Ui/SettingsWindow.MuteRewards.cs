using System.Linq;
using System.Windows;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private void InitMuteRewards()
    {
        MuteRewardsOffCheck.IsChecked = _services.Settings.Current.MuteSwitchesRewardsOff;
        ShowLine(MuteRewardsStatus, _services.RewardMute.Status);
        Hook(() => _services.RewardMute.StatusChanged += OnMuteRewardsStatus, () => _services.RewardMute.StatusChanged -= OnMuteRewardsStatus);
        Hook(() => _services.RewardMute.Switched += OnMuteRewardSwitched, () => _services.RewardMute.Switched -= OnMuteRewardSwitched);
    }

    private void OnMuteRewardsOffChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.Settings.Current.MuteSwitchesRewardsOff = MuteRewardsOffCheck.IsChecked == true;
        _services.Settings.Save();
        _ = _services.RewardMute.SyncAsync();
    }

    private void OnMuteRewardsStatus(string text) => Dispatcher.InvokeAsync(() => ShowLine(MuteRewardsStatus, text));

    private void OnMuteRewardSwitched(string rewardId, bool enabled) => Dispatcher.InvokeAsync(() =>
    {
        var index = _allRewards.FindIndex(r => r.Id == rewardId);
        if (index >= 0) _allRewards[index] = _allRewards[index] with { IsEnabled = enabled };
        if (_managedInfos.TryGetValue(rewardId, out var info)) _managedInfos[rewardId] = info with { IsEnabled = enabled };
        foreach (var row in _rewardRows.Where(r => r.Id == rewardId)) row.Enabled = enabled;
    });
}