using System;
using System.Drawing;
using System.Windows.Forms;

namespace StreamHelper.Ui;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _image;

    public const string ToggleChatText = "Показать/Скрыть Чат";
    public const string ToggleWindowText = "Показать/Скрыть Меню Уведомлений";

    public TrayIcon(Action toggle, Action toggleChat, Action openSettings, Action exit)
    {
        _image = AppIcon.Load(SystemInformation.SmallIconSize);
        _icon = new NotifyIcon
        {
            Icon = _image,
            Text = "StreamHelper",
            ContextMenuStrip = BuildMenu(toggle, toggleChat, openSettings, exit),
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) toggle();
        };
    }

    internal static ContextMenuStrip BuildMenu(Action toggle, Action toggleChat, Action openSettings, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(ToggleChatText, null, (_, _) => toggleChat());
        menu.Items.Add(ToggleWindowText, null, (_, _) => toggle());
        menu.Items.Add("Настройки", null, (_, _) => openSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => exit());
        return menu;
    }

    public void SetUnseen(int count) =>
        _icon.Text = count > 0 ? $"StreamHelper: {count} новых" : "StreamHelper";

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
