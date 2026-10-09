using StreamHelper.Storage;

namespace StreamHelper.Tests;

public class AppPathsMigrationTests
{
    private static string Legacy(TempDir dir) => Path.Combine(dir.Path, "OrderReminder");

    private static string Target(TempDir dir) => Path.Combine(dir.Path, "StreamHelper");

    private static void MakeLegacy(TempDir dir)
    {
        Directory.CreateDirectory(Path.Combine(Legacy(dir), "icons", "Заказать трек"));
        File.WriteAllText(Path.Combine(Legacy(dir), "settings.json"), "{\"ToastSeconds\":9}");
        File.WriteAllText(Path.Combine(Legacy(dir), "donations.json"), "[1,2,3]");
        File.WriteAllText(Path.Combine(Legacy(dir), "icons", "Заказать трек", "28x28.png"), "png");
    }

    [Fact]
    public void A_fresh_install_uses_the_new_name_and_creates_nothing_by_itself()
    {
        using var dir = new TempDir();

        var result = AppPaths.ResolveDefaultDirectory(dir.Path, out var migrated);

        Assert.Equal(Target(dir), result);
        Assert.False(migrated);
        Assert.False(Directory.Exists(Target(dir)));
        Assert.False(Directory.Exists(Legacy(dir)));
    }

    [Fact]
    public void The_old_folder_is_copied_with_everything_in_it_and_is_left_where_it_was()
    {
        using var dir = new TempDir();
        MakeLegacy(dir);

        var result = AppPaths.ResolveDefaultDirectory(dir.Path, out var migrated);

        Assert.True(migrated);
        Assert.Equal(Target(dir), result);
        Assert.Equal("{\"ToastSeconds\":9}", File.ReadAllText(Path.Combine(Target(dir), "settings.json")));
        Assert.Equal("[1,2,3]", File.ReadAllText(Path.Combine(Target(dir), "donations.json")));
        Assert.Equal("png", File.ReadAllText(Path.Combine(Target(dir), "icons", "Заказать трек", "28x28.png")));
        Assert.True(File.Exists(Path.Combine(Legacy(dir), "settings.json")));
        Assert.False(Directory.Exists(Target(dir) + ".migrating"));
    }

    [Fact]
    public void The_copy_happens_only_once_and_later_changes_are_not_overwritten()
    {
        using var dir = new TempDir();
        MakeLegacy(dir);
        AppPaths.ResolveDefaultDirectory(dir.Path, out _);
        File.WriteAllText(Path.Combine(Target(dir), "settings.json"), "{\"ToastSeconds\":3}");
        File.WriteAllText(Path.Combine(Legacy(dir), "settings.json"), "{\"ToastSeconds\":50}");

        var result = AppPaths.ResolveDefaultDirectory(dir.Path, out var migrated);

        Assert.False(migrated);
        Assert.Equal(Target(dir), result);
        Assert.Equal("{\"ToastSeconds\":3}", File.ReadAllText(Path.Combine(Target(dir), "settings.json")));
    }

    [Fact]
    public void An_existing_new_folder_is_never_touched_even_when_an_old_one_exists()
    {
        using var dir = new TempDir();
        MakeLegacy(dir);
        Directory.CreateDirectory(Target(dir));
        File.WriteAllText(Path.Combine(Target(dir), "settings.json"), "mine");

        var result = AppPaths.ResolveDefaultDirectory(dir.Path, out var migrated);

        Assert.False(migrated);
        Assert.Equal(Target(dir), result);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(Target(dir), "settings.json")));
        Assert.False(File.Exists(Path.Combine(Target(dir), "donations.json")));
    }

    [Fact]
    public void When_the_copy_fails_the_old_folder_keeps_being_used_and_no_half_copy_is_left()
    {
        using var dir = new TempDir();
        MakeLegacy(dir);
        using var locked = new FileStream(Path.Combine(Legacy(dir), "donations.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = AppPaths.ResolveDefaultDirectory(dir.Path, out var migrated);

        Assert.False(migrated);
        Assert.Equal(Legacy(dir), result);
        Assert.False(Directory.Exists(Target(dir)));
        Assert.False(Directory.Exists(Target(dir) + ".migrating"));
    }

    [Fact]
    public void A_leftover_temporary_copy_from_an_interrupted_start_does_not_get_in_the_way()
    {
        using var dir = new TempDir();
        MakeLegacy(dir);
        Directory.CreateDirectory(Target(dir) + ".migrating");
        File.WriteAllText(Path.Combine(Target(dir) + ".migrating", "stale.json"), "stale");

        var result = AppPaths.ResolveDefaultDirectory(dir.Path, out var migrated);

        Assert.True(migrated);
        Assert.Equal(Target(dir), result);
        Assert.False(File.Exists(Path.Combine(Target(dir), "stale.json")));
        Assert.True(File.Exists(Path.Combine(Target(dir), "settings.json")));
    }
}
