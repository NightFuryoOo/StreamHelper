using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class RewardDialog : Window
{
    private readonly IRewardApi _api;
    private readonly RewardInfo? _editing;
    private readonly IReadOnlyList<RewardInfo> _all;
    private bool _busy;

    private RewardDialog(IRewardApi api, RewardInfo? editing, IReadOnlyList<RewardInfo> all)
    {
        InitializeComponent();
        _api = api;
        _editing = editing;
        _all = all;
        foreach (var hex in RewardRules.Colors) Swatches.Children.Add(ColorSwatch.Create(this, hex, hex, "Цвет " + hex, () => ColorBox.Text = hex));
        AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler((_, _) => ClearError()));
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.CheckedEvent, new RoutedEventHandler((_, _) => ClearError()));
        AddHandler(System.Windows.Controls.Primitives.ToggleButton.UncheckedEvent, new RoutedEventHandler((_, _) => ClearError()));
        if (editing != null)
        {
            HeadingText.Text = "Изменить награду";
            SaveButton.Content = "Сохранить";
            EnabledCheck.Visibility = Visibility.Collapsed;
            NoteText.Visibility = Visibility.Collapsed;
            Fill(RewardRules.FromReward(editing));
        }
        else
        {
            ColorBox.Text = RewardRules.Colors[0];
        }
        Loaded += (_, _) =>
        {
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
    }

    public string ResultMessage { get; private set; } = "";

    public string? CreatedId { get; private set; }

    public static RewardDialog? Ask(Window owner, IRewardApi api, RewardInfo? editing, IReadOnlyList<RewardInfo> all)
    {
        var dialog = new RewardDialog(api, editing, all) { Owner = owner };
        return dialog.ShowDialog() == true ? dialog : null;
    }

    private RewardDraft Draft() => new()
    {
        Title = TitleBox.Text,
        Cost = CostBox.Text,
        Prompt = PromptBox.Text,
        UserInput = UserInputCheck.IsChecked == true,
        Color = ColorBox.Text,
        LimitPerStream = PerStreamCheck.IsChecked == true,
        PerStream = PerStreamBox.Text,
        LimitPerUser = PerUserCheck.IsChecked == true,
        PerUser = PerUserBox.Text,
        Cooldown = CooldownCheck.IsChecked == true,
        CooldownValue = CooldownBox.Text,
        CooldownUnit = UnitHours.IsChecked == true ? CooldownUnit.Hours : UnitSeconds.IsChecked == true ? CooldownUnit.Seconds : CooldownUnit.Minutes,
        SkipQueue = SkipQueueCheck.IsChecked == true,
        Enabled = _editing?.IsEnabled ?? EnabledCheck.IsChecked == true,
    };

    private void Fill(RewardDraft draft)
    {
        TitleBox.Text = draft.Title;
        CostBox.Text = draft.Cost;
        PromptBox.Text = draft.Prompt;
        UserInputCheck.IsChecked = draft.UserInput;
        ColorBox.Text = draft.Color;
        PerStreamCheck.IsChecked = draft.LimitPerStream;
        PerStreamBox.Text = draft.PerStream;
        PerUserCheck.IsChecked = draft.LimitPerUser;
        PerUserBox.Text = draft.PerUser;
        CooldownCheck.IsChecked = draft.Cooldown;
        CooldownBox.Text = draft.CooldownValue;
        UnitSeconds.IsChecked = draft.CooldownUnit == CooldownUnit.Seconds;
        UnitMinutes.IsChecked = draft.CooldownUnit == CooldownUnit.Minutes;
        UnitHours.IsChecked = draft.CooldownUnit == CooldownUnit.Hours;
        SkipQueueCheck.IsChecked = draft.SkipQueue;
    }

    private void OnColorChanged(object sender, TextChangedEventArgs e)
    {
        var hex = RewardRules.NormalizeColor(ColorBox.Text);
        ColorPreview.Background = string.IsNullOrEmpty(hex) ? Brushes.Transparent : new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var (reward, error) = RewardRules.Build(Draft(), _editing?.Id ?? "");
        if (reward == null)
        {
            ShowError(error);
            return;
        }
        var others = _all.Where(r => r.Id != _editing?.Id).Select(r => r.Title).ToList();
        if (_editing == null && RewardRules.TitleTaken(reward.Title, others))
        {
            ShowError("Награда с таким названием уже есть на канале.");
            return;
        }
        if (_editing != null && reward.Title != RewardRename.Clean(_editing.Title) && RewardRename.Candidates(reward.Title, others).Count == 0)
        {
            ShowError($"Название «{reward.Title}» уже занято другой наградой на канале.");
            return;
        }

        SetBusy(true);
        try
        {
            if (_editing == null) await CreateAsync(reward);
            else await SaveAsync(_editing, reward, others);
        }
        catch (AuthRequiredException ex)
        {
            ShowError(ex.Message);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log.Write("Saving a reward failed: " + ex.Message);
            ShowError("Нет связи с Twitch: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task CreateAsync(RewardInfo reward)
    {
        var result = await _api.CreateRewardAsync(reward, reward.Title, CancellationToken.None);
        switch (result.Outcome)
        {
            case RewardCreateOutcome.Created:
                CreatedId = result.Id;
                ResultMessage = $"Награда «{reward.Title}» создана.";
                DialogResult = true;
                break;
            case RewardCreateOutcome.TitleTaken:
                ShowError("Награда с таким названием уже есть на канале.");
                break;
            case RewardCreateOutcome.LimitReached:
                ShowError("На канале уже 50 своих наград, больше Twitch не разрешает: удали ненужные.");
                break;
            default:
                ShowError(result.Message.Length > 0 ? "Twitch не создал награду: " + result.Message : "Twitch не создал награду.");
                break;
        }
    }

    private async Task SaveAsync(RewardInfo editing, RewardInfo reward, IReadOnlyList<string> others)
    {
        var update = await _api.UpdateRewardAsync(editing.Id, reward, CancellationToken.None);
        if (!update.Success)
        {
            ShowError(update.Message);
            return;
        }
        var rename = await RewardRename.RunAsync(_api, editing.Id, editing.Title, reward.Title, others, CancellationToken.None);
        ResultMessage = rename.Success
            ? $"Награда «{RewardRename.Clean(rename.Title)}» сохранена."
            : $"Награда сохранена, но название не поменялось: {rename.Message}";
        DialogResult = true;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        SaveButton.IsEnabled = !busy;
        if (busy) ShowError("");
    }

    private void ClearError()
    {
        if (!_busy && ErrorText.Visibility == Visibility.Visible) ShowError("");
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        DialogResult = false;
    }
}
