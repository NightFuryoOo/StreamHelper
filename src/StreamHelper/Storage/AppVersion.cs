namespace StreamHelper.Storage;

public static class AppVersion
{
    public static string Current { get; } = Read();

    private static string Read()
    {
        var version = typeof(AppVersion).Assembly.GetName().Version;
        return version == null ? "0.0.0" : $"{version.Major}.{version.Minor}.{System.Math.Max(version.Build, 0)}";
    }
}
