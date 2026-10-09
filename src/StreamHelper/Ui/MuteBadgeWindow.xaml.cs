using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public partial class MuteBadgeWindow : Window
{
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _front = new() { Interval = TimeSpan.FromSeconds(3) };
    private IntPtr _handle;
    private bool _positioning;

    private string? _corner;
    private Point _resizeStartCursor;
    private Rect _resizeStartRect;

    public MuteBadgeWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        ApplyAppearance();
        foreach (var child in ResizeLayer.Children)
        {
            if (child is not Rectangle handle) continue;
            handle.MouseLeftButtonDown += OnCornerDown;
            handle.MouseMove += OnCornerMove;
            handle.MouseLeftButtonUp += OnCornerUp;
        }
        _front.Tick += (_, _) => KeepInFront();
        _front.Start();
        Closed += (_, _) => _front.Stop();
    }

    public event Action<double, double>? Moved;

    public event Action<double>? ScaleChanging;

    public event Action<double, double, double>? ResizeFinished;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        NativeMethods.AddExStyle(_handle, NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow | NativeMethods.WsExTransparent);
        UpdateInputMode();
    }

    public void Present()
    {
        MoveToConfiguredPlace();
        if (!IsVisible) Show();
        MoveToConfiguredPlace();
        if (_handle != IntPtr.Zero) NativeMethods.KeepOnTop(_handle);
    }

    public void SetPositioning(bool on)
    {
        _positioning = on;
        Plate.BorderBrush = on ? (Brush)Application.Current.Resources["AccentBrush"] : (Brush)Application.Current.Resources["DangerBrush"];
        ResizeLayer.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Surface.Background = on ? new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)) : null;
        Cursor = on ? Cursors.SizeAll : Cursors.Arrow;
        UpdateInputMode();
    }

    public void ApplyAppearance()
    {
        var size = MuteBadgePlacement.BaseSize * MuteBadgePlacement.ClampScale(_settings.MuteBadgeScale);
        Width = size;
        Height = size;
        if (IsVisible) MoveToConfiguredPlace();
    }

    public void MoveToConfiguredPlace()
    {
        var work = SystemParameters.WorkArea;
        var (left, top) = MuteBadgePlacement.Resolve(
            _settings.MuteBadgeLeft, _settings.MuteBadgeTop, Width,
            new ScreenBounds(work.Left, work.Top, work.Right, work.Bottom),
            new ScreenBounds(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight));
        Left = left;
        Top = top;
    }

    private void KeepInFront()
    {
        if (!IsVisible || _handle == IntPtr.Zero || _positioning) return;
        NativeMethods.KeepOnTop(_handle);
    }

    private void UpdateInputMode()
    {
        if (_handle == IntPtr.Zero) return;
        if (_positioning) NativeMethods.RemoveExStyle(_handle, NativeMethods.WsExTransparent);
        else NativeMethods.AddExStyle(_handle, NativeMethods.WsExTransparent);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_positioning || e.Handled || e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            return;
        }
        Moved?.Invoke(Left, Top);
    }

    private Point CursorDip()
    {
        var (x, y) = NativeMethods.CursorPixels();
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(x / dpi.DpiScaleX, y / dpi.DpiScaleY);
    }

    private void OnCornerDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Rectangle { Tag: string corner } handle) return;
        _corner = corner;
        _resizeStartCursor = CursorDip();
        _resizeStartRect = new Rect(Left, Top, Width, Height);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void OnCornerMove(object sender, MouseEventArgs e)
    {
        if (_corner == null) return;
        var cursor = CursorDip();
        var dx = cursor.X - _resizeStartCursor.X;
        var dy = cursor.Y - _resizeStartCursor.Y;
        var left = _corner.Contains("Left");
        var top = _corner.Contains("Top");

        var grow = ((left ? -dx : dx) + (top ? -dy : dy)) / 2;
        var scale = MuteBadgePlacement.ClampScale((_resizeStartRect.Width + grow) / MuteBadgePlacement.BaseSize);
        var size = MuteBadgePlacement.BaseSize * scale;
        _settings.MuteBadgeScale = scale;
        Width = size;
        Height = size;

        var newLeft = left ? _resizeStartRect.Right - size : _resizeStartRect.Left;
        var newTop = top ? _resizeStartRect.Bottom - size : _resizeStartRect.Top;
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        Left = Math.Max(screenLeft, Math.Min(newLeft, screenLeft + SystemParameters.VirtualScreenWidth - size));
        Top = Math.Max(screenTop, Math.Min(newTop, screenTop + SystemParameters.VirtualScreenHeight - size));
        ScaleChanging?.Invoke(scale);
    }

    private void OnCornerUp(object sender, MouseButtonEventArgs e)
    {
        if (_corner == null) return;
        _corner = null;
        (sender as Rectangle)?.ReleaseMouseCapture();
        ResizeFinished?.Invoke(_settings.MuteBadgeScale, Left, Top);
    }
}