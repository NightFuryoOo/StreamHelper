using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace StreamHelper.Ui;

internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _image;

    public const string ToggleChatText = "Показать/Скрыть Чат";
    public const string ToggleWindowText = "Показать/Скрыть Меню Уведомлений";

    public const string StreamTitleText = "Название стрима";

    public const string ArchiveText = "Архив стримов";

    public const string PauseAlertsName = "PauseAlerts";

    private const int ItemPadding = 6;
    private const int ItemPaddingTop = 3;

    public TrayIcon(
        Action toggle, Action toggleChat, Action openStreamInfo, Action openArchive, Func<string> pauseAlertsText, Action pauseAlerts,
        Action openSettings, Action openHelp, Action exit)
    {
        _image = AppIcon.Load(SystemInformation.SmallIconSize);
        _icon = new NotifyIcon
        {
            Icon = _image,
            Text = "StreamHelper",
            ContextMenuStrip = BuildMenu(toggle, toggleChat, openStreamInfo, openArchive, pauseAlertsText, pauseAlerts, openSettings, openHelp, exit),
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) toggle();
        };
    }

    internal static ContextMenuStrip BuildMenu(
        Action toggle, Action toggleChat, Action openStreamInfo, Action openArchive, Func<string> pauseAlertsText, Action pauseAlerts,
        Action openSettings, Action openHelp, Action exit)
    {
        var menu = new ContextMenuStrip
        {
            Renderer = new TrayMenuRenderer(),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            BackColor = TrayMenuRenderer.Back,
            ForeColor = TrayMenuRenderer.Text,
        };
        menu.HandleCreated += (_, _) => NativeMethods.RoundSmallCorners(menu.Handle, TrayMenuRenderer.BorderColorRef);
        menu.Items.Add(ToggleChatText, null, (_, _) => toggleChat());
        menu.Items.Add(ToggleWindowText, null, (_, _) => toggle());
        menu.Items.Add(StreamTitleText, null, (_, _) => openStreamInfo());
        menu.Items.Add(ArchiveText, null, (_, _) => openArchive());
        var pause = new ToolStripMenuItem(pauseAlertsText(), null, (_, _) => pauseAlerts()) { Name = PauseAlertsName };
        menu.Items.Add(pause);
        menu.Opening += (_, _) => pause.Text = pauseAlertsText();
        menu.Items.Add("Настройки", null, (_, _) => openSettings());
        menu.Items.Add("Справка", null, (_, _) => openHelp());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => exit());
        foreach (var item in menu.Items.OfType<ToolStripMenuItem>()) item.Padding = new Padding(ItemPadding, ItemPaddingTop, ItemPadding, ItemPaddingTop);
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
