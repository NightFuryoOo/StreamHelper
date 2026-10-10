using System.Linq;
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
        _tabChecks = MainTabs.Titles.Select((title, index) =>
        {
            var check = new CheckBox
            {
                Content = title,
                Tag = index,
                Margin = new Thickness(0, 4, 18, 0),
                IsChecked = !settings.IsTabHidden(index),
            };
            AutomationProperties.SetName(check, $"Показывать вкладку «{title}»");
            check.Checked += OnTabCheckChanged;
            check.Unchecked += OnTabCheckChanged;
            TabChecks.Children.Add(check);
            return check;
        }).ToArray();
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
            check.ToolTip = last && shown ? "Хотя бы одна вкладка должна остаться" : null;
        }
    }
}
