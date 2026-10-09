using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace StreamHelper.Ui;

public partial class HighlightColorDialog : Window
{
    private double _h, _s = 1, _v = 1;
    private bool _syncing;
    private bool _dragSv, _dragHue;

    private HighlightColorDialog(string nickname, Brush nickBrush, string startHex)
    {
        InitializeComponent();
        NickText.Text = nickname;
        NickText.Foreground = nickBrush;
        SetColor(startHex);
        Loaded += (_, _) =>
        {
            Activate();
            NativeMethods.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            HexBox.Focus();
            HexBox.SelectAll();
        };
    }

    public string? Hex { get; private set; }

    public static string? Ask(Window? owner, string nickname, Brush nickBrush, string? currentHex)
    {
        var dialog = new HighlightColorDialog(nickname, nickBrush, currentHex ?? ChatHighlights.Palette[1].Hex);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog.Hex;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x231D1B, 0xEEE8E6, 0x4B3F3A);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            OnOk(this, new RoutedEventArgs());
        }
    }

    private void OnSvDown(object sender, MouseButtonEventArgs e)
    {
        _dragSv = true;
        SvArea.CaptureMouse();
        PickSv(e.GetPosition(SvArea));
    }

    private void OnSvMove(object sender, MouseEventArgs e)
    {
        if (_dragSv) PickSv(e.GetPosition(SvArea));
    }

    private void OnHueDown(object sender, MouseButtonEventArgs e)
    {
        _dragHue = true;
        HueArea.CaptureMouse();
        PickHue(e.GetPosition(HueArea));
    }

    private void OnHueMove(object sender, MouseEventArgs e)
    {
        if (_dragHue) PickHue(e.GetPosition(HueArea));
    }

    private void OnPickerUp(object sender, MouseButtonEventArgs e)
    {
        _dragSv = _dragHue = false;
        SvArea.ReleaseMouseCapture();
        HueArea.ReleaseMouseCapture();
    }

    private void OnPickerResized(object sender, SizeChangedEventArgs e) => PlaceMarkers();

    private void PickSv(Point p)
    {
        if (SvArea.ActualWidth <= 0 || SvArea.ActualHeight <= 0) return;
        _s = Math.Clamp(p.X / SvArea.ActualWidth, 0, 1);
        _v = 1 - Math.Clamp(p.Y / SvArea.ActualHeight, 0, 1);
        FromPicker();
    }

    private void PickHue(Point p)
    {
        if (HueArea.ActualWidth <= 0) return;
        _h = Math.Clamp(p.X / HueArea.ActualWidth, 0, 1) * 360;
        FromPicker();
    }

    private void FromPicker()
    {
        var (r, g, b) = ChatHighlights.FromHsv(_h, _s, _v);
        _syncing = true;
        try
        {
            HexBox.Text = ChatHighlights.ToHex(r, g, b);
        }
        finally
        {
            _syncing = false;
        }
        ErrorText.Visibility = Visibility.Collapsed;
        ShowColor();
    }

    private void SetColor(string hex)
    {
        var (r, g, b) = ChatHighlights.Parse(hex);
        var (h, s, v) = ChatHighlights.ToHsv(r, g, b);
        if (s > 0 && v > 0) _h = h;
        _s = s;
        _v = v;
        _syncing = true;
        try
        {
            HexBox.Text = hex;
        }
        finally
        {
            _syncing = false;
        }
        ShowColor();
    }

    private void ShowColor()
    {
        var (hr, hg, hb) = ChatHighlights.FromHsv(_h, 1, 1);
        SvHue.Fill = new SolidColorBrush(Color.FromRgb(hr, hg, hb));
        var (r, g, b) = ChatHighlights.FromHsv(_h, _s, _v);
        Preview.Background = new SolidColorBrush(Color.FromRgb(r, g, b));
        PlaceMarkers();
    }

    private void PlaceMarkers()
    {
        Canvas.SetLeft(SvMarker, _s * SvArea.ActualWidth - SvMarker.Width / 2);
        Canvas.SetTop(SvMarker, (1 - _v) * SvArea.ActualHeight - SvMarker.Height / 2);
        Canvas.SetLeft(SvMarkerRing, _s * SvArea.ActualWidth - SvMarkerRing.Width / 2);
        Canvas.SetTop(SvMarkerRing, (1 - _v) * SvArea.ActualHeight - SvMarkerRing.Height / 2);
        Canvas.SetLeft(HueMarker, _h / 360 * HueArea.ActualWidth - HueMarker.Width / 2);
    }

    private void OnHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        ErrorText.Visibility = Visibility.Collapsed;
        var hex = ChatHighlights.Normalize(HexBox.Text);
        if (hex == null)
        {
            Preview.Background = Brushes.Transparent;
            return;
        }
        var (r, g, b) = ChatHighlights.Parse(hex);
        var (h, s, v) = ChatHighlights.ToHsv(r, g, b);
        if (s > 0 && v > 0) _h = h;
        _s = s;
        _v = v;
        ShowColor();
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var hex = ChatHighlights.Normalize(HexBox.Text);
        if (hex == null)
        {
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Hex = hex;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();
}