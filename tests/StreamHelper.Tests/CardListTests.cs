using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace StreamHelper.Tests;

public class CardListTests
{
    private static readonly XNamespace Ui = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XDocument MainWindow()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var file = Path.Combine(dir.FullName, "src", "StreamHelper", "Ui", "MainWindow.xaml");
            if (File.Exists(file)) return XDocument.Load(file);
        }
        throw new FileNotFoundException("MainWindow.xaml");
    }

    [Fact]
    public void Every_event_list_creates_cards_only_for_what_is_on_screen()
    {
        var xaml = MainWindow();
        var lists = xaml.Descendants(Ui + "ItemsControl").Where(e => ((string?)e.Attribute("ItemsSource"))?.EndsWith(".Items}") == true).ToList();

        Assert.Equal(new[] { "Donations", "Moments", "Followers", "Subscribers", "Redemptions", "Pings" },
            lists.Select(e => Regex.Match((string)e.Attribute("ItemsSource")!, @"Binding (\w+)\.Items").Groups[1].Value).ToArray());
        Assert.All(lists, e => Assert.Equal("{StaticResource CardList}", (string?)e.Attribute("Style")));
        Assert.All(lists, e => Assert.NotEqual("ScrollViewer", e.Parent!.Name.LocalName));
    }

    [Fact]
    public void The_card_list_style_virtualizes_recycles_and_scrolls_by_pixels()
    {
        var style = MainWindow().Descendants(Ui + "Style").Single(s => (string?)s.Attribute(X + "Key") == "CardList");
        string? Setter(string property) => style.Elements(Ui + "Setter").FirstOrDefault(s => (string?)s.Attribute("Property") == property)?.Attribute("Value")?.Value;

        Assert.Equal("True", Setter("VirtualizingPanel.IsVirtualizing"));
        Assert.Equal("Recycling", Setter("VirtualizingPanel.VirtualizationMode"));
        Assert.Equal("Pixel", Setter("VirtualizingPanel.ScrollUnit"));
        Assert.Single(style.Descendants(Ui + "VirtualizingStackPanel"));
        var scroller = Assert.Single(style.Descendants(Ui + "ScrollViewer"));
        Assert.Equal("True", (string?)scroller.Attribute("CanContentScroll"));
        Assert.Single(scroller.Elements(Ui + "ItemsPresenter"));
    }
}
