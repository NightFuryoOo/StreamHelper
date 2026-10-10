using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StreamHelper.Api;
using StreamHelper.Models;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public sealed class VoteOptionEntry : INotifyPropertyChanged
{
    private string _text = "";
    private string _name = "";
    private bool _canRemove;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            Raise(nameof(Text));
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            Raise(nameof(Name));
            Raise(nameof(RemoveName));
        }
    }

    public string RemoveName => "Убрать: " + _name;

    public bool CanRemove
    {
        get => _canRemove;
        set
        {
            if (_canRemove == value) return;
            _canRemove = value;
            Raise(nameof(CanRemove));
        }
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public partial class VoteDialog : Window
{
    private readonly VoteKind _kind;
    private readonly IVoteApi _api;
    private readonly SettingsStore _settings;
    private readonly VoteWatcher _watcher;
    private readonly ObservableCollection<VoteOptionEntry> _options = new();
    private bool _busy;

    private VoteDialog(VoteKind kind, IVoteApi api, SettingsStore settings, VoteWatcher watcher)
    {
        InitializeComponent();
        _kind = kind;
        _api = api;
        _settings = settings;
        _watcher = watcher;

        var poll = kind == VoteKind.Poll;
        Title = HeadingText.Text = poll ? "Новый опрос" : "Новый предикт";
        OptionsLabel.Text = poll ? "Варианты" : "Исходы";
        AddOptionButton.Content = poll ? "+ Добавить вариант" : "+ Добавить исход";
        LastResultButton.Content = poll ? "Показать результат последнего опроса" : "Показать результат последнего предикта";
        TimeLabel.Text = poll ? "Длительность" : "Время на ставки";
        PointsPanel.Visibility = poll ? Visibility.Visible : Visibility.Collapsed;
        TitleBox.MaxLength = VoteRules.TitleMax(kind);
        OptionsList.ItemsSource = _options;
        for (var i = 0; i < VoteRules.OptionsMin; i++) _options.Add(new VoteOptionEntry());

        var current = settings.Current;
        var seconds = Math.Clamp(poll ? current.PollSeconds : current.PredictionSeconds, VoteRules.SecondsMin(kind), VoteRules.SecondsMax);
        if (seconds % 60 == 0)
        {
            TimeBox.Text = (seconds / 60).ToString(CultureInfo.InvariantCulture);
            UnitMinutes.IsChecked = true;
        }
        else
        {
            TimeBox.Text = seconds.ToString(CultureInfo.InvariantCulture);
            UnitSeconds.IsChecked = true;
        }
        PointsCheck.IsChecked = current.PollPointsVoting;
        PointsBox.Text = Math.Clamp(current.PollPointsPerVote, 1, VoteRules.PointsPerVoteMax).ToString(CultureInfo.InvariantCulture);
        PointsCostPanel.Visibility = current.PollPointsVoting ? Visibility.Visible : Visibility.Collapsed;

        UpdateCounter();
        Refresh();
        Loaded += (_, _) =>
        {
            Activate();
            NativeMethods.SetForegroundWindow(new System.Windows.Interop.WindowInteropHelper(this).Handle);
            TitleBox.Focus();
        };
    }

    public ChannelVote? Created { get; private set; }

    public bool Started { get; private set; }

    public ChannelVote? Replay { get; private set; }

    public static VoteDialog Ask(Window? owner, VoteKind kind, IVoteApi api, SettingsStore settings, VoteWatcher watcher)
    {
        var dialog = new VoteDialog(kind, api, settings, watcher);
        if (owner is { IsVisible: true }) dialog.Owner = owner;
        dialog.ShowDialog();
        return dialog;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x231D1B, 0xEEE8E6, 0x4B3F3A);
    }

    private string? AccessProblem()
    {
        var settings = _settings.Current;
        var poll = _kind == VoteKind.Poll;
        if (!settings.HasTwitchTokens) return "Сначала подключи Twitch в настройках, вкладка «Twitch».";
        if (poll ? !settings.HasPollScope : !settings.HasPredictionScope)
        {
            return $"Переподключи Twitch в настройках, вкладка «Twitch»: нужно право на {(poll ? "опросы" : "предикты")}.";
        }
        return null;
    }

    private string? Blocker()
    {
        var poll = _kind == VoteKind.Poll;
        if (AccessProblem() is { } access) return access;
        if (_watcher.IsBusy(_kind))
        {
            var running = poll ? _watcher.Poll : _watcher.Prediction;
            return poll
                ? $"Уже идёт опрос «{running?.Title}»: заверши его на карточке в чате."
                : $"Уже идёт предикт «{running?.Title}»: заверши или отмени его на карточке в чате.";
        }
        return null;
    }

    private void Refresh()
    {
        var count = _options.Count;
        for (var i = 0; i < count; i++)
        {
            _options[i].Name = (_kind == VoteKind.Poll ? "Вариант " : "Исход ") + (i + 1).ToString(CultureInfo.InvariantCulture);
            _options[i].CanRemove = count > VoteRules.OptionsMin;
        }
        AddOptionButton.Visibility = count < VoteRules.OptionsMax(_kind) ? Visibility.Visible : Visibility.Collapsed;
        var blocker = Blocker();
        if (blocker != null) ShowMessage(blocker, true);
        StartButton.IsEnabled = blocker == null && !_busy;
    }

    private void ShowMessage(string text, bool error)
    {
        ErrorText.Text = text;
        ErrorText.Foreground = (Brush)Application.Current.Resources[error ? "DangerBrush" : "MutedBrush"];
        ErrorText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCounter() => TitleCounter.Text = $"{TitleBox.Text.Trim().Length}/{VoteRules.TitleMax(_kind)}";

    private void OnTitleChanged(object sender, TextChangedEventArgs e)
    {
        UpdateCounter();
        OnAnyChanged(sender, e);
    }

    private void OnOptionChanged(object sender, TextChangedEventArgs e) => OnAnyChanged(sender, e);

    private void OnAnyChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded || _busy) return;
        if (Blocker() is { } blocker) ShowMessage(blocker, true);
        else ShowMessage("", false);
    }

    private void OnAddOption(object sender, RoutedEventArgs e)
    {
        if (_options.Count >= VoteRules.OptionsMax(_kind)) return;
        _options.Add(new VoteOptionEntry());
        Refresh();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, new Action(() => FocusOption(_options.Count - 1)));
    }

    private void OnRemoveOption(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not VoteOptionEntry entry || _options.Count <= VoteRules.OptionsMin) return;
        _options.Remove(entry);
        Refresh();
    }

    private void FocusOption(int index)
    {
        if (OptionsList.ItemContainerGenerator.ContainerFromIndex(index) is not ContentPresenter presenter) return;
        if (VisualTreeHelper.GetChildrenCount(presenter) == 0 || VisualTreeHelper.GetChild(presenter, 0) is not Grid grid) return;
        grid.Children.OfType<TextBox>().FirstOrDefault()?.Focus();
    }

    private void OnPointsToggled(object sender, RoutedEventArgs e)
    {
        PointsCostPanel.Visibility = PointsCheck.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        OnAnyChanged(sender, e);
    }

    private void OnDigitsOnly(object sender, TextCompositionEventArgs e) => e.Handled = !e.Text.All(char.IsAsciiDigit);

    private void OnDigitsPasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(typeof(string)) is not string text || !text.All(char.IsAsciiDigit)) e.CancelCommand();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            if (!_busy) Close();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            _ = StartAsync();
        }
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        if (!_busy) Close();
    }

    private async void OnStart(object sender, RoutedEventArgs e) => await StartAsync();

    private int Seconds()
    {
        if (!int.TryParse(TimeBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return 0;
        return UnitMinutes.IsChecked == true ? number * 60 : number;
    }

    private async Task StartAsync()
    {
        if (_busy) return;
        if (Blocker() is { } blocker)
        {
            ShowMessage(blocker, true);
            return;
        }

        var seconds = Seconds();
        var options = _options.Select(o => o.Text).ToList();
        string? problem;
        Func<CancellationToken, Task<VoteResult>> call;
        if (_kind == VoteKind.Poll)
        {
            var points = PointsCheck.IsChecked == true;
            _ = int.TryParse(PointsBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var perVote);
            var (draft, why) = VoteRules.Poll(TitleBox.Text, options, seconds, points, perVote);
            problem = why;
            call = ct => _api.CreatePollAsync(draft!, ct);
        }
        else
        {
            var (draft, why) = VoteRules.Prediction(TitleBox.Text, options, seconds);
            problem = why;
            call = ct => _api.CreatePredictionAsync(draft!, ct);
        }
        if (problem != null)
        {
            ShowMessage(problem, true);
            return;
        }

        _busy = true;
        StartButton.IsEnabled = false;
        LastResultButton.IsEnabled = false;
        ShowMessage(_kind == VoteKind.Poll ? "Создаю опрос…" : "Создаю предикт…", false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await call(cts.Token);
            if (!result.Success)
            {
                ShowMessage(result.Message, true);
                return;
            }
            Remember(seconds);
            Created = result.Vote;
            Started = true;
            _busy = false;
            Close();
        }
        catch (AuthRequiredException ex)
        {
            ShowMessage(ex.Message, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Write("Creating a vote failed: " + ex.Message);
            ShowMessage("Не получилось: " + ex.Message, true);
        }
        finally
        {
            if (!Started)
            {
                _busy = false;
                StartButton.IsEnabled = Blocker() == null;
                LastResultButton.IsEnabled = true;
            }
        }
    }

    private async void OnShowLastResult(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var poll = _kind == VoteKind.Poll;
        if (AccessProblem() is { } access)
        {
            ShowMessage(access, true);
            return;
        }
        if (_watcher.IsBusy(_kind))
        {
            ShowMessage(poll
                ? "Сейчас идёт опрос: его результат появится на карточке в чате, когда он закончится."
                : "Сейчас идёт предикт: его результат появится на карточке в чате, когда он закончится.", false);
            return;
        }

        _busy = true;
        StartButton.IsEnabled = false;
        LastResultButton.IsEnabled = false;
        ShowMessage(poll ? "Ищу последний опрос…" : "Ищу последний предикт…", false);
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var vote = await _api.GetLastEndedAsync(_kind, cts.Token);
            if (vote == null)
            {
                ShowMessage(poll ? "Завершённых опросов на канале ещё не было." : "Завершённых предиктов на канале ещё не было.", false);
                return;
            }
            Replay = vote;
            _busy = false;
            Close();
        }
        catch (AuthRequiredException ex)
        {
            ShowMessage(ex.Message, true);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Log.Write("Loading the last vote failed: " + ex.Message);
            ShowMessage("Не получилось: " + ex.Message, true);
        }
        finally
        {
            if (Replay == null)
            {
                _busy = false;
                LastResultButton.IsEnabled = true;
                StartButton.IsEnabled = Blocker() == null;
            }
        }
    }

    private void Remember(int seconds)
    {
        var settings = _settings.Current;
        if (_kind == VoteKind.Poll)
        {
            settings.PollSeconds = seconds;
            settings.PollPointsVoting = PointsCheck.IsChecked == true;
            if (int.TryParse(PointsBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var perVote)) settings.PollPointsPerVote = perVote;
        }
        else
        {
            settings.PredictionSeconds = seconds;
        }
        _settings.Save();
    }
}
