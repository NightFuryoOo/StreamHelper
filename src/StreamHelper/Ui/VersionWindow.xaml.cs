using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class VersionWindow : Window
{
    private readonly UpdateService _updates;
    private IReadOnlyList<UpdateRelease> _releases = Array.Empty<UpdateRelease>();
    private VersionRow? _armed;

    public VersionWindow(UpdateService updates)
    {
        InitializeComponent();
        _updates = updates;
        DataContext = updates;
        HintText.Text = VersionRow.Hint;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        ShowListMessage("Загружаю список версий…", false);
        VersionList.ItemsSource = null;
        var (releases, error) = await _updates.GetChoicesAsync();
        if (releases == null)
        {
            ShowListMessage(error, true);
            return;
        }
        _releases = releases;
        var rows = releases.Select(r => new VersionRow(r, _updates.Current)).ToList();
        VersionList.ItemsSource = rows;
        if (rows.Count == 0)
        {
            ShowListMessage("На GitHub пока нет версий, которые можно выбрать.", false);
            return;
        }
        ListMessage.Visibility = Visibility.Collapsed;
        VersionList.SelectedItem = rows.FirstOrDefault(r => r.IsCurrent) ?? rows[0];
    }

    private void ShowListMessage(string text, bool retry)
    {
        ListMessageText.Text = text;
        RetryButton.Visibility = retry ? Visibility.Visible : Visibility.Collapsed;
        ListMessage.Visibility = Visibility.Visible;
    }

    private void OnSelected(object sender, SelectionChangedEventArgs e)
    {
        _armed = null;
        WarnText.Visibility = Visibility.Collapsed;
        var row = VersionList.SelectedItem as VersionRow;
        NotesText.Text = row?.Release.Notes ?? "";
        NotesBox.Visibility = NotesText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowInstall(row);
    }

    private void ShowInstall(VersionRow? row)
    {
        InstallButton.Content = row?.InstallText(ReferenceEquals(row, _armed)) ?? "Установить";
        InstallButton.IsEnabled = row is { IsCurrent: false };
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        if (VersionList.SelectedItem is not VersionRow { IsCurrent: false } row) return;
        if (row.IsOlder && !ReferenceEquals(row, _armed))
        {
            _armed = row;
            WarnText.Text = VersionRow.OlderWarning;
            WarnText.Visibility = Visibility.Visible;
            ShowInstall(row);
            return;
        }
        _armed = null;
        WarnText.Visibility = Visibility.Collapsed;
        ShowInstall(row);
        await _updates.InstallChosenAsync(row.Release, _releases);
    }

    private async void OnRetry(object sender, RoutedEventArgs e) => await LoadAsync();

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}