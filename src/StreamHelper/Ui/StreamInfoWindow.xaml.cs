using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using StreamHelper.Api;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public static class StreamInfoRules
{
    public const string PickFromList = "Выбери категорию из списка, который появляется под полем.";

    public static string Counter(string title) => $"{title.Length} / {TwitchClient.MaxStreamTitle}";

    public static string? TitleProblem(string title) =>
        title.Trim().Length == 0 ? "Название не может быть пустым." : title.Trim().Length > TwitchClient.MaxStreamTitle ? $"Название длиннее {TwitchClient.MaxStreamTitle} символов." : null;

    public static (string? GameId, string? Problem) GameToSave(string typed, StreamCategory? picked)
    {
        var text = typed.Trim();
        if (text.Length == 0) return ("", null);
        if (picked != null && string.Equals(text, picked.Name, StringComparison.OrdinalIgnoreCase)) return (picked.Id, null);
        return (null, PickFromList);
    }
}

public partial class StreamInfoWindow : Window
{
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(350);

    private readonly IChannelInfoApi _api;
    private readonly SettingsStore _settings;
    private readonly DispatcherTimer _searchTimer = new() { Interval = SearchDelay };
    private readonly CancellationTokenSource _life = new();
    private StreamCategory? _picked;
    private bool _settingCategory;
    private bool _loaded;
    private bool _busy;
    private int _searchVersion;

    public StreamInfoWindow(IChannelInfoApi api, SettingsStore settings)
    {
        InitializeComponent();
        _api = api;
        _settings = settings;
        ChannelText.Text = settings.Current.TwitchLogin.Length > 0 ? "Канал " + settings.Current.TwitchLogin : "";
        TitleCounter.Text = StreamInfoRules.Counter("");
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await SearchAsync(CategoryBox.Text);
        };
        Closed += (_, _) =>
        {
            _searchTimer.Stop();
            _life.Cancel();
        };
        Loaded += async (_, _) =>
        {
            Activate();
            NativeMethods.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            TitleBox.Focus();
            await LoadAsync();
        };
    }

    public string? SavedTitle { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        NativeMethods.UseDarkTitleBar(handle, 0x231D1B, 0xEEE8E6, 0x4B3F3A);
        NativeMethods.KeepOnTop(handle);
    }

    private async Task LoadAsync()
    {
        SetBusy(true, "Загружаю название и категорию из Twitch…");
        try
        {
            var info = await _api.GetChannelInfoAsync(_life.Token);
            if (info == null)
            {
                SetBusy(false, "Twitch не отдал данные канала. Можно всё равно ввести название и сохранить.", error: true);
                _loaded = true;
                return;
            }
            TitleBox.Text = info.Title;
            TitleBox.CaretIndex = TitleBox.Text.Length;
            if (info.GameId.Length > 0)
            {
                _picked = new StreamCategory(info.GameId, info.GameName, "");
                SetCategoryText(info.GameName);
                _ = LoadArtAsync(info);
            }
            _loaded = true;
            SetBusy(false, "");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthRequiredException)
        {
            SetBusy(false, ex.Message, error: true);
            _loaded = true;
        }
    }

    private async Task LoadArtAsync(ChannelInfo info)
    {
        try
        {
            var found = await _api.SearchCategoriesAsync(info.GameName, _life.Token);
            if (found.FirstOrDefault(c => c.Id == info.GameId) is { } match && _picked?.Id == match.Id)
            {
                _picked = match;
                ShowArt(match.BoxArtUrl);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthRequiredException or OperationCanceledException)
        {
        }
    }

    private void SetCategoryText(string text)
    {
        _settingCategory = true;
        CategoryBox.Text = text;
        CategoryBox.CaretIndex = text.Length;
        _settingCategory = false;
    }

    private void ShowArt(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            CategoryArt.Source = null;
            return;
        }
        try
        {
            CategoryArt.Source = new BitmapImage(uri);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException)
        {
            CategoryArt.Source = null;
        }
    }

    private void OnTitleChanged(object sender, TextChangedEventArgs e) => TitleCounter.Text = StreamInfoRules.Counter(TitleBox.Text);

    private void OnCategoryTyped(object sender, TextChangedEventArgs e)
    {
        if (_settingCategory) return;
        if (_picked != null && !string.Equals(CategoryBox.Text.Trim(), _picked.Name, StringComparison.OrdinalIgnoreCase)) CategoryArt.Source = null;
        _searchTimer.Stop();
        if (CategoryBox.Text.Trim().Length == 0)
        {
            CategoryResults.Visibility = Visibility.Collapsed;
            return;
        }
        _searchTimer.Start();
    }

    private async Task SearchAsync(string query)
    {
        var version = ++_searchVersion;
        try
        {
            var found = await _api.SearchCategoriesAsync(query, _life.Token);
            if (version != _searchVersion) return;
            CategoryList.ItemsSource = found;
            CategoryResults.Visibility = found.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (found.Count == 0) ShowStatus("Такой категории Twitch не нашёл.", error: true);
            else if (StatusText.Text == "Такой категории Twitch не нашёл.") ShowStatus("");
        }
        catch (Exception ex) when (ex is HttpRequestException or AuthRequiredException)
        {
            if (version == _searchVersion) ShowStatus(ex.Message, error: true);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void OnCategoryPicked(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StreamCategory category) return;
        Pick(category);
    }

    private void Pick(StreamCategory category)
    {
        _picked = category;
        _searchTimer.Stop();
        _searchVersion++;
        SetCategoryText(category.Name);
        ShowArt(category.BoxArtUrl);
        CategoryResults.Visibility = Visibility.Collapsed;
        if (StatusText.Text == StreamInfoRules.PickFromList) ShowStatus("");
    }

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu { PlacementTarget = RecentButton, Placement = PlacementMode.Bottom };
        var recent = _settings.Current.RecentStreamTitles ?? new List<string>();
        if (recent.Count == 0) menu.Items.Add(new MenuItem { Header = "Здесь появятся названия, которые ты сохранял", IsEnabled = false });
        foreach (var title in recent)
        {
            var item = new MenuItem { Header = ChatRules.MenuText(title) };
            System.Windows.Automation.AutomationProperties.SetName(item, title);
            item.Click += (_, _) =>
            {
                TitleBox.Text = title;
                TitleBox.CaretIndex = title.Length;
                TitleBox.Focus();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private async void OnSave(object sender, RoutedEventArgs e) => await SaveAsync();

    private async Task SaveAsync()
    {
        if (_busy || !_loaded) return;
        if (StreamInfoRules.TitleProblem(TitleBox.Text) is { } titleProblem)
        {
            ShowStatus(titleProblem, error: true);
            TitleBox.Focus();
            return;
        }
        var (gameId, gameProblem) = StreamInfoRules.GameToSave(CategoryBox.Text, _picked);
        if (gameProblem != null || gameId == null)
        {
            ShowStatus(gameProblem ?? StreamInfoRules.PickFromList, error: true);
            CategoryBox.Focus();
            return;
        }
        var title = TitleBox.Text.Trim();
        SetBusy(true, "Сохраняю…");
        ChannelUpdateResult result;
        try
        {
            result = await _api.UpdateChannelInfoAsync(title, gameId, _life.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        if (!result.Success)
        {
            SetBusy(false, result.Message, error: true);
            return;
        }
        _settings.Current.RememberStreamTitle(title);
        _settings.Save();
        SavedTitle = title;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (CategoryResults.Visibility == Visibility.Visible)
            {
                CategoryResults.Visibility = Visibility.Collapsed;
                return;
            }
            Close();
            return;
        }
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
        e.Handled = true;
        if (CategoryBox.IsKeyboardFocusWithin && CategoryResults.Visibility == Visibility.Visible && CategoryList.ItemsSource is IReadOnlyList<StreamCategory> { Count: > 0 } found)
        {
            Pick(found[0]);
            return;
        }
        await SaveAsync();
    }

    private void SetBusy(bool busy, string status, bool error = false)
    {
        _busy = busy;
        SaveButton.IsEnabled = !busy;
        ShowStatus(status, error);
    }

    private void ShowStatus(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = (System.Windows.Media.Brush)FindResource(error ? "DangerBrush" : "MutedBrush");
        StatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
