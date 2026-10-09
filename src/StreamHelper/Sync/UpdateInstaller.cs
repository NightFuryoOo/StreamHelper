using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using StreamHelper.Storage;

namespace StreamHelper.Sync;

public sealed class ChecksumMismatchException : Exception
{
    public ChecksumMismatchException() : base("checksum mismatch")
    {
    }
}

public static class UpdateInstaller
{
    private const string MarkerFile = "update-done.txt";

    public static string NewPath(string exe) => exe + ".new";

    public static string OldPath(string exe) => exe + ".old";

    public static bool IsInstallable(string? exe, string baseDirectory, string assemblyName) =>
        !string.IsNullOrEmpty(exe) &&
        string.Equals(Path.GetExtension(exe), ".exe", StringComparison.OrdinalIgnoreCase) &&
        !File.Exists(Path.Combine(baseDirectory, assemblyName + ".dll"));

    public static async Task<string> DownloadAsync(
        HttpClient http, string url, string target, IProgress<int>? progress, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? 0;
        if (total > AppUpdate.MaxExeBytes) throw new InvalidDataException("файл обновления слишком большой");

        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            var buffer = new byte[81920];
            long done = 0;
            var lastPercent = -1;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                hash.AppendData(buffer, 0, read);
                done += read;
                if (done > AppUpdate.MaxExeBytes) throw new InvalidDataException("файл обновления слишком большой");
                if (total <= 0) continue;
                var percent = (int)(done * 100 / total);
                if (percent == lastPercent) continue;
                lastPercent = percent;
                progress?.Report(percent);
            }
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public static void Verify(string actualHash, string expectedHash)
    {
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)) throw new ChecksumMismatchException();
    }

    public static void Swap(string exe, string downloaded)
    {
        var old = OldPath(exe);
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try
        {
            File.Move(downloaded, exe);
        }
        catch
        {
            File.Move(old, exe);
            throw;
        }
    }

    public static bool CleanUp(string exe)
    {
        var clean = true;
        foreach (var leftover in new[] { OldPath(exe), NewPath(exe) })
        {
            try
            {
                if (File.Exists(leftover)) File.Delete(leftover);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                clean = false;
            }
        }
        return clean;
    }

    public static async Task CleanUpSoonAsync(string exe)
    {
        for (var attempt = 0; attempt < 15; attempt++)
        {
            if (CleanUp(exe)) return;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        Log.Write("Removing the previous version's file failed: " + OldPath(exe));
    }

    public static void WriteMarker(string dataDirectory, string version) =>
        File.WriteAllText(Path.Combine(dataDirectory, MarkerFile), version);

    public static string? TakeMarker(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, MarkerFile);
        try
        {
            if (!File.Exists(path)) return null;
            var version = File.ReadAllText(path).Trim();
            File.Delete(path);
            return version;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
