using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class EmoteAnimatorTests
{
    private static EmoteFrames TwoFrames()
    {
        BitmapSource Pixel(byte shade)
        {
            var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { shade, shade, shade, 255 }, 4);
            bitmap.Freeze();
            return bitmap;
        }
        return new EmoteFrames(new[] { Pixel(10), Pixel(200) }, new[] { 30, 50 });
    }

    private static void Pump(int ms)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Send) { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            frame.Continue = false;
        };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    [Fact]
    public void Emotes_out_of_sight_keep_their_frame_and_move_again_once_seen()
    {
        Sta.Run(() =>
        {
            var frames = TwoFrames();
            var seen = false;
            var animator = new EmoteAnimator { Shows = _ => seen };
            var image = new Image { Source = frames.Frames[0] };
            var changes = 0;
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image)).AddValueChanged(image, (_, _) => changes++);
            animator.Add(image, frames);

            Pump(300);
            Assert.Equal(0, changes);
            Assert.Same(frames.Frames[0], image.Source);

            seen = true;
            Pump(300);
            Assert.True(changes >= 2, changes.ToString());
            animator.Stop();
            GC.KeepAlive(image);
        });
    }

    [Fact]
    public void Without_a_rule_every_emote_moves()
    {
        Sta.Run(() =>
        {
            var frames = TwoFrames();
            var animator = new EmoteAnimator();
            var image = new Image { Source = frames.Frames[0] };
            var changes = 0;
            System.ComponentModel.DependencyPropertyDescriptor.FromProperty(Image.SourceProperty, typeof(Image)).AddValueChanged(image, (_, _) => changes++);
            animator.Add(image, frames);

            Pump(300);

            Assert.True(changes >= 2, changes.ToString());
            animator.Stop();
            GC.KeepAlive(image);
        });
    }
}
