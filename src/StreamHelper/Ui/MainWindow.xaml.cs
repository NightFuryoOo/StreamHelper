using System;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using StreamHelper.Api;
using StreamHelper.Models;

namespace StreamHelper.Ui;

public partial class MainWindow : Window
{
    private readonly Services _services;
    private IntPtr _handle;
    private IntPtr _returnFocusTo;
    private readonly System.Collections.Generic.Dictionary<Redemption, System.Windows.Threading.DispatcherTimer> _confirmTimers = new();
    private Donation? _editing;
    private System.Windows.Controls.TextBox? _selecting;
    private bool _selectionFocus;
    private bool _focusTaken;
    private IntPtr _foregroundOnHover;
    private readonly System.Windows.Threading.DispatcherTimer _plainClickTimer = new();

    public MainWindow(Services services)
    {
        InitializeComponent();
        _services = services;
        DataContext = services.ViewModel;
        UpdateBannerView.DataContext = services.Updates;
        ApplySavedBounds();
        Deactivated += (_, _) =>
        {
            FinishRename(commit: true);
            EndSelection(giveFocusBack: false);
        };
        PreviewMouseDown += OnWindowPreviewMouseDown;
        PreviewKeyDown += OnWindowPreviewKeyDown;

        _plainClickTimer.Interval = TimeSpan.FromMilliseconds(System.Windows.Forms.SystemInformation.DoubleClickTime + 50);
        _plainClickTimer.Tick += (_, _) =>
        {
            _plainClickTimer.Stop();
            if (_selecting is null or { SelectionLength: 0 }) EndSelection(giveFocusBack: true);
        };
        AddHandler(CommandManager.ExecutedEvent, new ExecutedRoutedEventHandler(OnCommandExecuted), handledEventsToo: true);
    }

    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_selectionFocus && !IsInsideSelectable(e.OriginalSource as DependencyObject)) EndSelection(giveFocusBack: true);
        if (_editing == null) return;
        for (var node = e.OriginalSource as DependencyObject; node != null; node = System.Windows.Media.VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.TextBox) return;
        }
        FinishRename(commit: true);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new WindowInteropHelper(this).Handle;
        NativeMethods.AddExStyle(_handle, NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow);

        HwndSource.FromHwnd(_handle)?.AddHook(OnWindowMessage);
    }

    private const double AppearMs = 220;
    private const double DisappearMs = 160;
    private const double HiddenScale = 0.96;

    private bool _hiding;
    private int _animationVersion;

    public bool IsOpen => IsVisible && !_hiding;

    public void ShowOverlay()
    {
        var wasOpen = IsOpen;
        _hiding = false;
        if (!IsVisible)
        {
            Opacity = 0;
            ShellScale.ScaleX = ShellScale.ScaleY = HiddenScale;
            Show();
        }
        if (_handle != IntPtr.Zero) NativeMethods.KeepOnTop(_handle);
        if (!wasOpen) Animate(1, 1, AppearMs, EasingMode.EaseOut, null);
    }

    public void HideOverlay()
    {
        if (!IsOpen) return;
        FinishRename(commit: true);
        EndSelection(giveFocusBack: true);
        SaveBounds();
        _hiding = true;
        Animate(0, HiddenScale, DisappearMs, EasingMode.EaseIn, () =>
        {
            _hiding = false;
            Hide();
            UpdateActivationStyle();
        });
    }

    public void Toggle()
    {
        if (IsOpen) HideOverlay();
        else ShowOverlay();
    }

    public void ToggleTab(int tab)
    {
        if (IsOpen && _services.ViewModel.SelectedTab == tab)
        {
            HideOverlay();
            return;
        }
        _services.ViewModel.SelectedTab = tab;
        ShowOverlay();
    }

    private void Animate(double opacity, double scale, double milliseconds, EasingMode mode, Action? done)
    {
        var version = ++_animationVersion;
        var opacityFrom = Opacity;
        var scaleFrom = ShellScale.ScaleX;
        Opacity = opacity;
        ShellScale.ScaleX = scale;
        ShellScale.ScaleY = scale;

        if (!SystemParameters.ClientAreaAnimation)
        {
            BeginAnimation(OpacityProperty, null);
            ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            done?.Invoke();
            return;
        }

        var duration = TimeSpan.FromMilliseconds(milliseconds);
        var ease = new CubicEase { EasingMode = mode };
        var fade = new DoubleAnimation(opacityFrom, opacity, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        var zoom = new DoubleAnimation(scaleFrom, scale, duration) { EasingFunction = ease, FillBehavior = FillBehavior.Stop };
        fade.Completed += (_, _) =>
        {
            if (version == _animationVersion) done?.Invoke();
        };
        BeginAnimation(OpacityProperty, fade);
        ShellScale.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        ShellScale.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
    }
    public void SaveBounds()
    {
        var settings = _services.Settings.Current;
        settings.WindowLeft = Left;
        settings.WindowTop = Top;
        settings.WindowWidth = Width;
        settings.WindowHeight = Height;
        _services.Settings.Save();
    }

    private void ApplySavedBounds()
    {
        var settings = _services.Settings.Current;
        var area = SystemParameters.WorkArea;
        if (settings.WindowWidth is > 0 && settings.WindowHeight is > 0)
        {
            Width = Math.Max(MinWidth, Math.Min(settings.WindowWidth.Value, area.Width));
            Height = Math.Max(MinHeight, Math.Min(settings.WindowHeight.Value, area.Height));
        }

        if (settings.WindowLeft is { } left && settings.WindowTop is { } top && IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        else
        {
            Left = area.Right - Width - 16;
            Top = area.Top + 60;
        }
    }

    private static bool IsOnScreen(double left, double top)
    {
        var x = SystemParameters.VirtualScreenLeft;
        var y = SystemParameters.VirtualScreenTop;
        var w = SystemParameters.VirtualScreenWidth;
        var h = SystemParameters.VirtualScreenHeight;
        return left > x - 200 && left < x + w - 80 && top > y - 20 && top < y + h - 60;
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void OnHideClick(object sender, RoutedEventArgs e) => HideOverlay();

    private void OnSettingsClick(object sender, RoutedEventArgs e) => _services.OpenSettings();

    private void OnDeleteClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Donation donation) return;
        var store = _services.Donations;
        donation.Selected = false;
        var index = store.Remove(donation);
        if (index >= 0) _services.ViewModel.ShowUndo($"Удалено: {donation.DisplayName} · {donation.AmountText}", () => store.Restore(donation, index));
    }

    private async void OnAcceptRedemptionClick(object sender, RoutedEventArgs e) =>
        await DecideAsync(sender, RedemptionDecision.Fulfilled);

    private void OnRejectRedemptionClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Redemption redemption) return;
        redemption.ConfirmingReject = true;

        if (_confirmTimers.Remove(redemption, out var old)) old.Stop();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _confirmTimers.Remove(redemption);
            redemption.ConfirmingReject = false;
        };
        _confirmTimers[redemption] = timer;
        timer.Start();
    }

    private void OnCancelRejectClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Redemption redemption) return;
        redemption.ConfirmingReject = false;
        if (_confirmTimers.Remove(redemption, out var timer)) timer.Stop();
    }

    private async void OnConfirmRejectClick(object sender, RoutedEventArgs e) =>
        await DecideAsync(sender, RedemptionDecision.Canceled);

    private async System.Threading.Tasks.Task DecideAsync(object sender, RedemptionDecision decision)
    {
        if (((FrameworkElement)sender).DataContext is not Redemption redemption || redemption.IsBusy) return;
        if (_confirmTimers.Remove(redemption, out var timer)) timer.Stop();
        redemption.IsBusy = true;
        redemption.ConfirmingReject = false;
        try
        {
            var result = await Sync.RedemptionDecisions.DecideAsync(_services.Twitch, redemption, decision, System.Threading.CancellationToken.None);
            if (result.Resolved) _services.Redemptions.Remove(redemption);
            _services.ViewModel.ShowNotice(result.Message);
        }
        catch (Api.AuthRequiredException ex)
        {
            _services.ViewModel.ShowNotice(ex.Message);
        }
        catch (Exception ex)
        {
            Storage.Log.Write("Redemption decision failed: " + ex.Message);
            _services.ViewModel.ShowNotice("Не получилось: " + ex.Message);
        }
        finally
        {
            redemption.IsBusy = false;
        }
    }

    private void OnDeleteRedemptionClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Redemption redemption) return;
        redemption.Selected = false;
        if (_services.Redemptions.Remove(redemption) >= 0) _services.ViewModel.ShowNotice($"Удалено: {redemption.Name} · {redemption.Title}");
    }

    private void OnDeleteSubscriberClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Subscriber subscriber) return;
        var store = _services.Subscribers;
        subscriber.Selected = false;
        var index = store.Remove(subscriber);
        if (index >= 0) _services.ViewModel.ShowUndo($"Удалено: {subscriber.Name} ({subscriber.KindText})", () => store.Restore(subscriber, index));
    }

    private void OnDeletePingClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ChatPing ping) return;
        var store = _services.Pings;
        ping.Selected = false;
        var index = store.Remove(ping);
        if (index >= 0) _services.ViewModel.ShowUndo($"Удалён пинг: {ping.Name}", () => store.Restore(ping, index));
    }

    private void OnSelectAllPingsClick(object sender, RoutedEventArgs e) => _services.ViewModel.PingSelection.ToggleAll();

    private void OnDeleteSelectedPingsClick(object sender, RoutedEventArgs e) => _services.ViewModel.DeleteSelectedPings();

    private void OnSelectAllFollowersClick(object sender, RoutedEventArgs e) => _services.ViewModel.FollowerSelection.ToggleAll();

    private void OnSelectAllSubscribersClick(object sender, RoutedEventArgs e) => _services.ViewModel.SubscriberSelection.ToggleAll();

    private void OnDeleteSelectedSubscribersClick(object sender, RoutedEventArgs e) => _services.ViewModel.DeleteSelectedSubscribers();

    private void OnSelectAllDonationsClick(object sender, RoutedEventArgs e) => _services.ViewModel.DonationSelection.ToggleAll();

    private void OnDeleteSelectedDonationsClick(object sender, RoutedEventArgs e) => _services.ViewModel.DeleteSelectedDonations();

    private void OnDeleteSelectedFollowersClick(object sender, RoutedEventArgs e) => _services.ViewModel.DeleteSelectedFollowers();

    private void OnSelectAllRewardsClick(object sender, RoutedEventArgs e) => _services.ViewModel.RewardSelection.ToggleAll();

    private async void OnAcceptSelectedRewardsClick(object sender, RoutedEventArgs e) =>
        await DecideSelectedAsync(RedemptionDecision.Fulfilled);

    private void OnRejectSelectedRewardsClick(object sender, RoutedEventArgs e) => _services.ViewModel.BeginRewardsRejectConfirm();

    private void OnCancelRejectSelectedClick(object sender, RoutedEventArgs e) => _services.ViewModel.CancelRewardsRejectConfirm();

    private async void OnConfirmRejectSelectedClick(object sender, RoutedEventArgs e) =>
        await DecideSelectedAsync(RedemptionDecision.Canceled);

    private async System.Threading.Tasks.Task DecideSelectedAsync(RedemptionDecision decision)
    {
        var viewModel = _services.ViewModel;
        if (viewModel.RewardsBusy) return;
        var picked = viewModel.RewardSelection.Picked();
        if (picked.Count == 0) return;
        viewModel.CancelRewardsRejectConfirm();
        viewModel.RewardsBusy = true;
        try
        {
            var result = await Sync.RedemptionDecisions.DecideManyAsync(
                _services.Twitch, picked, decision, redemption => _services.Redemptions.Remove(redemption), System.Threading.CancellationToken.None);
            viewModel.ShowNotice(result.Summary(decision));
        }
        finally
        {
            viewModel.RewardsBusy = false;
        }
    }

    private void OnDeleteFollowerClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not Follower follower) return;
        var store = _services.Followers;
        follower.Selected = false;
        var index = store.Remove(follower);
        if (index >= 0) _services.ViewModel.ShowUndo($"Удалён фолловер: {follower.Name}", () => store.Restore(follower, index));
    }

    private void OnDeleteMomentClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ChannelMoment moment) return;
        var store = _services.Moments;
        moment.Selected = false;
        var index = store.Remove(moment);
        if (index >= 0) _services.ViewModel.ShowUndo($"Удалено: {moment.Name} ({moment.KindText})", () => store.Restore(moment, index));
    }

    private void OnSelectAllMomentsClick(object sender, RoutedEventArgs e) => _services.ViewModel.MomentSelection.ToggleAll();

    private void OnDeleteSelectedMomentsClick(object sender, RoutedEventArgs e) => _services.ViewModel.DeleteSelectedMoments();

    private void OnUndoClick(object sender, RoutedEventArgs e) => _services.ViewModel.Undo();

    private void OnRenameClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is Donation donation) StartRename(donation);
    }

    private void OnNameMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && ((FrameworkElement)sender).DataContext is Donation donation)
        {
            e.Handled = true;
            StartRename(donation);
        }
    }

    private void StartRename(Donation donation)
    {
        FinishRename(commit: true);
        TakeFocus();
        donation.EditText = donation.DisplayName;
        donation.IsEditing = true;
        _editing = donation;
    }

    private void FinishRename(bool commit)
    {
        if (_editing is not { } donation) return;
        _editing = null;
        if (commit) donation.Rename(donation.EditText);
        donation.IsEditing = false;
        ReleaseFocus(giveBack: true);
    }

    private void RememberFocusOwner()
    {
        if (_focusTaken) return;

        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _handle) foreground = _foregroundOnHover;
        _returnFocusTo = foreground == _handle ? IntPtr.Zero : foreground;
        _focusTaken = true;
        UpdateActivationStyle();
    }

    private void TakeFocus()
    {
        RememberFocusOwner();
        Activate();
    }

    private void ReleaseFocus(bool giveBack)
    {
        if (!_focusTaken || _editing != null || _selectionFocus) return;
        _focusTaken = false;
        UpdateActivationStyle();
        if (giveBack && NativeMethods.Exists(_returnFocusTo)) NativeMethods.SetForegroundWindow(_returnFocusTo);
        _returnFocusTo = IntPtr.Zero;
    }

    private void UpdateActivationStyle()
    {
        if (_handle == IntPtr.Zero) return;
        var activatable = _focusTaken || _editing != null || _selectionFocus || IsCursorOverSelectableText();
        if (activatable) NativeMethods.RemoveExStyle(_handle, NativeMethods.WsExNoActivate);
        else NativeMethods.AddExStyle(_handle, NativeMethods.WsExNoActivate);
    }

    private void OnSelectableHoverChanged(object sender, MouseEventArgs e)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != _handle) _foregroundOnHover = foreground;
        UpdateActivationStyle();
    }

    private void OnCopyMenuClosed(object sender, RoutedEventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (_selecting is null or { SelectionLength: 0 }) EndSelection(giveFocusBack: true);
        }, System.Windows.Threading.DispatcherPriority.Background);

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != NativeMethods.WmMouseActivate) return IntPtr.Zero;
        handled = true;
        if (!IsCursorOverSelectableText()) return (IntPtr)NativeMethods.MaNoActivate;

        RememberFocusOwner();
        _selectionFocus = true;
        return (IntPtr)NativeMethods.MaActivate;
    }

    private bool IsCursorOverSelectableText()
    {
        var (x, y) = NativeMethods.CursorPixels();
        try
        {
            return IsInsideSelectable(InputHitTest(PointFromScreen(new Point(x, y))) as DependencyObject);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void OnSelectableMouseDown(object sender, MouseButtonEventArgs e)
    {
        _plainClickTimer.Stop();
        var box = (System.Windows.Controls.TextBox)sender;
        if (_selecting != null && !ReferenceEquals(_selecting, box)) ClearSelection(_selecting);
        _selecting = box;
        _selectionFocus = true;
        RememberFocusOwner();
    }

    private void OnSelectableMouseUp(object sender, MouseButtonEventArgs e)
    {
        var box = (System.Windows.Controls.TextBox)sender;
        if (box.SelectionLength != 0) return;
        _plainClickTimer.Stop();
        _plainClickTimer.Start();
    }
    private void EndSelection(bool giveFocusBack)
    {
        if (!_selectionFocus) return;
        _selectionFocus = false;
        if (_selecting is { } box)
        {
            _selecting = null;
            ClearSelection(box);
        }
        ReleaseFocus(giveFocusBack);
    }

    private static void ClearSelection(System.Windows.Controls.TextBox box) => box.Select(0, 0);

    private void OnCommandExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (!_selectionFocus || e.Command != ApplicationCommands.Copy) return;
        Dispatcher.BeginInvoke(() => EndSelection(giveFocusBack: true), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || !_selectionFocus) return;
        e.Handled = true;
        EndSelection(giveFocusBack: true);
    }

    private static bool IsInsideSelectable(DependencyObject? source)
    {
        for (var node = source; node != null; node = node is Visual or System.Windows.Media.Media3D.Visual3D
                 ? VisualTreeHelper.GetParent(node)
                 : LogicalTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.TextBox { IsReadOnly: true }) return true;
        }
        return false;
    }

    private void OnRenameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            FinishRename(commit: true);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            FinishRename(commit: false);
        }
    }

    private void OnRenameLostFocus(object sender, KeyboardFocusChangedEventArgs e) => FinishRename(commit: true);

    private void OnRenameVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is not true) return;
        var box = (System.Windows.Controls.TextBox)sender;
        box.Dispatcher.BeginInvoke(() =>
        {
            box.Focus();
            Keyboard.Focus(box);
            box.SelectAll();
        }, System.Windows.Threading.DispatcherPriority.Input);
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(MinHeight, Height + e.VerticalChange);
    }
}
