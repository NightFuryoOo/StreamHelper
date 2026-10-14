using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace StreamHelper.Ui;

public partial class HelpWindow : Window
{
    private readonly Services _services;
    private readonly List<RadioButton> _sections = new();
    private string _section = HelpCatalog.Start;

    public HelpWindow(Services services, string? section = null)
    {
        InitializeComponent();
        _services = services;
        foreach (var name in HelpCatalog.Sections)
        {
            var button = new RadioButton
            {
                Style = (Style)FindResource("NavButton"),
                GroupName = "help",
                Tag = name,
                Content = new TextBlock { Text = name, TextWrapping = TextWrapping.Wrap },
            };
            AutomationProperties.SetName(button, name);
            button.Checked += (_, _) => OnSectionPicked(name);
            _sections.Add(button);
            SectionList.Children.Add(button);
        }
        PreviewKeyDown += OnPreviewKeyDown;
        Select(section ?? HelpCatalog.Start);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x231D1B, 0xEEE8E6, 0x4B3F3A);
    }

    public void Select(string section)
    {
        var button = _sections.FirstOrDefault(s => (string)s.Tag == section) ?? _sections[0];
        if (button.IsChecked == true) OnSectionPicked((string)button.Tag);
        else button.IsChecked = true;
    }

    private void OnSectionPicked(string section)
    {
        _section = section;
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Clear();
            return;
        }
        ShowCards(section, "", HelpCatalog.InSection(section), showSection: false);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        var query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            ShowCards(_section, "", HelpCatalog.InSection(_section), showSection: false);
            return;
        }
        var found = HelpCatalog.Search(query);
        ShowCards(SearchTitle(found.Count), found.Count > 0 ? $"По запросу «{query}»" : "Попробуй другое слово или выбери раздел слева.", found, showSection: true);
    }

    public static string SearchTitle(int count) =>
        count == 0 ? "Ничего не нашлось" : $"Нашлось {count} {Models.Words.Plural(count, "статья", "статьи", "статей")}";

    private void ShowCards(string title, string sub, IEnumerable<HelpArticle> articles, bool showSection)
    {
        PageTitleText.Text = title;
        PageSubText.Text = sub;
        PageSubText.Visibility = sub.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        var settings = _services.Settings.Current;
        ArticleList.ItemsSource = articles.Select(a => HelpCard.From(a, settings, showSection)).ToList();
        ArticleScroll.ScrollToTop();
    }

    private void OnOpenSettings(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HelpCard { HasPage: true } card) _services.OpenSettings(card.Page);
    }

    private void OnPictureClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HelpPicture picture) return;
        e.Handled = true;
        new HelpPictureWindow(this, picture).ShowDialog();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (SearchBox.Text.Length > 0) SearchBox.Clear();
            else Close();
        }
        else if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            SearchBox.Focus();
            SearchBox.SelectAll();
        }
    }
}
