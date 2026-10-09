using System;
using System.IO;

namespace StreamHelper.Storage;

internal static class AppPaths
{
    private const string FolderName = "StreamHelper";

    private const string LegacyFolderName = "OrderReminder";

    public static string Directory { get; private set; } = Path.Combine(AppDataRoot(), FolderName);

    public static string SettingsFile => Path.Combine(Directory, "settings.json");
    public static string DonationsFile => Path.Combine(Directory, "donations.json");
    public static string FollowersFile => Path.Combine(Directory, "followers.json");
    public static string SubscribersFile => Path.Combine(Directory, "subscribers.json");
    public static string RedemptionsFile => Path.Combine(Directory, "redemptions.json");
    public static string PingsFile => Path.Combine(Directory, "pings.json");
    public static string LogFile => Path.Combine(Directory, "log.txt");
    public static string AlertsFolder => Path.Combine(Directory, "alerts");

    public static void Init(string? overrideDirectory)
    {
        var migrated = false;
        Directory = string.IsNullOrWhiteSpace(overrideDirectory)
            ? ResolveDefaultDirectory(AppDataRoot(), out migrated)
            : Path.GetFullPath(overrideDirectory);
        System.IO.Directory.CreateDirectory(Directory);
        if (migrated) Log.Write("Data copied from the old " + LegacyFolderName + " folder.");
    }

    private static string AppDataRoot() => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

    internal static string ResolveDefaultDirectory(string appDataRoot, out bool migrated)
    {
        var target = Path.Combine(appDataRoot, FolderName);
        var legacy = Path.Combine(appDataRoot, LegacyFolderName);
        migrated = false;
        if (System.IO.Directory.Exists(target) || !System.IO.Directory.Exists(legacy)) return target;

        var temp = target + ".migrating";
        try
        {
            if (System.IO.Directory.Exists(temp)) System.IO.Directory.Delete(temp, recursive: true);
            CopyDirectory(legacy, temp);
            System.IO.Directory.Move(temp, target);
            migrated = true;
            return target;
        }
        catch
        {
            try
            {
                if (System.IO.Directory.Exists(temp)) System.IO.Directory.Delete(temp, recursive: true);
            }
            catch
            {
            }
            return legacy;
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        System.IO.Directory.CreateDirectory(destination);
        foreach (var file in System.IO.Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var directory in System.IO.Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }
}
internal static class Log
{
    private static readonly object Gate = new();
    private const long MaxBytes = 512 * 1024;

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                var path = AppPaths.LogFile;
                System.IO.Directory.CreateDirectory(AppPaths.Directory);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, path + ".old", true);
                }
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
        }
        catch
        {
        }
    }
}
