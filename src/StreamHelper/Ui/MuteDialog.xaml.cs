using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StreamHelper.Ui;

public partial class MuteDialog : Window
{
    private MuteDialog(string nickname, Brush nickBrush, int defaultMinutes)
    {
        InitializeComponent();
        NickText.Text = nickname;
        NickText.Foreground = nickBrush;
        TimeBox.Text = defaultMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Loaded += (_, _) =>
        {
            Activate();
            NativeMethods.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            TimeBox.Focus();
            TimeBox.SelectAll();
        };
    }

    public int? Seconds { get; private set; }

    public static int? Ask(Window? owner, string nickname, Brush nickBrush, int defaultMinutes)
    {
        var dialog = new MuteDialog(nickname, nickBrush, defaultMinutes);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Seconds;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x231D1B, 0xEEE8E6, 0x4B3F3A);
    }

    private DurationUnit SelectedUnit =>
        UnitSeconds.IsChecked == true ? DurationUnit.Seconds
        : UnitHours.IsChecked == true ? DurationUnit.Hours
        : UnitDays.IsChecked == true ? DurationUnit.Days
        : DurationUnit.Minutes;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            OnCancel(this, new RoutedEventArgs());
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnMute(this, new RoutedEventArgs());
        }
    }

    private void OnTimeInput(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnTimePasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(typeof(string)) is not string text || !text.All(char.IsAsciiDigit)) e.CancelCommand();
    }

    private void OnTimeChanged(object sender, TextChangedEventArgs e) => ErrorText.Visibility = Visibility.Collapsed;

    private void OnMute(object sender, RoutedEventArgs e)
    {
        if (!MuteDuration.TryCompute(TimeBox.Text, SelectedUnit, out var seconds, out var error))
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Seconds = seconds;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}
