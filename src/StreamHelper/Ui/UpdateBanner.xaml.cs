using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace StreamHelper.Ui;

public partial class UpdateBanner : UserControl
{
    public UpdateBanner()
    {
        InitializeComponent();
        SetBinding(VisibilityProperty, new Binding(nameof(UpdateService.ShowBanner))
        {
            Converter = new BooleanToVisibilityConverter(),
            FallbackValue = Visibility.Collapsed,
        });
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (DataContext is UpdateService updates) await updates.UpdateAsync();
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        if (DataContext is UpdateService updates) updates.Later();
    }
}
