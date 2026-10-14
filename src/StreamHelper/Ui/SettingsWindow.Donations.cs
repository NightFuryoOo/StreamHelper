using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private (Button Tile, Image Logo, Border Badge, TextBlock Mark, Ellipse Dot, TextBlock State) TileParts(DonationService service) => service switch
    {
        DonationService.DonatePay => (TileDonatePay, LogoDonatePay, BadgeDonatePay, MarkDonatePay, StateDotDonatePay, StateTextDonatePay),
        DonationService.DonateX => (TileDonateX, LogoDonateX, BadgeDonateX, MarkDonateX, StateDotDonateX, StateTextDonateX),
        _ => (TileDonationAlerts, LogoDonationAlerts, BadgeDonationAlerts, MarkDonationAlerts, StateDotDonationAlerts, StateTextDonationAlerts),
    };

    public static string TileState(SyncState state) => state switch
    {
        SyncState.Ok => "Подключено",
        SyncState.NeedsLogin => "Нужно подключить заново",
        SyncState.Error => "Ошибка",
        _ => "Не подключено",
    };

    private void ApplyTile(DonationService service, SyncStatus status)
    {
        var (tile, logo, badge, mark, dot, state) = TileParts(service);
        var connected = status.State != SyncState.NotConnected;
        logo.Opacity = connected ? 1 : 0.45;
        badge.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        badge.Background = StatusBrush(status.State);
        mark.Text = status.State == SyncState.Ok ? "✓" : "!";
        dot.Fill = StatusBrush(status.State);
        state.Text = TileState(status.State);
        System.Windows.Automation.AutomationProperties.SetName(badge, connected ? TileState(status.State) : "");
        tile.ToolTip = connected ? status.Message : null;
    }

    private void ApplyVideoState(SyncStatus status)
    {
        var text = VideoState(status.State);
        VideoStateDonationAlerts.Text = text;
        VideoStateDonationAlerts.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    public static string VideoState(SyncState state) => state switch
    {
        SyncState.Ok => "Заказанные видео: вкл",
        SyncState.Error => "Заказанные видео: нет связи",
        _ => "",
    };

    private void OnServiceTileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<DonationService>(tag, out var service)) return;
        try
        {
            new DonationServiceWindow(_services, service) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            Log.Write("Service window failed: " + ex);
            throw;
        }
    }
}
