using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class SettingsWindow
{
    private readonly ObservableCollection<RemovableEntry> _pingWords = new();
    private readonly ObservableCollection<RemovableEntry> _pingIgnored = new();

    private void InitPings(AppSettings settings)
    {
        foreach (var name in settings.PingIgnoredChatters) _pingIgnored.Add(new RemovableEntry(name));
        PingIgnoredList.ItemsSource = _pingIgnored;
        foreach (var word in settings.PingWords) _pingWords.Add(new RemovableEntry(word));
        PingWordsList.ItemsSource = _pingWords;
    }

    private static bool Contains(IEnumerable<string> list, string value) =>
        list.Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));

    private static void SubmitOnEnter(KeyEventArgs e, Action submit)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        submit();
    }

    private void OnAddPingWord(object sender, RoutedEventArgs e) => AddPingWords();

    private void OnPingWordKeyDown(object sender, KeyEventArgs e) => SubmitOnEnter(e, AddPingWords);

    private void AddPingWords()
    {
        var settings = _services.Settings.Current;
        var added = false;
        var full = false;
        foreach (var word in PingDetector.ParseWords(PingWordInput.Text))
        {
            if (Contains(settings.PingWords, word)) continue;
            if (settings.PingWords.Count >= PingDetector.MaxWords)
            {
                full = true;
                break;
            }
            settings.PingWords.Add(word);
            _pingWords.Add(new RemovableEntry(word));
            added = true;
        }
        if (added) _services.Settings.Save();
        ShowLine(PingWordsStatus, full ? $"Не больше {PingDetector.MaxWords} слов." : "");
        if (!full) PingWordInput.Clear();
        PingWordInput.Focus();
    }

    private void OnAddPingIgnored(object sender, RoutedEventArgs e) => AddPingIgnored();

    private void OnPingIgnoredKeyDown(object sender, KeyEventArgs e) => SubmitOnEnter(e, AddPingIgnored);

    private void AddPingIgnored()
    {
        var settings = _services.Settings.Current;
        var added = false;
        foreach (var name in PingDetector.ParseIgnoredList(PingIgnoredInput.Text))
        {
            if (Contains(settings.PingIgnoredChatters, name)) continue;
            settings.PingIgnoredChatters.Add(name);
            _pingIgnored.Add(new RemovableEntry(name));
            added = true;
        }
        if (added) _services.Settings.Save();
        PingIgnoredInput.Clear();
        PingIgnoredInput.Focus();
    }

    private void OnRemoveEntry(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not RemovableEntry item) return;
        var settings = _services.Settings.Current;
        if (_pingWords.Remove(item))
        {
            settings.PingWords.RemoveAll(x => string.Equals(x, item.Text, StringComparison.OrdinalIgnoreCase));
            PingWordsStatus.Visibility = Visibility.Collapsed;
        }
        else if (_pingIgnored.Remove(item))
        {
            settings.PingIgnoredChatters.RemoveAll(x => string.Equals(x, item.Text, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            return;
        }
        _services.Settings.Save();
    }

    private void OnResetPingIgnored(object sender, RoutedEventArgs e)
    {
        var settings = _services.Settings.Current;
        settings.PingIgnoredChatters = new List<string>(AppSettings.DefaultPingIgnored);
        _services.Settings.Save();
        _pingIgnored.Clear();
        foreach (var name in settings.PingIgnoredChatters) _pingIgnored.Add(new RemovableEntry(name));
    }
}
