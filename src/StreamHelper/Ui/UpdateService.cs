using System;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public sealed class UpdateService : INotifyPropertyChanged
{
    private static readonly TimeSpan FirstCheckDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan DownloadLimit = TimeSpan.FromMinutes(15);

    private readonly HttpClient _api;
    private readonly HttpClient _download = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _latestUrl;
    private readonly Version _current;
    private readonly string? _exePath;
    private readonly bool _installable;
    private readonly string _dataDirectory;
    private readonly Func<bool> _restart;
    private readonly DispatcherTimer _timer = new();
    private UpdateRelease? _release;
    private bool _dismissed;
    private bool _busy;
    private bool _checking;
    private string _status = "";
    private bool _statusIsError;

    public UpdateService(HttpClient api, string? apiBase, Version current, string? exePath, bool installable, string dataDirectory, Func<bool> restart)
    {
        _api = api;
        _latestUrl = AppUpdate.LatestUrl(apiBase);
        _current = current;
        _exePath = exePath;
        _installable = installable;
        _dataDirectory = dataDirectory;
        _restart = restart;
        _download.DefaultRequestHeaders.UserAgent.ParseAdd("StreamHelper/" + AppVersion.Current);
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = CheckInterval;
            await CheckAsync();
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public bool ShowBanner => _release != null && !_dismissed;

    public string Title => _release == null ? "" : $"Доступна версия {_release.Version.ToString(4)}";

    public string Notes => _release?.Notes ?? "";

    public bool HasNotes => Notes.Length > 0;

    public string Status => _status;

    public bool HasStatus => _status.Length > 0;

    public bool StatusIsError => _statusIsError;

    public bool Busy => _busy;

    public bool NotBusy => !_busy;

    public void Start()
    {
        _timer.Interval = FirstCheckDelay;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        _download.Dispose();
    }

    public async Task CheckAsync()
    {
        if (_checking || _busy) return;
        _checking = true;
        try
        {
            var (answered, release) = await FetchLatestAsync();
            if (!answered) return;
            if (release != null && _release?.Version != release.Version) Log.Write($"Update available: {release.Version.ToString(4)}.");
            SetRelease(release);
        }
        finally
        {
            _checking = false;
        }
    }

    private async Task<(bool Answered, UpdateRelease? Newer)> FetchLatestAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _latestUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _api.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound) return (true, null);
            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"Update check: HTTP {(int)response.StatusCode}.");
                return (false, null);
            }
            var release = AppUpdate.ParseRelease(await response.Content.ReadAsStringAsync());
            return (true, release != null && AppUpdate.IsNewer(release, _current) ? release : null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Write("Update check failed: " + ex.Message);
            return (false, null);
        }
    }

    public void Later()
    {
        if (_busy) return;
        _dismissed = true;
        SetStatus("", false);
        Raise(nameof(ShowBanner));
    }

    public async Task UpdateAsync()
    {
        if (_release == null || _busy) return;
        SetBusy(true);
        var restarting = false;
        string? target = null;
        try
        {
            SetStatus("Проверяю релиз…", false);
            var (answered, fresh) = await FetchLatestAsync();
            if (answered)
            {
                SetRelease(fresh);
                if (fresh == null) return;
            }
            var release = _release!;
            if (!_installable || _exePath == null)
            {
                if (release.PageUrl.Length > 0) BrowserLauncher.Open(release.PageUrl);
                SetStatus("Эта копия программы не обновляется сама: открыта страница релиза, скачай файл оттуда.", false);
                return;
            }
            if (release.Exe == null)
            {
                SetStatus($"В релизе нет файла {AppUpdate.ExeAsset}.", true);
                return;
            }
            if (release.Checksum == null)
            {
                SetStatus($"В релизе нет файла {AppUpdate.ChecksumAsset}: без проверки обновлять нельзя.", true);
                return;
            }

            target = UpdateInstaller.NewPath(_exePath);
            SetStatus("Скачиваю обновление…", false);
            using var cts = new CancellationTokenSource(DownloadLimit);
            var expected = AppUpdate.ParseChecksum(await _download.GetStringAsync(release.Checksum.Url, cts.Token))
                           ?? throw new InvalidDataException("в файле контрольной суммы нет SHA-256");
            var progress = new Progress<int>(percent => SetStatus($"Скачиваю обновление: {percent}%", false));
            var actual = await UpdateInstaller.DownloadAsync(_download, release.Exe.Url, target, progress, cts.Token);
            UpdateInstaller.Verify(actual, expected);

            SetStatus("Устанавливаю…", false);
            UpdateInstaller.Swap(_exePath, target);
            UpdateInstaller.WriteMarker(_dataDirectory, release.Version.ToString(4));
            Log.Write($"Updated the exe to {release.Version.ToString(4)}, restarting.");
            restarting = _restart();
            if (!restarting) SetStatus("Обновление установлено. Перезапусти программу, чтобы оно заработало.", false);
        }
        catch (ChecksumMismatchException)
        {
            Discard(target);
            SetStatus("Скачанный файл повреждён (контрольная сумма не совпала), обновление отменено. Попробуй ещё раз.", true);
        }
        catch (UnauthorizedAccessException)
        {
            Discard(target);
            SetStatus($"Нет прав на запись в папку {Path.GetDirectoryName(_exePath)}. Перенеси программу в другую папку или скачай новую версию вручную.", true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            Discard(target);
            Log.Write("Update failed: " + ex.Message);
            SetStatus("Не удалось обновить: " + ex.Message, true);
        }
        finally
        {
            if (!restarting) SetBusy(false);
        }
    }

    private static void Discard(string? file)
    {
        try
        {
            if (file != null && File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Removing the downloaded update failed: " + ex.Message);
        }
    }

    private void SetRelease(UpdateRelease? release)
    {
        if (release?.Version == _release?.Version && release != null)
        {
            _release = release;
            if (_dismissed)
            {
                _dismissed = false;
                Raise(nameof(ShowBanner));
            }
            return;
        }
        _release = release;
        _dismissed = false;
        SetStatus("", false);
        Raise(nameof(ShowBanner));
        Raise(nameof(Title));
        Raise(nameof(Notes));
        Raise(nameof(HasNotes));
    }

    private void SetBusy(bool busy)
    {
        if (_busy == busy) return;
        _busy = busy;
        Raise(nameof(Busy));
        Raise(nameof(NotBusy));
    }

    private void SetStatus(string text, bool error)
    {
        _status = text;
        _statusIsError = error;
        Raise(nameof(Status));
        Raise(nameof(HasStatus));
        Raise(nameof(StatusIsError));
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
