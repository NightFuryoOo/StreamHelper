using System.Text.RegularExpressions;
using StreamHelper.Storage;
using StreamHelper.Ui;

namespace StreamHelper.Tests;

public class HelpCatalogTests
{
    [Fact]
    public void Every_hotkey_has_an_article_that_shows_it()
    {
        var shown = HelpCatalog.Articles.SelectMany(a => a.Keys).ToHashSet();

        Assert.DoesNotContain(HotkeyActions.All, action => !shown.Contains(action));
    }

    [Fact]
    public void The_hotkey_list_shows_every_hotkey_once()
    {
        var all = HelpCatalog.Articles.Single(a => a.Title == "Все горячие клавиши").Keys;

        Assert.Equal(HotkeyActions.All.Length, all.Length);
        Assert.Equal(HotkeyActions.All.OrderBy(a => a), all.OrderBy(a => a));
    }

    [Fact]
    public void Every_settings_page_has_an_article_that_opens_it_and_no_article_points_elsewhere()
    {
        var pages = new[] { SettingsWindow.ConnectionsPage, SettingsWindow.EventsPage, SettingsWindow.RewardsPage, SettingsWindow.ChatPage, SettingsWindow.GeneralPage };

        Assert.All(pages, page => Assert.Contains(HelpCatalog.Articles, a => a.Page == page));
        Assert.All(HelpCatalog.Articles.Where(a => a.Page.Length > 0), a => Assert.Contains(a.Page, pages));
    }

    [Fact]
    public void Every_settings_page_and_every_main_window_tab_is_named_somewhere_in_the_help()
    {
        var text = string.Join("\n", HelpCatalog.Articles.Select(a => a.Title + " " + a.Text + " " + a.Where));

        foreach (var page in new[] { "Подключения", "События", "Награды", "Чат", "Общие" }) Assert.Contains("Настройки → " + page, text);
        foreach (var tab in MainTabs.Titles) Assert.Contains("«" + tab + "»", text);
    }

    [Fact]
    public void Articles_are_in_known_sections_every_section_has_some_and_titles_do_not_repeat()
    {
        Assert.All(HelpCatalog.Articles, a => Assert.Contains(a.Section, HelpCatalog.Sections));
        Assert.All(HelpCatalog.Sections, s => Assert.NotEmpty(HelpCatalog.InSection(s)));
        Assert.Equal(HelpCatalog.Articles.Count, HelpCatalog.Articles.Select(a => a.Title).Distinct().Count());
        Assert.True(HelpCatalog.Articles.Count >= 50);
    }

    [Fact]
    public void Texts_are_finished_sentences_without_stray_spaces()
    {
        foreach (var article in HelpCatalog.Articles)
        {
            foreach (var paragraph in article.Text.Split('\n'))
            {
                Assert.True(paragraph.Length > 0, article.Title);
                Assert.True(paragraph.TrimEnd().EndsWith('.') || paragraph.TrimEnd().EndsWith('»') || paragraph.TrimEnd().EndsWith(')'), article.Title + ": " + paragraph);
                Assert.DoesNotMatch(new Regex("  |^ | $"), paragraph);
                Assert.DoesNotContain("—", paragraph);
                Assert.DoesNotMatch(StiffWords, paragraph);
            }
            Assert.DoesNotContain("—", article.Title);
            Assert.DoesNotMatch(StiffWords, article.Title);
            Assert.True(article.Limit.Length == 0 || article.Limit.EndsWith('.'), article.Title);
        }
    }

    private static readonly Regex StiffWords = new(@"(?i)\bназывается\b|\bявля(ется|ются)\b|\bосуществ|\bданн(ый|ая|ое|ые|ого|ой)\b|\bнеобходимо\b|\bв рамках\b|\bв том числе\b|\bпосредством\b");

    [Fact]
    public void The_help_starts_with_where_to_begin_and_limits_are_shown_once_instead_of_in_every_text()
    {
        Assert.Equal("С чего начать", HelpCatalog.Articles[0].Title);
        Assert.All(HelpCatalog.Articles, a => Assert.DoesNotContain(HelpCatalog.OwnChannelOnly, a.Text));
        Assert.Contains(HelpCatalog.Articles, a => a.Limit == HelpCatalog.OwnChannelOnly);
        Assert.Contains(HelpCatalog.Articles, a => a.Limit == HelpCatalog.ProgramRewardsOnly);
        Assert.Equal(HelpCatalog.OwnChannelOnly, HelpCard.From(HelpCatalog.Articles.First(a => a.Title == "Фолловеры"), new AppSettings(), false).Limit);
    }

    [Theory]
    [InlineData("бан", "Бан, мут и удаление сообщения")]
    [InlineData("БАН мут", "Бан, мут и удаление сообщения")]
    [InlineData("копировать", "Выделить и скопировать")]
    [InlineData("видео", "Заказанные видео DonationAlerts")]
    [InlineData("профиль", "Перенести настройки на другой компьютер")]
    [InlineData("borderless", "Чат не виден поверх игры")]
    [InlineData("полноэкранный", "Чат не виден поверх игры")]
    [InlineData("античит", "Почему чат не рисуется в полноэкранной игре")]
    public void Search_finds_articles_by_words_in_any_case_and_puts_title_matches_first(string query, string expected)
    {
        var found = HelpCatalog.Search(query);

        Assert.Contains(found, a => a.Title == expected);
        Assert.All(found, a => Assert.True(query.Split(' ').All(w => (a.Title + a.Text + a.Where + a.Section + a.Limit).Contains(w, StringComparison.CurrentCultureIgnoreCase))));
    }

    [Fact]
    public void Help_text_turns_links_and_launch_options_into_their_own_pieces()
    {
        const string text = "Купить в [Steam](https://store.example/app), параметр `-popupwindow`.";
        var spans = HelpText.Parse(text);

        Assert.Equal(new[] { HelpSpanKind.Text, HelpSpanKind.Link, HelpSpanKind.Text, HelpSpanKind.Code, HelpSpanKind.Text }, spans.Select(s => s.Kind));
        Assert.Equal(("Steam", "https://store.example/app"), (spans[1].Text, spans[1].Url));
        Assert.Equal("-popupwindow", spans[3].Text);
        Assert.Equal("Купить в Steam, параметр -popupwindow.", HelpText.Plain(text));
        Assert.DoesNotContain('-', HelpText.ForDisplay("-popupwindow"));
        Assert.Equal("-popupwindow", HelpText.ForCopy(HelpText.ForDisplay("-popupwindow")));
    }

    [Fact]
    public void Help_links_are_https_and_no_markup_is_left_in_the_searchable_text()
    {
        var links = HelpCatalog.Articles.SelectMany(a => a.Text.Split('\n')).SelectMany(HelpText.Parse).Where(s => s.Kind == HelpSpanKind.Link).ToList();

        Assert.Contains(links, l => l.Text == "Steam" && l.Url == "https://store.steampowered.com/app/388080/Borderless_Gaming/");
        Assert.Contains(links, l => l.Text == "GitHub" && l.Url == "https://github.com/Codeusa/Borderless-Gaming");
        Assert.All(links, l => Assert.True(Uri.TryCreate(l.Url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps, l.Url));
        Assert.All(HelpCatalog.Articles, a => Assert.DoesNotMatch(new Regex(@"[\[\]`]|https?://"), a.PlainText));
        Assert.Contains(HelpCatalog.Search("github"), a => a.Title == "Чат не виден поверх игры");
    }

    [Fact]
    public void The_help_text_shows_links_as_hyperlinks_and_launch_options_without_a_line_break()
    {
        Sta.Run(() =>
        {
            var box = new HelpTextBox { Paragraphs = new[] { "Купить в [Steam](https://store.example/app).", "Параметр `-popupwindow`." } };
            var paragraphs = box.Document.Blocks.OfType<System.Windows.Documents.Paragraph>().ToList();

            Assert.Equal(2, paragraphs.Count);
            var link = Assert.Single(paragraphs[0].Inlines.OfType<System.Windows.Documents.Hyperlink>());
            Assert.Equal(new Uri("https://store.example/app"), link.NavigateUri);
            var code = paragraphs[1].Inlines.OfType<System.Windows.Documents.Run>().Single(r => r.FontFamily.Source == "Consolas");
            Assert.Equal(HelpText.NoBreakHyphen + "popupwindow", code.Text);
        });
    }

    [Fact]
    public void An_empty_search_shows_everything_and_nonsense_shows_nothing()
    {
        Assert.Equal(HelpCatalog.Articles.Count, HelpCatalog.Search("   ").Count);
        Assert.Empty(HelpCatalog.Search("абракадабра123"));
    }

    [Fact]
    public void A_card_shows_the_hotkeys_as_they_are_set_now()
    {
        var settings = new AppSettings();
        settings.SetHotkey(HotkeyAction.ChatToggle, 2, 0x7C);
        var article = HelpCatalog.Articles.First(a => a.Title == "Чат поверх игры");

        var card = HelpCard.From(article, settings, showSection: false);

        Assert.Equal("Чат: показать / скрыть", Assert.Single(card.Keys).Name);
        Assert.True(card.Keys[0].IsSet);
        Assert.Equal(HotkeyService.Describe(2, 0x7C), card.Keys[0].Key);
        Assert.True(card.HasPage);
        Assert.Equal(2, card.Paragraphs.Count);
    }

    [Fact]
    public void Every_picture_is_in_the_help_assets_and_every_help_asset_is_used()
    {
        var folder = Path.Combine(SourceFolder(), "Assets", "Help");
        var files = Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(f => f, StringComparer.Ordinal);
        var used = HelpCatalog.Articles.SelectMany(a => a.Images).Distinct().OrderBy(f => f, StringComparer.Ordinal);

        Assert.Equal(files, used);
        Assert.All(files, f => Assert.True(f!.EndsWith(".png") || f.EndsWith(".jpg"), f));
        var project = File.ReadAllText(Path.Combine(SourceFolder(), "StreamHelper.csproj"));
        Assert.Contains(@"<Resource Include=""Assets\Help\*.png"" />", project);
        Assert.Contains(@"<Resource Include=""Assets\Help\*.jpg"" />", project);
    }

    [Fact]
    public void An_article_shows_its_pictures_once_each_and_the_help_pictures_stay_small()
    {
        Assert.All(HelpCatalog.Articles, a => Assert.Equal(a.Images.Length, a.Images.Distinct().Count()));
        Assert.All(HelpCatalog.Articles, a => Assert.True(a.Images.Length <= 4, a.Title));
        var bytes = Directory.GetFiles(Path.Combine(SourceFolder(), "Assets", "Help")).Sum(f => new FileInfo(f).Length);
        Assert.True(bytes < 4_000_000, $"{bytes} bytes");
    }

    [Fact]
    public void A_card_loads_all_its_pictures_from_the_program_and_a_card_without_any_has_none()
    {
        Sta.Run(() =>
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            var article = HelpCatalog.Articles.First(a => a.Title == "Посмотрел, Сделано, Удалить");
            var withPictures = HelpCard.From(article, new AppSettings(), showSection: false);
            var photo = HelpCard.From(HelpCatalog.Articles.First(a => a.Images.Any(i => i.EndsWith(".jpg"))), new AppSettings(), showSection: false);
            var without = HelpCard.From(HelpCatalog.Articles.First(a => a.Title == "Листать чат"), new AppSettings(), showSection: false);

            Assert.True(withPictures.HasPictures);
            Assert.Equal(3, withPictures.Pictures.Count);
            Assert.All(withPictures.Pictures, p => Assert.True(((System.Windows.Media.Imaging.BitmapSource)p.Source).PixelWidth > 100));
            Assert.All(withPictures.Pictures, p => Assert.Equal(article.Title, p.Name));
            Assert.True(((System.Windows.Media.Imaging.BitmapSource)photo.Pictures[0].Source).PixelWidth > 100);
            Assert.False(without.HasPictures);
        });
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

    [Theory]
    [InlineData(0, "Ничего не нашлось")]
    [InlineData(1, "Нашлось 1 статья")]
    [InlineData(3, "Нашлось 3 статьи")]
    [InlineData(11, "Нашлось 11 статей")]
    public void The_search_title_counts_articles_in_words(int count, string title)
    {
        Assert.Equal(title, HelpWindow.SearchTitle(count));
    }
}
