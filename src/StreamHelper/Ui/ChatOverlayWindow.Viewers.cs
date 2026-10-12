using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using StreamHelper.Api;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public partial class ChatOverlayWindow
{
    private static readonly TimeSpan DeltaShown = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DeltaFade = TimeSpan.FromMilliseconds(300);
    private static readonly Brush GainBrush = Solid(Color.FromRgb(0x4A, 0xDE, 0x80));
    private static readonly Brush LossBrush = Solid(Color.FromRgb(0xFF, 0x5C, 0x5C));

    private LiveStream? _viewers;
    private int _deltaShows;

    private void InitViewers()
    {
        ViewersIcon.Child = BrandIcons.Twitch();
        ViewersStrip.ToolTipOpening += (_, _) =>
        {
            if (_viewers != null) ViewersStrip.ToolTip = ChannelLive.Describe(_viewers, DateTime.UtcNow);
        };
    }

    public void ShowViewers(LiveStream? stream)
    {
        var previous = _viewers;
        var wasShown = ViewersStrip.Visibility == Visibility.Visible;
        _viewers = stream;
        ViewersStrip.Visibility = stream == null ? Visibility.Collapsed : Visibility.Visible;
        if (stream == null)
        {
            HideDelta();
            return;
        }
        var text = ChannelLive.Count(stream.Viewers);
        AutomationProperties.SetName(ViewersCount, ChannelLive.Viewers(stream.Viewers));
        if (text == ViewersCount.Text) return;
        var animate = wasShown && previous != null && IsVisible;
        ViewersCount.Show(text, up: previous == null || stream.Viewers >= previous.Viewers, animate);
        if (animate) ShowDelta(stream.Viewers - previous!.Viewers);
        else HideDelta();
    }

    private void ShowDelta(int delta)
    {
        var show = ++_deltaShows;
        ViewersDelta.BeginAnimation(OpacityProperty, null);
        ViewersDelta.Text = ChannelLive.Change(delta);
        if (ViewersDelta.Text.Length == 0)
        {
            ViewersDelta.Visibility = Visibility.Collapsed;
            return;
        }
        ViewersDelta.Foreground = delta > 0 ? GainBrush : LossBrush;
        ViewersDelta.Visibility = Visibility.Visible;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(DeltaFade)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(DeltaFade + DeltaShown)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(DeltaFade + DeltaShown + DeltaFade)));
        fade.Completed += (_, _) =>
        {
            if (show == _deltaShows) HideDelta();
        };
        ViewersDelta.BeginAnimation(OpacityProperty, fade);
    }

    private void HideDelta()
    {
        _deltaShows++;
        ViewersDelta.BeginAnimation(OpacityProperty, null);
        ViewersDelta.Opacity = 1;
        ViewersDelta.Visibility = Visibility.Collapsed;
    }

    private void ApplyViewersSize(double size)
    {
        TextElement.SetFontSize(ViewersCount, ReplySize(size));
        ViewersDelta.FontSize = ReplySize(size);
        ViewersIcon.Width = ViewersIcon.Height = Math.Round(size);
    }
}
