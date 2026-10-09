using System;
using System.Windows;
using System.Windows.Controls;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private (Button Tile, Image Logo, Border Badge, TextBlock Mark) TileParts(DonationService service) => service switch
    {
        DonationService.DonatePay => (TileDonatePay, LogoDonatePay, BadgeDonatePay, MarkDonatePay),
        DonationService.DonateX => (TileDonateX, LogoDonateX, BadgeDonateX, MarkDonateX),
        _ => (TileDonationAlerts, LogoDonationAlerts, BadgeDonationAlerts, MarkDonationAlerts),
    };

    private void ApplyTile(DonationService service, SyncStatus status)
    {
        var (tile, logo, badge, mark) = TileParts(service);
        var connected = status.State != SyncState.NotConnected;
        logo.Opacity = connected ? 1 : 0.45;
        badge.Visibility = connected ? Visibility.Visible : Visibility.Collapsed;
        badge.Background = StatusBrush(status.State);
        mark.Text = status.State == SyncState.Ok ? "✓" : "!";
        System.Windows.Automation.AutomationProperties.SetName(badge, status.State switch
        {
            SyncState.Ok => "Подключено",
            SyncState.NeedsLogin => "Нужно подключить заново",
            SyncState.Error => "Ошибка",
            _ => "",
        });
        tile.ToolTip = connected ? status.Message : null;
    }

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
