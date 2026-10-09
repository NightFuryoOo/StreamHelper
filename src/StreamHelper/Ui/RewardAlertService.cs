using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media;
using System.Windows.Threading;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public sealed record AlertImport(bool Success, string StoredName, string Error);

public sealed class RewardAlertService
{
    public const long MaxBytes = 15L * 1024 * 1024;

    public static readonly string[] Extensions = { ".mp3", ".wav", ".wma", ".m4a", ".aac", ".flac" };

    private const string StoredPrefix = "r-";

    private readonly SettingsStore _settings;
    private readonly string _folder;
    private readonly Dictionary<string, MediaPlayer> _playing = new(StringComparer.Ordinal);

    private const int WakeMs = 400;
    private const int KeepSilentMs = 3000;
    private const string SilenceFile = "_silence.wav";
    private MediaPlayer? _silent;
    private long _silentReadyAt;
    private DispatcherTimer? _silentStop;
    private readonly Dictionary<string, DispatcherTimer> _starts = new(StringComparer.Ordinal);

    public RewardAlertService(SettingsStore settings, string? alertsFolder = null)
    {
        _settings = settings;
        _folder = alertsFolder ?? AppPaths.AlertsFolder;
        RemoveSingleSoundCopy();
    }

    public event Action<string>? Failed;

    public event Action<bool>? MutedChanged;

    public bool Muted => _settings.Current.RewardSoundsMuted;

    public void SetMuted(bool muted)
    {
        if (Muted == muted) return;
        _settings.Current.RewardSoundsMuted = muted;
        _settings.Save();
        if (muted) Stop();
        Log.Write(muted ? "Reward sounds switched off." : "Reward sounds switched on.");
        MutedChanged?.Invoke(muted);
    }

    public bool ToggleMuted()
    {
        SetMuted(!Muted);
        return Muted;
    }

    public bool PlayOrdered(string rewardId) => !Muted && Play(rewardId);
    public static string SoundKey(IReadOnlyDictionary<string, string> copies, string rewardId)
    {
        foreach (var pair in copies)
        {
            if (string.Equals(pair.Value, rewardId, StringComparison.Ordinal)) return pair.Key;
        }
        return rewardId;
    }

    private string Key(string rewardId) => SoundKey(_settings.Current.RewardCopies, rewardId);

    public RewardSound? SoundOf(string rewardId) =>
        _settings.Current.RewardSounds.TryGetValue(Key(rewardId), out var sound) ? sound : null;

    public string? PathOf(string rewardId)
    {
        var sound = SoundOf(rewardId);
        if (sound == null || string.IsNullOrWhiteSpace(sound.File)) return null;
        var path = Path.Combine(_folder, Path.GetFileName(sound.File));
        return File.Exists(path) ? path : null;
    }

    public static double ClampVolume(double value) => double.IsNaN(value) ? 0.7 : Math.Clamp(value, 0, 1);

    public static string StoredBaseName(string rewardId)
    {
        var safe = new string(rewardId.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        return StoredPrefix + (safe.Length > 80 ? safe[..80] : safe);
    }

    public static AlertImport Import(string sourcePath, string alertsFolder, string rewardId)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(rewardId)) return new AlertImport(false, "", "Не выбрана награда.");
            if (!File.Exists(sourcePath)) return new AlertImport(false, "", "Файл не найден.");
            var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
            if (!Extensions.Contains(extension))
            {
                return new AlertImport(false, "", "Такой файл не воспроизвести. Подойдут " + string.Join(", ", Extensions.Select(e => e.TrimStart('.'))) + ".");
            }
            var length = new FileInfo(sourcePath).Length;
            if (length == 0) return new AlertImport(false, "", "Файл пустой.");
            if (length > MaxBytes) return new AlertImport(false, "", $"Файл больше {MaxBytes / (1024 * 1024)} МБ.");

            Directory.CreateDirectory(alertsFolder);
            var baseName = StoredBaseName(rewardId);
            var stored = baseName + extension;
            foreach (var old in Directory.GetFiles(alertsFolder, baseName + ".*"))
            {
                if (!string.Equals(Path.GetFileName(old), stored, StringComparison.OrdinalIgnoreCase)) File.Delete(old);
            }
            File.Copy(sourcePath, Path.Combine(alertsFolder, stored), overwrite: true);
            return new AlertImport(true, stored, "");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new AlertImport(false, "", "Не удалось скопировать файл: " + ex.Message);
        }
    }

    public AlertImport Choose(string rewardId, string sourcePath)
    {
        rewardId = Key(rewardId);
        StopOne(rewardId);
        var result = Import(sourcePath, _folder, rewardId);
        if (!result.Success) return result;
        var sound = SoundOf(rewardId);
        if (sound == null)
        {
            sound = new RewardSound();
            _settings.Current.RewardSounds[rewardId] = sound;
        }
        sound.File = result.StoredName;
        sound.Name = Path.GetFileName(sourcePath);
        _settings.Save();
        return result;
    }

    public void SetVolume(string rewardId, double volume)
    {
        var sound = SoundOf(rewardId);
        if (sound == null) return;
        sound.Volume = ClampVolume(volume);
        _settings.Save();
    }

    public void Clear(string rewardId) => Forget(new[] { Key(rewardId) });

    public void Forget(IEnumerable<string> rewardIds)
    {
        var changed = false;
        foreach (var id in rewardIds.Distinct(StringComparer.Ordinal))
        {
            StopOne(id);
            changed |= _settings.Current.RewardSounds.Remove(id);
            DeleteFiles(StoredBaseName(id));
        }
        if (changed) _settings.Save();
    }

    public bool Play(string rewardId)
    {
        rewardId = Key(rewardId);
        var path = PathOf(rewardId);
        if (path == null) return false;

        StopOne(rewardId);
        var volume = ClampVolume(SoundOf(rewardId)!.Volume);
        Log.Write($"Reward alert: {Path.GetFileName(path)} at {volume:P0}.");
        var wait = WakeOutput();
        if (wait > 0)
        {
            Log.Write($"Reward alert: the output was idle, the sound follows in {wait} ms.");
            StartLater(rewardId, path, volume, wait);
            return true;
        }
        return Start(rewardId, path, volume);
    }

    public void Stop()
    {
        foreach (var id in _starts.Keys.ToList()) StopOne(id);
        foreach (var pair in _playing.ToList()) Release(pair.Key, pair.Value);
        StopSilent();
    }

    private bool Start(string rewardId, string path, double volume)
    {
        var player = new MediaPlayer();
        player.MediaOpened += (_, _) =>
        {
            if (!_playing.TryGetValue(rewardId, out var current) || !ReferenceEquals(current, player)) return;
            player.Volume = volume;
            player.Play();
        };
        player.MediaEnded += (_, _) =>
        {
            Log.Write("Reward alert ended.");
            Release(rewardId, player);
        };
        player.MediaFailed += (_, e) =>
        {
            var reason = e.ErrorException?.Message ?? "неизвестная ошибка";
            Log.Write("Reward alert failed: " + reason);
            Failed?.Invoke("Не получилось проиграть этот файл: он повреждён или такой формат не поддерживается.");
            Release(rewardId, player);
        };
        _playing[rewardId] = player;
        try
        {
            player.Open(new Uri(path));
        }
        catch (Exception ex)
        {
            Log.Write("Reward alert failed: " + ex.Message);
            Failed?.Invoke("Не получилось проиграть: " + ex.Message);
            Release(rewardId, player);
            return false;
        }
        return true;
    }
    private int WakeOutput()
    {
        _silentStop?.Stop();
        var now = Environment.TickCount64;
        if (_silent == null)
        {
            var path = EnsureSilenceFile();
            if (path == null) return 0;
            var silent = new MediaPlayer { Volume = 1 };
            silent.MediaEnded += (_, _) =>
            {
                if (!ReferenceEquals(_silent, silent)) return;
                silent.Position = TimeSpan.Zero;
                silent.Play();
            };
            silent.MediaFailed += (_, e) =>
            {
                Log.Write("Silent stream failed: " + (e.ErrorException?.Message ?? "unknown error"));
                if (ReferenceEquals(_silent, silent)) _silent = null;
                try { silent.Close(); } catch (Exception) { }
            };
            try
            {
                silent.Open(new Uri(path));
                silent.Play();
            }
            catch (Exception ex)
            {
                Log.Write("Silent stream failed: " + ex.Message);
                try { silent.Close(); } catch (Exception) { }
                return 0;
            }
            _silent = silent;
            _silentReadyAt = now + WakeMs;
        }
        return (int)Math.Max(0, _silentReadyAt - now);
    }

    private void StartLater(string rewardId, string path, double volume, int delayMs)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delayMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!_starts.TryGetValue(rewardId, out var registered) || !ReferenceEquals(registered, timer)) return;
            _starts.Remove(rewardId);
            Start(rewardId, path, volume);
        };
        _starts[rewardId] = timer;
        timer.Start();
    }
    private void ArmSilentStop()
    {
        if (_silent == null) return;
        if (_silentStop == null)
        {
            _silentStop = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(KeepSilentMs) };
            _silentStop.Tick += (_, _) =>
            {
                _silentStop!.Stop();
                if (_playing.Count == 0 && _starts.Count == 0) StopSilent();
            };
        }
        _silentStop.Stop();
        _silentStop.Start();
    }

    private void StopSilent()
    {
        _silentStop?.Stop();
        var silent = _silent;
        _silent = null;
        if (silent == null) return;
        try
        {
            silent.Close();
        }
        catch (Exception ex)
        {
            Log.Write("Closing the silent stream failed: " + ex.Message);
        }
    }

    private string? EnsureSilenceFile()
    {
        var path = Path.Combine(_folder, SilenceFile);
        try
        {
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(_folder);
            const int rate = 8000;
            var data = new byte[rate * 2 * 2];
            using var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + data.Length);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(rate);
                writer.Write(rate * 2);
                writer.Write((short)2);
                writer.Write((short)16);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(data.Length);
                writer.Write(data);
            }
            File.WriteAllBytes(path, stream.ToArray());
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Writing the silent stream failed: " + ex.Message);
            return null;
        }
    }

    private void StopOne(string rewardId)
    {
        if (_starts.Remove(rewardId, out var waiting))
        {
            waiting.Stop();
            if (_playing.Count == 0 && _starts.Count == 0) ArmSilentStop();
        }
        if (_playing.TryGetValue(rewardId, out var player)) Release(rewardId, player);
    }

    private void Release(string rewardId, MediaPlayer player)
    {
        try
        {
            player.Close();
        }
        catch (Exception ex)
        {
            Log.Write("Closing the alert player failed: " + ex.Message);
        }
        if (_playing.TryGetValue(rewardId, out var current) && ReferenceEquals(current, player)) _playing.Remove(rewardId);
        if (_playing.Count == 0 && _starts.Count == 0) ArmSilentStop();
    }
    private void DeleteFiles(string baseName)
    {
        try
        {
            if (!Directory.Exists(_folder)) return;
            foreach (var file in Directory.GetFiles(_folder, baseName + ".*")) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Removing the alert sound failed: " + ex.Message);
        }
    }

    private void RemoveSingleSoundCopy()
    {
        try
        {
            if (!Directory.Exists(_folder)) return;
            foreach (var file in Directory.GetFiles(_folder, "reward.*")) File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Removing the old alert sound failed: " + ex.Message);
        }
    }
}
