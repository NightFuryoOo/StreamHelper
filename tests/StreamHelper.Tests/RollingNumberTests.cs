using System.Windows.Controls;
using System.Windows.Media;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class RollingNumberTests
{
    private static string Cells(RollingNumber number) =>
        string.Join("|", number.Children.Cast<object>().Select(child => child switch
        {
            Grid roll => ((TextBlock)roll.Children[0]).Text + (((TranslateTransform)((TextBlock)roll.Children[1]).RenderTransform).Y > 0 ? "↑" : "↓") + ((TextBlock)roll.Children[1]).Text,
            TextBlock still => still.Text == " " ? "_" : still.Text,
            _ => "?",
        }));

    [Fact]
    public void Only_the_digits_that_change_roll_up_when_the_number_grows_and_down_when_it_falls()
    {
        Sta.Run(() =>
        {
            var number = new RollingNumber();

            number.Show("1 234", up: true, animate: false);
            Assert.Equal("1|_|2|3|4", Cells(number));

            number.Show("1 240", up: true, animate: true);
            Assert.Equal("1|_|2|3↑4|4↑0", Cells(number));
            Assert.Equal("1 240", number.Text);

            number.Show("999", up: false, animate: true);
            Assert.Equal("2↓9|4↓9|0↓9", Cells(number));

            number.Show("1 000", up: true, animate: true);
            Assert.Equal("↑1|_|9↑0|9↑0|9↑0", Cells(number));
        });
    }
}
