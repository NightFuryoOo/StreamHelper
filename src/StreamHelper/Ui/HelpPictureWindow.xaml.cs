using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;

namespace StreamHelper.Ui;

public partial class HelpPictureWindow : Window
{
    public HelpPictureWindow(Window owner, HelpPicture picture)
    {
        InitializeComponent();
        Owner = owner;
        Title = picture.Name;
        PictureImage.Source = picture.Source;
        AutomationProperties.SetName(PictureImage, picture.Name);
        var area = SystemParameters.WorkArea;
        PictureImage.MaxWidth = Math.Max(200, area.Width - 100);
        PictureImage.MaxHeight = Math.Max(200, area.Height - 120);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x231D1B, 0xEEE8E6, 0x4B3F3A);
    }

    private void OnPictureClick(object sender, MouseButtonEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
