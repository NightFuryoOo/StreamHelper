using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public partial class ToastWindow : Window
{
    private const double BaseWidth = 340;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(6) };
    private readonly AppSettings _settings;
    private readonly bool _positioning;
    private double _targetOpacity = 1;
    private bool _closing;
    private bool _closed;

    public bool Stacked { get; init; }

    private string? _edge;
    private Point _resizeStartCursor;
    private Rect _resizeStartRect;

    public ToastWindow(AppSettings settings, string title, string line, string? message, bool positioning = false)
    {
        InitializeComponent();
        _settings = settings;
        _positioning = positioning;
        TitleText.Text = title;
        LineText.Text = line;
        if (string.IsNullOrWhiteSpace(message)) MessageText.Visibility = Visibility.Collapsed;
        else MessageText.Text = message;
        _timer.Tick += (_, _) => Dismiss();
        ApplyAppearance();

        if (positioning)
        {
            Root.Cursor = Cursors.SizeAll;
            Root.BorderThickness = new Thickness(2);
            Root.MouseLeftButtonDown += OnDragStart;
            ResizeLayer.Visibility = Visibility.Visible;
            foreach (var child in ResizeLayer.Children)
            {
                if (child is not Rectangle handle) continue;
                handle.MouseLeftButtonDown += OnEdgeDown;
                handle.MouseMove += OnEdgeMove;
                handle.MouseLeftButtonUp += OnEdgeUp;
            }
        }
    }

    public event Action<double, double>? Moved;

    public event Action<double>? ScaleChanging;

    public event Action<double, double, double>? ResizeFinished;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow;
        if (!_positioning) style |= NativeMethods.WsExTransparent;
        NativeMethods.AddExStyle(handle, style);
    }

    public void ApplyAppearance()
    {
        ApplyScale(ToastPlacement.ClampScale(_settings.ToastScale));
        _targetOpacity = ToastPlacement.ClampOpacity(_settings.ToastOpacity);
        if (IsVisible && !_closing)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = _targetOpacity;
            UpdateLayout();
            if (!Stacked) MoveToConfiguredPlace();
        }
    }

    private void ApplyScale(double scale)
    {
        Root.LayoutTransform = new ScaleTransform(scale, scale);
        Width = BaseWidth * scale;
    }

    public void MoveToConfiguredPlace()
    {
        var work = SystemParameters.WorkArea;
        var (left, top) = ToastPlacement.Resolve(
            _settings.ToastLeft, _settings.ToastTop, Width, IsVisible ? ActualHeight : Height,
            new ScreenBounds(work.Left, work.Top, work.Right, work.Bottom),
            new ScreenBounds(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight));
        Left = left;
        Top = top;
    }

    public double StackHeight => ActualHeight > 0 ? ActualHeight : 90 * ToastPlacement.ClampScale(_settings.ToastScale);

    public void MoveTo(double left, double top)
    {
        Left = left;
        if (!IsVisible || _closing)
        {
            BeginAnimation(TopProperty, null);
            Top = top;
            return;
        }
        var from = Top;
        if (Math.Abs(from - top) < 0.5) return;
        Top = top;
        BeginAnimation(TopProperty, new DoubleAnimation(from, top, TimeSpan.FromMilliseconds(160)) { FillBehavior = FillBehavior.Stop });
    }

    public void Present(Action? place = null)
    {
        place ??= MoveToConfiguredPlace;
        place();
        Show();
        UpdateLayout();
        place();
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, _targetOpacity, TimeSpan.FromMilliseconds(180)));
        if (!_positioning)
        {
            _timer.Interval = TimeSpan.FromSeconds(ToastPlacement.ClampSeconds(_settings.ToastSeconds));
            _timer.Start();
        }
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _timer.Stop();
        var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(350));
        fade.Completed += (_, _) =>
        {
            if (!_closed) Close();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    public void CloseNow()
    {
        if (_closed) return;
        _closing = true;
        _timer.Stop();
        BeginAnimation(OpacityProperty, null);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        base.OnClosed(e);
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
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

    private void OnEdgeDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Rectangle { Tag: string edge } handle) return;
        _edge = edge;
        _resizeStartCursor = CursorDip();
        _resizeStartRect = new Rect(Left, Top, ActualWidth, ActualHeight);
        handle.CaptureMouse();
        e.Handled = true;
    }

    private void OnEdgeMove(object sender, MouseEventArgs e)
    {
        if (_edge == null) return;
        var cursor = CursorDip();
        var dx = cursor.X - _resizeStartCursor.X;
        var dy = cursor.Y - _resizeStartCursor.Y;

        var left = _edge.Contains("Left");
        var right = _edge.Contains("Right");
        var top = _edge.Contains("Top");
        var bottom = _edge.Contains("Bottom");

        double newWidth;
        if (left || right)
        {
            newWidth = _resizeStartRect.Width + (right ? dx : -dx);
        }
        else
        {
            var aspect = _resizeStartRect.Width / Math.Max(_resizeStartRect.Height, 1);
            newWidth = (_resizeStartRect.Height + (bottom ? dy : -dy)) * aspect;
        }

        var scale = ToastPlacement.ClampScale(newWidth / BaseWidth);
        _settings.ToastScale = scale;
        ApplyScale(scale);
        UpdateLayout();

        var newLeft = left ? _resizeStartRect.Right - Width : _resizeStartRect.Left;
        var newTop = top ? _resizeStartRect.Bottom - ActualHeight : _resizeStartRect.Top;

        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;
        Left = Math.Max(screenLeft, Math.Min(newLeft, screenRight - Width));
        Top = Math.Max(screenTop, Math.Min(newTop, screenBottom - ActualHeight));
        ScaleChanging?.Invoke(scale);
    }

    private void OnEdgeUp(object sender, MouseButtonEventArgs e)
    {
        if (_edge == null) return;
        _edge = null;
        (sender as Rectangle)?.ReleaseMouseCapture();
        ResizeFinished?.Invoke(_settings.ToastScale, Left, Top);
    }
}
