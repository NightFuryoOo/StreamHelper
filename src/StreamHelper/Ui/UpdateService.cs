using System;
using System.Collections.Generic;
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

    private readonly SettingsStore _settings;
    private readonly HttpClient _api;
    private readonly HttpClient _download = new() { Timeout = Timeout.InfiniteTimeSpan };
    private readonly string _latestUrl;
    private readonly string _releasesUrl;
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

    public UpdateService(
        SettingsStore settings, HttpClient api, string? apiBase, Version current, string? exePath, bool installable, string dataDirectory,
        Func<bool> restart)
    {
        _settings = settings;
        _api = api;
        _latestUrl = AppUpdate.LatestUrl(apiBase);
        _releasesUrl = AppUpdate.ReleasesUrl(apiBase);
        _current = AppVersion.Normalize(current);
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

    public Version Current => _current;

    public bool ShowBanner => _release != null && !_dismissed && AppUpdate.ShouldOffer(_release, _current, _settings.Current.UpdateSkipVersion);

    public string Title => _release == null ? "" : $"Доступна версия {_release.Version.ToString(4)}";

    public string CurrentText => $"Установлена версия {_current.ToString(4)}";

    public string AvailableText => _release == null ? "" : $"Доступна версия {_release.Version.ToString(4)}";

    public bool HasAvailable => _release != null;

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

    public async Task<(IReadOnlyList<UpdateRelease>? Releases, string Error)> GetChoicesAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _releasesUrl);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await _api.SendAsync(request);
            if (response.StatusCode == HttpStatusCode.NotFound) return (Array.Empty<UpdateRelease>(), "");
            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"Release list: HTTP {(int)response.StatusCode}.");
                return (null, $"GitHub не отдал список версий (HTTP {(int)response.StatusCode}).");
            }
            var releases = AppUpdate.Choosable(AppUpdate.ParseReleases(await response.Content.ReadAsStringAsync()));
            var newest = releases.Count > 0 ? releases[0] : null;
            if (newest != null && AppUpdate.IsNewer(newest, _current) && newest.Version != _release?.Version) SetRelease(newest);
            return (releases, "");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Write("Release list failed: " + ex.Message);
            return (null, "Нет связи с GitHub: " + ex.Message);
        }
    }

    public void Later()
    {
        if (_busy) return;
        _dismissed = true;
        SetStatus("", false);
        Raise(nameof(ShowBanner));
    }

    public void Never()
    {
        if (_busy || _release == null) return;
        _settings.Current.UpdateSkipVersion = _release.Version.ToString(4);
        _settings.Save();
        Log.Write($"Update {_release.Version.ToString(4)} will not be offered again.");
        SetStatus("", false);
        Raise(nameof(ShowBanner));
    }

    public async Task UpdateAsync()
    {
        if (_release == null || _busy) return;
        SetBusy(true);
        var restarting = false;
        try
        {
            SetStatus("Проверяю релиз…", false);
            var (answered, fresh) = await FetchLatestAsync();
            if (answered)
            {
                SetRelease(fresh);
                if (fresh == null) return;
            }
            restarting = await InstallAsync(_release!, null);
        }
        finally
        {
            if (!restarting) SetBusy(false);
        }
    }

    public async Task InstallChosenAsync(UpdateRelease release, IReadOnlyList<UpdateRelease> known)
    {
        if (_busy) return;
        SetBusy(true);
        var restarting = false;
        try
        {
            restarting = await InstallAsync(release, known);
        }
        finally
        {
            if (!restarting) SetBusy(false);
        }
    }

    private async Task<bool> InstallAsync(UpdateRelease release, IReadOnlyList<UpdateRelease>? chosenFrom)
    {
        var chosen = chosenFrom != null;
        var version = release.Version.ToString(4);
        string? target = null;
        try
        {
            if (!_installable || _exePath == null)
            {
                if (release.PageUrl.Length > 0) BrowserLauncher.Open(release.PageUrl);
                SetStatus("Эта копия программы не обновляется сама: открыта страница релиза, скачай файл оттуда.", false);
                return false;
            }
            if (release.Exe == null)
            {
                SetStatus($"В релизе нет файла {AppUpdate.ExeAsset}.", true);
                return false;
            }
            using var cts = new CancellationTokenSource(DownloadLimit);
            var expected = release.Exe.Digest;
            if (expected == null && release.Checksum != null)
            {
                expected = AppUpdate.ParseChecksum(await _download.GetStringAsync(release.Checksum.Url, cts.Token));
            }
            if (expected == null)
            {
                SetStatus("У файла обновления нет контрольной суммы: без проверки обновлять нельзя.", true);
                return false;
            }

            var what = chosen ? $"версию {version}" : "обновление";
            target = UpdateInstaller.NewPath(_exePath);
            SetStatus($"Скачиваю {what}…", false);
            var progress = new Progress<int>(percent => SetStatus($"Скачиваю {what}: {percent}%", false));
            var actual = await UpdateInstaller.DownloadAsync(_download, release.Exe.Url, target, progress, cts.Token);
            UpdateInstaller.Verify(actual, expected);

            if (chosen)
            {
                var backup = Profile.Backup(_dataDirectory, "До смены версии", AppVersion.Current, DateTime.Now);
                if (!backup.Success) Log.Write("Backup before switching versions failed: " + backup.Error);
                if (AppUpdate.SkipAfterChoosing(release.Version, chosenFrom!) is { } skip)
                {
                    _settings.Current.UpdateSkipVersion = skip;
                    _settings.Save();
                }
            }

            SetStatus("Устанавливаю…", false);
            UpdateInstaller.Swap(_exePath, target);
            UpdateInstaller.WriteMarker(_dataDirectory, version, chosen);
            Log.Write(chosen ? $"Switched the exe to the chosen version {version}, restarting." : $"Updated the exe to {version}, restarting.");
            var restarting = _restart();
            if (!restarting) SetStatus("Версия установлена. Перезапусти программу, чтобы она заработала.", false);
            return restarting;
        }
        catch (ChecksumMismatchException)
        {
            Discard(target);
            SetStatus("Скачанный файл повреждён (контрольная сумма не совпала), установка отменена. Попробуй ещё раз.", true);
        }
        catch (UnauthorizedAccessException)
        {
            Discard(target);
            SetStatus($"Нет прав на запись в папку {Path.GetDirectoryName(_exePath)}. Перенеси программу в другую папку или скачай файл вручную.", true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            Discard(target);
            Log.Write("Update failed: " + ex.Message);
            SetStatus("Не удалось установить: " + ex.Message, true);
        }
        return false;
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
        Raise(nameof(AvailableText));
        Raise(nameof(HasAvailable));
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
