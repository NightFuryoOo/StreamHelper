using System;
using System.Windows;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private readonly System.Windows.Threading.DispatcherTimer _toastSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool _syncingToast;
    private bool _syncingBadge;

    private void InitToasts(AppSettings settings)
    {
        SetPercent(ToastOpacitySlider, ToastOpacityText, ToastPlacement.ClampOpacity(settings.ToastOpacity));
        SetPercent(ToastScaleSlider, ToastScaleText, ToastPlacement.ClampScale(settings.ToastScale));
        ToastSecondsSlider.Value = ToastPlacement.ClampSeconds(settings.ToastSeconds);
        ToastSecondsText.Text = $"{ToastSecondsSlider.Value:0} с";
        _toastSaveTimer.Tick += (_, _) =>
        {
            _toastSaveTimer.Stop();
            _services.Settings.Save();
        };
        Hook(() => _services.Toasts.ScaleChanged += OnToastScaleDragged, () => _services.Toasts.ScaleChanged -= OnToastScaleDragged);
    }

    private void SaveSoon()
    {
        _toastSaveTimer.Stop();
        _toastSaveTimer.Start();
    }

    private void OnToastScaleDragged(double scale)
    {
        _syncingToast = true;
        try
        {
            SetPercent(ToastScaleSlider, ToastScaleText, scale);
        }
        finally
        {
            _syncingToast = false;
        }
    }

    private void OnToastSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _syncingToast) return;
        var settings = _services.Settings.Current;
        settings.ToastOpacity = ToastPlacement.ClampOpacity(ToastOpacitySlider.Value / 100);
        settings.ToastScale = ToastPlacement.ClampScale(ToastScaleSlider.Value / 100);
        settings.ToastSeconds = ToastPlacement.ClampSeconds(ToastSecondsSlider.Value);
        ToastOpacityText.Text = Percent(ToastOpacitySlider);
        ToastScaleText.Text = Percent(ToastScaleSlider);
        ToastSecondsText.Text = $"{ToastSecondsSlider.Value:0} с";
        SaveSoon();
        _services.Toasts.ApplyAppearance();
    }

    private void OnToastSample(object sender, RoutedEventArgs e) => _services.Toasts.ShowSample();

    private void OnToastPosition(object sender, RoutedEventArgs e) => TogglePositioning(_services.Toasts, ToastPositionButton);

    private void OnToastResetPosition(object sender, RoutedEventArgs e) => _services.Toasts.ResetPosition();

    private void InitMuteBadge(AppSettings settings)
    {
        RewardSoundsMutedCheck.IsChecked = settings.RewardSoundsMuted;
        MuteBadgeHiddenCheck.IsChecked = settings.MuteBadgeHidden;
        MuteBadgeHiddenWarning.Visibility = settings.MuteBadgeHidden ? Visibility.Visible : Visibility.Collapsed;
        SetPercent(MuteBadgeScaleSlider, MuteBadgeScaleText, MuteBadgePlacement.ClampScale(settings.MuteBadgeScale));
        Hook(() => _services.RewardAlert.MutedChanged += OnRewardSoundsMutedChanged, () => _services.RewardAlert.MutedChanged -= OnRewardSoundsMutedChanged);
        Hook(() => _services.MuteBadge.ScaleChanged += OnMuteBadgeScaleDragged, () => _services.MuteBadge.ScaleChanged -= OnMuteBadgeScaleDragged);
    }

    private void OnRewardSoundsMutedClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _services.RewardAlert.SetMuted(RewardSoundsMutedCheck.IsChecked == true);
    }

    private void OnRewardSoundsMutedChanged(bool muted) =>
        Dispatcher.InvokeAsync(() => RewardSoundsMutedCheck.IsChecked = muted);

    private void OnMuteBadgeHiddenClick(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var hidden = MuteBadgeHiddenCheck.IsChecked == true;
        _services.Settings.Current.MuteBadgeHidden = hidden;
        _services.Settings.Save();
        MuteBadgeHiddenWarning.Visibility = hidden ? Visibility.Visible : Visibility.Collapsed;
        _services.MuteBadge.Refresh();
    }

    private void OnMuteBadgeScaleChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_loading || _syncingBadge) return;
        _services.Settings.Current.MuteBadgeScale = MuteBadgePlacement.ClampScale(MuteBadgeScaleSlider.Value / 100);
        MuteBadgeScaleText.Text = Percent(MuteBadgeScaleSlider);
        SaveSoon();
        _services.MuteBadge.ApplyAppearance();
    }

    private void OnMuteBadgeScaleDragged(double scale)
    {
        _syncingBadge = true;
        try
        {
            SetPercent(MuteBadgeScaleSlider, MuteBadgeScaleText, scale);
        }
        finally
        {
            _syncingBadge = false;
        }
    }

    private void OnMuteBadgePosition(object sender, RoutedEventArgs e) => TogglePositioning(_services.MuteBadge, MuteBadgePositionButton);

    private void OnMuteBadgeResetPosition(object sender, RoutedEventArgs e) => _services.MuteBadge.ResetPosition();
}
