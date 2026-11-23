using System.Windows.Forms;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class TrayMenuTests
{
    [Fact]
    public void The_tray_menu_toggles_the_chat_and_the_notifications_window_separately()
    {
        var calls = new List<string>();
        using var menu = TrayIcon.BuildMenu(() => calls.Add("window"), () => calls.Add("chat"), () => calls.Add("title"), () => calls.Add("archive"),
            () => "Пауза оповещений DA", () => calls.Add("pause"), () => calls.Add("settings"), () => calls.Add("help"), () => calls.Add("exit"));

        var items = menu.Items.Cast<ToolStripItem>().ToList();
        Assert.Equal(new[] { "Показать/Скрыть Чат", "Показать/Скрыть Меню Уведомлений", "Название стрима", "Архив стримов", "Пауза оповещений DA", "Настройки", "Справка", "", "Выход" },
            items.Select(i => i is ToolStripSeparator ? "" : i.Text).ToArray());

        foreach (var index in new[] { 0, 1, 2, 3, 4, 5, 6, 8 }) items[index].PerformClick();
        Assert.Equal(new[] { "chat", "window", "title", "archive", "pause", "settings", "help", "exit" }, calls.ToArray());
    }

    [Fact]
    public void The_pause_item_shows_the_current_text_each_time_the_menu_opens()
    {
        var text = "Пауза оповещений DA";
        using var menu = TrayIcon.BuildMenu(() => { }, () => { }, () => { }, () => { }, () => text, () => { }, () => { }, () => { }, () => { });
        var pause = menu.Items[TrayIcon.PauseAlertsName]!;
        Assert.Equal("Пауза оповещений DA", pause.Text);

        text = "Продолжить оповещения DA (ждут: 80)";
        typeof(ToolStripDropDown).GetMethod("OnOpening", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(menu, new object[] { new System.ComponentModel.CancelEventArgs() });
        Assert.Equal("Продолжить оповещения DA (ждут: 80)", pause.Text);
    }
}

public class TrayMenuLookTests
{
    private static System.Drawing.Bitmap Draw(ContextMenuStrip menu)
    {
        var picture = new System.Drawing.Bitmap(menu.Width, menu.Height);
        menu.DrawToBitmap(picture, new System.Drawing.Rectangle(System.Drawing.Point.Empty, menu.Size));
        return picture;
    }

    [Fact]
    public void The_tray_menu_is_dark_like_the_rest_of_the_program()
    {
        Sta.Run(() =>
        {
            using var menu = TrayIcon.BuildMenu(() => { }, () => { }, () => { }, () => { }, () => "Пауза подписок DA", () => { }, () => { }, () => { }, () => { });
            Assert.IsType<TrayMenuRenderer>(menu.Renderer);
            Assert.False(menu.ShowImageMargin);
            _ = menu.Handle;
            menu.PerformLayout();

            using (var plain = Draw(menu))
            {
                var first = menu.Items[0].Bounds;
                Assert.Equal(TrayMenuRenderer.Back.ToArgb(), plain.GetPixel(menu.Width - 6, first.Top + first.Height / 2).ToArgb());
                Assert.Equal(TrayMenuRenderer.Line.ToArgb(), plain.GetPixel(0, menu.Height / 2).ToArgb());
                Assert.Equal(TrayMenuRenderer.Divider.ToArgb(), plain.GetPixel(menu.Width - 20, menu.Items[1].Bounds.Top).ToArgb());
                Assert.Equal(TrayMenuRenderer.Back.ToArgb(), plain.GetPixel(menu.Width - 20, menu.Items[7].Bounds.Top).ToArgb());
            }

            var item = menu.Items[4];
            item.Select();
            using var hover = Draw(menu);
            var middle = item.Bounds.Top + item.Bounds.Height / 2;
            Assert.Equal(TrayMenuRenderer.Hover.ToArgb(), hover.GetPixel(Math.Min(item.Bounds.Right, menu.Width) - 12, middle).ToArgb());
            Assert.Equal(TrayMenuRenderer.Back.ToArgb(), hover.GetPixel(2, middle).ToArgb());
        });
    }
}

public class TrayMenuCentreTests
{
    [Fact]
    public void The_highlight_is_as_tall_above_the_letters_as_below_them()
    {
        Sta.Run(() =>
        {
            using var menu = TrayIcon.BuildMenu(() => { }, () => { }, () => { }, () => { }, () => "Пауза подписок DA", () => { }, () => { }, () => { }, () => { });
            _ = menu.Handle;
            menu.PerformLayout();
            var item = menu.Items[2];
            item.Select();
            using var picture = new System.Drawing.Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(picture, new System.Drawing.Rectangle(System.Drawing.Point.Empty, menu.Size));

            var probe = Math.Min(item.Bounds.Right, menu.Width) - 14;
            var plate = new List<int>();
            var letters = new int[item.Bounds.Height];
            for (var y = item.Bounds.Top; y < item.Bounds.Bottom; y++)
            {
                if (picture.GetPixel(probe, y).ToArgb() == TrayMenuRenderer.Hover.ToArgb()) plate.Add(y);
                for (var x = 6; x < probe; x++)
                {
                    var c = picture.GetPixel(x, y);
                    if (c.R > 150 && c.G > 150) letters[y - item.Bounds.Top]++;
                }
            }
            var most = letters.Max();
            var above = item.Bounds.Top + Array.FindIndex(letters, n => n > 0) - plate.Min();
            var below = plate.Max() - (item.Bounds.Top + Array.FindLastIndex(letters, n => n >= most * 0.3));

            Assert.InRange(above - below, -1, 1);
        });
    }
}
