using System;

namespace StreamHelper.Storage;

public static class AppVersion
{
    public static Version Value { get; } = Normalize(typeof(AppVersion).Assembly.GetName().Version);

    public static string Current { get; } = Value.ToString(4);

    public static Version Normalize(Version? version) =>
        version == null ? new Version(0, 0, 0, 0) : new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
}
