using System.Drawing;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class AppIconTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(24)]
    [InlineData(32)]
    [InlineData(48)]
    [InlineData(64)]
    [InlineData(128)]
    public void The_program_icon_is_available_in_the_sizes_the_system_asks_for(int side)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var icon = AppIcon.Load(new Size(side, side));
                Assert.Equal(side, icon.Width);
                Assert.Equal(side, icon.Height);
                using var bitmap = icon.ToBitmap();
                var visible = 0;
                for (var y = 0; y < bitmap.Height; y++)
                {
                    for (var x = 0; x < bitmap.Width; x++)
                    {
                        if (bitmap.GetPixel(x, y).A > 0) visible++;
                    }
                }
                Assert.True(visible > side, $"the {side}px picture is empty");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}