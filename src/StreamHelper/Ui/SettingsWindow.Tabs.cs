using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private CheckBox[] _tabChecks = System.Array.Empty<CheckBox>();

    private void InitTabChecks(AppSettings settings)
    {
        _tabChecks = new[] { TabEyeDonations, TabEyeFollowers, TabEyeSubscribers, TabEyeRewards, TabEyePings, TabEyeMoments };
        for (var index = 0; index < _tabChecks.Length; index++)
        {
            var check = _tabChecks[index];
            check.Tag = index;
            check.IsChecked = !settings.IsTabHidden(index);
            AutomationProperties.SetName(check, $"Показывать вкладку «{MainTabs.Titles[index]}»");
            ToolTipService.SetShowOnDisabled(check, true);
            check.Checked += OnTabCheckChanged;
            check.Unchecked += OnTabCheckChanged;
        }
        RefreshTabChecks();
    }

    private void OnTabCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not CheckBox { Tag: int index } check) return;
        var settings = _services.Settings.Current;
        settings.SetTabHidden(index, check.IsChecked != true);
        _services.Settings.Save();
        _services.ViewModel.ApplyHiddenTabs(settings);
        RefreshTabChecks();
    }

    private void RefreshTabChecks()
    {
        var settings = _services.Settings.Current;
        var last = settings.VisibleTabCount() <= 1;
        foreach (var check in _tabChecks)
        {
            var index = (int)check.Tag;
            var shown = !settings.IsTabHidden(index);
            if (check.IsChecked != shown) check.IsChecked = shown;
            check.IsEnabled = !(last && shown);
            check.ToolTip = TabEyeTip(shown, last && shown);
        }
    }

    public static string TabEyeTip(bool shown, bool lastShown) =>
        lastShown ? "Хотя бы одна вкладка должна остаться"
        : shown ? "Вкладка видна в главном окне. Снятая пропадает оттуда, её клавиша не работает, а уведомления идут как раньше."
        : "Вкладка скрыта из главного окна, её клавиша не работает. Уведомления идут как раньше.";
}
