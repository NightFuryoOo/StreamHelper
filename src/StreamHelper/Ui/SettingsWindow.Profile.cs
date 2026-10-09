using System;
using System.IO;
using System.Windows;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private const string ProfileFilter = "Профиль StreamHelper (*" + Profile.Extension + ")|*" + Profile.Extension;

    private string? _pendingProfile;
    private string? _profileFile;

    private bool TakeProfileNotice()
    {
        var notice = _services.ProfileNotice;
        _services.ProfileNotice = null;
        if (notice == null) return false;
        ShowProfileStatus(notice.Text, notice.BackupPath, "Показать резервную копию");
        Loaded += (_, _) => (ProfileShowFileButton.IsVisible ? ProfileShowFileButton : (FrameworkElement)ProfileStatus).BringIntoView();
        return true;
    }

    private string ProfileStartFolder()
    {
        var folder = _services.Settings.Current.ProfileFolder;
        return Directory.Exists(folder) ? folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private void RememberProfileFolder(string file)
    {
        _services.Settings.Current.ProfileFolder = Path.GetDirectoryName(file) ?? "";
        _services.Settings.Save();
    }

    private void ShowProfileStatus(string text, string? file = null, string fileButton = "Показать файл")
    {
        ShowLine(ProfileStatus, text);
        _profileFile = file != null && File.Exists(file) ? file : null;
        ProfileShowFileButton.Content = fileButton;
        ProfileShowFileButton.Visibility = _profileFile != null ? Visibility.Visible : Visibility.Collapsed;
        if (IsLoaded && text.Length > 0) Reveal(_profileFile != null ? ProfileShowFileButton : ProfileStatus);
    }

    private void OnSaveProfile(object sender, RoutedEventArgs e)
    {
        CancelProfileLoad();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить профиль StreamHelper",
            Filter = ProfileFilter,
            DefaultExt = Profile.Extension,
            AddExtension = true,
            FileName = ProfileTexts.DefaultFileName(DateTime.Now),
            InitialDirectory = ProfileStartFolder(),
        };
        if (dialog.ShowDialog(this) != true) return;
        RememberProfileFolder(dialog.FileName);

        var result = Profile.Export(
            dialog.FileName, _services.Settings.ToJson(), AppPaths.Directory, ProfileListsCheck.IsChecked == true,
            AppVersion.Current, DateTime.UtcNow);
        if (!result.Success) Log.Write("Saving the profile failed: " + result.Error);
        ShowProfileStatus(ProfileTexts.Saved(dialog.FileName, result), result.Success ? dialog.FileName : null);
    }

    private void OnLoadProfile(object sender, RoutedEventArgs e)
    {
        CancelProfileLoad();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Загрузить профиль StreamHelper",
            Filter = ProfileFilter,
            InitialDirectory = ProfileStartFolder(),
        };
        if (dialog.ShowDialog(this) != true) return;
        RememberProfileFolder(dialog.FileName);

        var result = Profile.Read(dialog.FileName);
        if (!result.Success || result.Info == null)
        {
            ShowProfileStatus(result.Error);
            return;
        }
        _pendingProfile = dialog.FileName;
        ShowProfileStatus("");
        ProfileConfirmText.Text = ProfileTexts.Confirm(result.Info, _services.Settings.Current.TwitchUserId);
        ProfileConfirmPanel.Visibility = Visibility.Visible;
        Reveal(ProfileConfirmPanel);
    }

    private void CancelProfileLoad()
    {
        _pendingProfile = null;
        ProfileConfirmPanel.Visibility = Visibility.Collapsed;
    }

    private void OnCancelLoadProfile(object sender, RoutedEventArgs e) => CancelProfileLoad();

    private void OnConfirmLoadProfile(object sender, RoutedEventArgs e)
    {
        var path = _pendingProfile;
        CancelProfileLoad();
        if (path == null) return;
        try
        {
            Profile.Stage(path, AppPaths.Directory);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowProfileStatus("Не удалось подготовить загрузку профиля: " + ex.Message);
            return;
        }
        if (_services.RestartApp()) return;
        Profile.Unstage(AppPaths.Directory);
        ShowProfileStatus("Не удалось перезапустить программу, профиль не загружен.");
    }

    private void OnShowProfileFile(object sender, RoutedEventArgs e)
    {
        if (_profileFile != null) BrowserLauncher.ShowFile(_profileFile);
    }
}
