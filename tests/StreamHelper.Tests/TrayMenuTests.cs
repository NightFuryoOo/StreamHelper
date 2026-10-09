using System.Windows.Forms;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class TrayMenuTests
{
    [Fact]
    public void The_tray_menu_toggles_the_chat_and_the_notifications_window_separately()
    {
        var calls = new List<string>();
        using var menu = TrayIcon.BuildMenu(() => calls.Add("window"), () => calls.Add("chat"), () => calls.Add("settings"), () => calls.Add("exit"));

        var items = menu.Items.Cast<ToolStripItem>().ToList();
        Assert.Equal(new[] { "Показать/Скрыть Чат", "Показать/Скрыть Меню Уведомлений", "Настройки", "", "Выход" },
            items.Select(i => i is ToolStripSeparator ? "" : i.Text).ToArray());

        items[0].PerformClick();
        items[1].PerformClick();
        items[2].PerformClick();
        items[4].PerformClick();
        Assert.Equal(new[] { "chat", "window", "settings", "exit" }, calls.ToArray());
    }
}
