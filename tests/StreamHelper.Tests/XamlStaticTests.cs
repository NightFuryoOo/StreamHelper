using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Markup;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class XamlStaticTests
{
    [Fact]
    public void Every_static_value_the_windows_ask_for_can_be_read_at_run_time()
    {
        var assembly = typeof(BrandIcons).Assembly.GetName().Name;
        var uses = new List<(string File, string Prefix, string Namespace, string Member)>();
        foreach (var file in Directory.EnumerateFiles(SourceFolder(), "*.xaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            var prefixes = Regex.Matches(text, "xmlns:(\\w+)=\"clr-namespace:([\\w.]+)\"")
                .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
            foreach (Match use in Regex.Matches(text, "\\{x:Static\\s+(\\w+):([\\w.]+)\\}"))
            {
                var prefix = use.Groups[1].Value;
                uses.Add((Path.GetFileName(file), prefix, prefixes.GetValueOrDefault(prefix, "?"), use.Groups[2].Value));
            }
        }

        var broken = new List<string>();
        Sta.Run(() =>
        {
            foreach (var (file, prefix, ns, member) in uses)
            {
                var xaml = "<ContentControl xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" " +
                           "xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" " +
                           $"xmlns:{prefix}=\"clr-namespace:{ns};assembly={assembly}\" Content=\"{{x:Static {prefix}:{member}}}\"/>";
                try
                {
                    if (((ContentControl)XamlReader.Parse(xaml)).Content is null) broken.Add($"{file}: {member} is empty");
                }
                catch (Exception ex)
                {
                    broken.Add($"{file}: {member}: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
        });

        Assert.Contains(uses, u => u.Member == "BrandIcons.YouTubeBox");
        Assert.Empty(broken);
    }

    private static string SourceFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "StreamHelper");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/StreamHelper");
    }
}
