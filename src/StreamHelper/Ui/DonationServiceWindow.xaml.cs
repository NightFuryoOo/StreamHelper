using System;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public enum DonationService
{
    DonationAlerts,
    DonatePay,
    DonateX,
}

public static class DonationServices
{
    public static readonly DonationService[] All = { DonationService.DonationAlerts, DonationService.DonatePay, DonationService.DonateX };

    public static string Title(DonationService service) => service switch
    {
        DonationService.DonatePay => "DonatePay",
        DonationService.DonateX => "DonateX",
        _ => "DonationAlerts",
    };

    public static string? KeyPage(DonationService service) => service switch
    {
        DonationService.DonatePay => "https://donatepay.ru/page/api",
        DonationService.DonateX => "https://donatex.gg/streamer/settings",
        _ => null,
    };

    public static Uri LogoUri(DonationService service) =>
        new($"pack://application:,,,/Assets/{Title(service).ToLowerInvariant()}.png");

    public static PollingService Poller(Services services, DonationService service) => service switch
    {
        DonationService.DonatePay => services.DonatePayPoller,
        DonationService.DonateX => services.DonateXPoller,
        _ => services.Poller,
    };

    public static string BrushKey(SyncState state) => state switch
    {
        SyncState.Ok => "OkBrush",
        SyncState.Error => "WarnBrush",
        SyncState.NeedsLogin => "DangerBrush",
        _ => "MutedBrush",
    };
}

public partial class DonationServiceWindow : Window
{
    private readonly Services _services;
    private readonly DonationService _service;
    private CancellationTokenSource? _connectCts;

    public DonationServiceWindow(Services services, DonationService service)
    {
        InitializeComponent();
        _services = services;
        _service = service;

        var title = DonationServices.Title(service);
        Title = title;
        TitleText.Text = title;
        LogoImage.Source = new BitmapImage(DonationServices.LogoUri(service));
        SecretPanel.Visibility = service == DonationService.DonationAlerts ? Visibility.Collapsed : Visibility.Visible;
        SecretLabel.Text = service == DonationService.DonatePay ? "Ключ API" : "Токен";
        var keyPage = DonationServices.KeyPage(service);
        KeyLink.Tag = keyPage;
        KeyLinkText.Text = keyPage?.Replace("https://", "") ?? "";

        var poller = DonationServices.Poller(services, service);
        ApplyStatus(poller.Status);
        poller.StatusChanged += OnStatusChanged;
        Closed += (_, _) =>
        {
            poller.StatusChanged -= OnStatusChanged;
            _connectCts?.Cancel();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x231D1B, 0xEEE8E6, 0x4B3F3A);
    }

    private void OnStatusChanged(SyncStatus status) => Dispatcher.InvokeAsync(() => ApplyStatus(status));

    private void ApplyStatus(SyncStatus status)
    {
        ShowStatus(status.Message, DonationServices.BrushKey(status.State));
    }

    private void ShowStatus(string message, string brushKey)
    {
        StatusText.Text = message;
        StatusDot.Fill = (Brush)Application.Current.Resources[brushKey];
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
        }
        else if (e.Key == Key.Enter && SecretPanel.Visibility == Visibility.Visible && SecretBox.IsKeyboardFocused)
        {
            e.Handled = true;
            OnConnect(this, new RoutedEventArgs());
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnKeyLinkClick(object sender, RoutedEventArgs e)
    {
        if (KeyLink.Tag is string url) BrowserLauncher.Open(url);
    }

    private async void OnConnect(object sender, RoutedEventArgs e)
    {
        switch (_service)
        {
            case DonationService.DonatePay:
                SaveDonatePay();
                break;
            case DonationService.DonateX:
                SaveDonateX();
                break;
            default:
                await ConnectDonationAlertsAsync();
                break;
        }
    }

    private void OnDisconnect(object sender, RoutedEventArgs e)
    {
        var settings = _services.Settings.Current;
        switch (_service)
        {
            case DonationService.DonatePay:
                settings.DonatePayKey = "";
                ForgetDonatePay(settings);
                _services.Settings.Save();
                SecretBox.Clear();
                _services.DonatePayPoller.Retry();
                break;
            case DonationService.DonateX:
                settings.DonateXToken = "";
                ForgetDonateX(settings);
                _services.Settings.Save();
                SecretBox.Clear();
                _services.DonateXPoller.Retry();
                break;
            default:
                settings.AccessToken = "";
                settings.RefreshToken = "";
                _services.Settings.Save();
                _services.Poller.PollSoon();
                break;
        }
    }

    private async System.Threading.Tasks.Task ConnectDonationAlertsAsync()
    {
        var settings = _services.Settings.Current;

        ConnectButton.IsEnabled = false;
        _connectCts?.Cancel();
        _connectCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var ct = _connectCts.Token;
        ShowStatus("Жду подтверждения в браузере…", "MutedBrush");
        try
        {
            var state = Guid.NewGuid().ToString("N");
            var url = _services.Client.BuildAuthorizeUrl(state);
            Log.Write("DonationAlerts authorize URL: " + url);
            if (settings.UsesCodeFlow)
            {
                var code = await OAuthLoopback.WaitForCodeAsync(settings.RedirectPort, state, () => BrowserLauncher.Open(url), ct);
                await _services.Client.ExchangeCodeAsync(code, ct);
            }
            else
            {
                var token = await OAuthLoopback.WaitForTokenAsync(settings.RedirectPort, state, () => BrowserLauncher.Open(url), ct);
                _services.Client.StoreImplicitToken(token.AccessToken, token.ExpiresInSeconds);
            }
            ShowStatus("Подключено, проверяю донаты…", "MutedBrush");
            _services.Poller.PollSoon();
        }
        catch (OperationCanceledException)
        {
            ShowStatus("Подключение отменено или истекло время ожидания.", "DangerBrush");
        }
        catch (Exception ex)
        {
            Log.Write("Connect failed: " + ex.Message);
            ShowStatus(ex.Message, "DangerBrush");
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private void SaveDonatePay()
    {
        var key = SecretBox.Password.Trim();
        if (key.Length == 0) return;
        var settings = _services.Settings.Current;
        var previous = settings.DonatePayKey;
        if (previous.Length > 0 && previous != key) ForgetDonatePay(settings);
        settings.DonatePayKey = key;
        _services.Settings.Save();
        SecretBox.Clear();
        ShowStatus("Проверяю ключ…", "MutedBrush");
        _services.DonatePayPoller.Retry();
    }

    private void SaveDonateX()
    {
        var token = SecretBox.Password.Trim();
        if (token.Length == 0) return;
        var settings = _services.Settings.Current;
        var previous = settings.DonateXToken;
        if (previous.Length > 0 && previous != token) ForgetDonateX(settings);
        settings.DonateXToken = token;
        _services.Settings.Save();
        SecretBox.Clear();
        ShowStatus("Проверяю токен…", "MutedBrush");
        _services.DonateXPoller.Retry();
    }

    private static void ForgetDonatePay(AppSettings settings)
    {
        settings.DonatePaySeen = new System.Collections.Generic.List<string>();
        settings.DonatePayBaselined = false;
    }

    private static void ForgetDonateX(AppSettings settings)
    {
        settings.DonateXSeen = new System.Collections.Generic.List<string>();
        settings.DonateXBaselined = false;
    }
}
