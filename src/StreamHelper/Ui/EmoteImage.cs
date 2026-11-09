using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StreamHelper.Ui;

public sealed class EmoteImage : Image
{
    private readonly ImageBrush _brush = new() { Stretch = Stretch.Fill };

    public EmoteImage() => RenderOptions.SetBitmapScalingMode(_brush, BitmapScalingMode.HighQuality);

    public ImageSource? Frame => _brush.ImageSource;

    public void ShowFrame(ImageSource frame) => _brush.ImageSource = frame;

    protected override void OnRender(DrawingContext drawingContext)
    {
        _brush.ImageSource ??= Source;
        if (_brush.ImageSource == null || RenderSize.Width <= 0 || RenderSize.Height <= 0) return;
        drawingContext.DrawRectangle(_brush, null, new Rect(RenderSize));
    }
}
