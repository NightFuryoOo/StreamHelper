using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public partial class ArchiveWindow : Window, IArchiveRowHost
{
    private static readonly Brush RecordingBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C)));
    private static readonly Brush MutedTimeBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x8B, 0x91, 0xA0)));

    public sealed record StreamItem(ArchiveStream Stream, string Date, string Time, Brush TimeBrush, string Title, Visibility TitleVisibility, string Counts);

    private readonly ChatArchive _archive;
    private readonly ChatEmotes _emotes;
    private readonly Action<ChatMessage, ArchiveStream> _openHistory;
    private readonly Dictionary<string, ArchiveRow> _byKey = new(StringComparer.Ordinal);
    private readonly HashSet<ArchiveFilter> _filters = new(ArchiveTimeline.AllFilters);
    private readonly DispatcherTimer _searchDelay = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly DispatcherTimer _listRefresh = new() { Interval = TimeSpan.FromSeconds(10) };
    private List<ArchiveRow> _rows = new();
    private ObservableCollection<ArchiveRow> _shown = new();
    private ArchiveStream? _current;
    private int _generation;
    private bool _loading;
    private bool _listDirty;

    public ArchiveWindow(ChatArchive archive, ChatEmotes emotes, Action<ChatMessage, ArchiveStream> openHistory)
    {
        InitializeComponent();
        _archive = archive;
        _emotes = emotes;
        _openHistory = openHistory;
        Animator = new EmoteAnimator(300) { Shows = image => image.IsVisible };
        ArchiveRowView.SetHost(Timeline, this);
        Timeline.ItemsSource = _shown;
        _searchDelay.Tick += (_, _) =>
        {
            _searchDelay.Stop();
            ApplyFilter(keepEnd: false);
        };
        _listRefresh.Tick += (_, _) =>
        {
            if (!_listDirty) return;
            _listDirty = false;
            LoadStreams();
        };
        _archive.Changed += OnArchiveChanged;
        _archive.Written += OnWritten;
        Closed += (_, _) =>
        {
            _archive.Changed -= OnArchiveChanged;
            _archive.Written -= OnWritten;
            _searchDelay.Stop();
            _listRefresh.Stop();
            Animator.Stop();
        };
        _ = emotes.EnsureCatalogAsync();
        LoadStreams();
        if (StreamList.Items.Count > 0) StreamList.SelectedIndex = 0;
        else ShowEmpty();
        _listRefresh.Start();
    }

    public ChatEmotes Emotes => _emotes;

    public EmoteAnimator Animator { get; }

    public string? CurrentStreamId => _current?.Id;

    public int ShownCount => _shown.Count;

    public void OpenHistory(ChatMessage message)
    {
        if (_current != null) _openHistory(message, _current);
    }

    public void OpenLink(string url) => BrowserLauncher.Open(url);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x231D1B, 0xEEE8E6, 0x4B3F3A);
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.Text = "";
            return;
        }
        Close();
    }

    private void OnArchiveChanged() => Dispatcher.InvokeAsync(LoadStreams);

    private void LoadStreams()
    {
        var open = _archive.Open;
        var selectedId = (StreamList.SelectedItem as StreamItem)?.Stream.Id;
        var items = _archive.Streams().Select(stream => ItemOf(stream, ReferenceEquals(stream, open))).ToList();
        StreamList.SelectionChanged -= OnStreamSelected;
        StreamList.ItemsSource = items;
        var keep = items.FirstOrDefault(i => i.Stream.Id == selectedId);
        if (keep != null) StreamList.SelectedItem = keep;
        StreamList.SelectionChanged += OnStreamSelected;
        NoStreamsText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RecordingText.Text = open != null ? "Сейчас идёт запись стрима." : items.Count == 0 ? "" : "Записано стримов: " + items.Count + ".";
        if (keep != null && _current != null) ShowHeader(keep.Stream, ReferenceEquals(keep.Stream, open));
        if (keep == null && selectedId != null && items.Count > 0) StreamList.SelectedIndex = 0;
    }

    private static StreamItem ItemOf(ArchiveStream stream, bool recording)
    {
        var title = stream.Title.Trim();
        return new StreamItem(
            stream, ArchiveTimeline.StreamDate(stream), ArchiveTimeline.StreamTime(stream, recording), recording ? RecordingBrush : MutedTimeBrush,
            title, title.Length > 0 ? Visibility.Visible : Visibility.Collapsed, ArchiveTimeline.Counts(stream));
    }

    private void OnStreamSelected(object sender, SelectionChangedEventArgs e)
    {
        if (StreamList.SelectedItem is StreamItem item) _ = ShowStreamAsync(item.Stream);
    }

    private void ShowEmpty()
    {
        StreamTitle.Text = "Архив стримов";
        StreamSub.Text = "Здесь будут твои стримы: чат, донаты, подписки, награды, рейды и клипы.";
        FilterPanel.IsEnabled = false;
        SearchBox.IsEnabled = false;
        ShowNote("");
    }

    private void ShowHeader(ArchiveStream stream, bool recording)
    {
        StreamTitle.Text = stream.Title.Trim().Length > 0 ? stream.Title.Trim() : "Стрим без названия";
        var parts = new List<string> { ArchiveTimeline.StreamDate(stream), ArchiveTimeline.StreamTime(stream, recording) };
        if (stream.Game.Length > 0) parts.Add(stream.Game);
        StreamSub.Text = string.Join(" · ", parts);
    }

    public async Task ShowStreamAsync(ArchiveStream stream)
    {
        var generation = ++_generation;
        _current = stream;
        FilterPanel.IsEnabled = true;
        SearchBox.IsEnabled = true;
        ShowHeader(stream, ReferenceEquals(_archive.Open, stream));
        _loading = true;
        _rows = new List<ArchiveRow>();
        _byKey.Clear();
        SetShown(new ObservableCollection<ArchiveRow>());
        ShowNote("Загружаю…");
        var id = stream.Id;
        var start = stream.StartUtc;
        var rows = await Task.Run(() => ArchiveTimeline.Build(_archive.Read(id), start));
        if (generation != _generation) return;
        _loading = false;
        _rows = rows;
        foreach (var row in rows) _byKey[row.Key] = row;
        ApplyFilter(keepEnd: false);
    }

    private void SetShown(ObservableCollection<ArchiveRow> shown)
    {
        _shown = shown;
        Timeline.ItemsSource = shown;
    }

    private string SearchText => SearchBox.Text;

    private void ApplyFilter(bool keepEnd)
    {
        if (_loading) return;
        var search = SearchText;
        SetShown(new ObservableCollection<ArchiveRow>(_rows.Where(r => ArchiveTimeline.Matches(r, _filters, search))));
        if (_rows.Count == 0) ShowNote(_current != null && ReferenceEquals(_archive.Open, _current) ? "Запись идёт, но пока ничего не пришло." : "В этом стриме ничего не записано.");
        else ShowNote(_shown.Count == 0 ? "Ничего не найдено." : "");
        if (_shown.Count > 0) Timeline.ScrollIntoView(_shown[keepEnd ? _shown.Count - 1 : 0]);
    }

    private void ShowNote(string text)
    {
        TimelineNote.Text = text;
        TimelineNote.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        _filters.Clear();
        if (MessagesFilter.IsChecked == true) _filters.Add(ArchiveFilter.Messages);
        if (DonationsFilter.IsChecked == true) _filters.Add(ArchiveFilter.Donations);
        if (SubscriptionsFilter.IsChecked == true) _filters.Add(ArchiveFilter.Subscriptions);
        if (RewardsFilter.IsChecked == true) _filters.Add(ArchiveFilter.Rewards);
        if (RaidsFilter.IsChecked == true) _filters.Add(ArchiveFilter.Raids);
        if (ClipsFilter.IsChecked == true) _filters.Add(ArchiveFilter.Clips);
        ApplyFilter(keepEnd: false);
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _searchDelay.Stop();
        _searchDelay.Start();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_archive.Folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write("Archive folder failed: " + ex.Message);
        }
        var index = Path.Combine(_archive.Folder, ChatArchive.IndexName);
        BrowserLauncher.ShowFile(File.Exists(index) ? index : _archive.Folder);
    }

    private void OnWritten(string streamId, ArchiveLine line, bool update) => Dispatcher.InvokeAsync(() => Take(streamId, line, update));

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T found) return found;
            if (FindChild<T>(child) is { } deeper) return deeper;
        }
        return null;
    }

    private bool AtEnd()
    {
        var viewer = FindChild<ScrollViewer>(Timeline);
        return viewer == null || viewer.VerticalOffset + viewer.ViewportHeight >= viewer.ExtentHeight - 8;
    }

    private void Take(string streamId, ArchiveLine line, bool update)
    {
        _listDirty = true;
        if (_loading || _current?.Id != streamId) return;
        if (line.Kind is ArchiveKind.Deleted or ArchiveKind.Moderated)
        {
            foreach (var id in line.Ids ?? new List<string>())
            {
                if (!_byKey.TryGetValue("m:" + id, out var old) || old.Chat == null) continue;
                var fresh = new ArchiveRow(old.Chat, old.Deleted || line.Kind == ArchiveKind.Deleted, line.Moderation ?? old.Moderation);
                Replace(old, fresh);
            }
            return;
        }
        if (ArchiveTimeline.RowOf(line, _current.StartUtc) is not { } row) return;
        if (_byKey.TryGetValue(row.Key, out var existing))
        {
            if (update) Replace(existing, row);
            return;
        }
        var atEnd = AtEnd();
        _byKey[row.Key] = row;
        _rows.Insert(ArchiveTimeline.PositionOf(_rows, row.AtUtc), row);
        if (!ArchiveTimeline.Matches(row, _filters, SearchText)) return;
        var shownAt = ArchiveTimeline.PositionOf(_shown, row.AtUtc);
        _shown.Insert(shownAt, row);
        ShowNote("");
        if (atEnd && shownAt == _shown.Count - 1) Timeline.ScrollIntoView(row);
    }

    private void Replace(ArchiveRow old, ArchiveRow fresh)
    {
        _byKey[fresh.Key] = fresh;
        var index = _rows.IndexOf(old);
        if (index >= 0) _rows[index] = fresh;
        var shownIndex = _shown.IndexOf(old);
        if (shownIndex >= 0) _shown[shownIndex] = fresh;
    }
}
