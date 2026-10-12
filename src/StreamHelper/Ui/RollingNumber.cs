using System;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace StreamHelper.Ui;

public sealed class RollingNumber : StackPanel
{
    public static readonly TimeSpan RollTime = TimeSpan.FromMilliseconds(650);

    public RollingNumber() => Orientation = Orientation.Horizontal;

    public string Text { get; private set; } = "";

    public void Show(string text, bool up, bool animate)
    {
        var old = Text;
        Text = text;
        Children.Clear();
        var shift = TextElement.GetFontSize(this) * 1.4;
        for (var i = 0; i < text.Length; i++)
        {
            var at = i - (text.Length - old.Length);
            char? before = at >= 0 && at < old.Length ? old[at] : null;
            var after = text[i];
            var roll = animate && before != after && char.IsDigit(after);
            Children.Add(roll ? Roll(before, after, up ? shift : -shift) : Glyph(after));
        }
    }

    private static TextBlock Glyph(char? c) => new() { Text = c?.ToString() ?? "" };

    private static Grid Roll(char? before, char after, double shift)
    {
        var leaving = Glyph(before);
        var coming = Glyph(after);
        var outMove = new TranslateTransform();
        var inMove = new TranslateTransform(0, shift);
        leaving.RenderTransform = outMove;
        coming.RenderTransform = inMove;
        var cell = new Grid { ClipToBounds = true, Children = { leaving, coming } };
        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        var leave = new DoubleAnimation(0, -shift, RollTime) { EasingFunction = ease };
        leave.Completed += (_, _) => cell.Children.Remove(leaving);
        outMove.BeginAnimation(TranslateTransform.YProperty, leave);
        inMove.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(shift, 0, RollTime) { EasingFunction = ease });
        return cell;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new Peer(this);

    private sealed class Peer : FrameworkElementAutomationPeer
    {
        public Peer(RollingNumber owner) : base(owner)
        {
        }

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;

        protected override string GetClassNameCore() => nameof(RollingNumber);
    }
}
