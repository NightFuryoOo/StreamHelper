using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public partial class ChatOverlayWindow : Window
{
    private const int MaxLines = 80;
    private static readonly TimeSpan BadgeWait = TimeSpan.FromSeconds(4);

    private static readonly TimeSpan IdleExit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan BanConfirmWindow = TimeSpan.FromSeconds(3);

    private const double HitFloor = 1.0 / 255;
    private const double PositioningBackground = 0.3;
    private static readonly TimeSpan FadeIn = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan FadeOutTime = TimeSpan.FromMilliseconds(300);

    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEE)));
    private static readonly Brush MutedBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)));
    private static readonly Brush ActionsBack = Frozen(new SolidColorBrush(Color.FromArgb(0xEE, 0x14, 0x17, 0x1D)));
    private static readonly Brush ActionsLine = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4B)));
    private static readonly Brush DoneBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)));
    private static readonly Brush ErrorBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C)));
    private static readonly Brush ShoutBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xB9, 0x8C, 0xFF)));

    private readonly SolidColorBrush _cellBack = new(Color.FromRgb(0xC3, 0xC3, 0xC3));
    private readonly SolidColorBrush _cellLine = new(Colors.Black);
    private readonly SolidColorBrush _pingText = new(Colors.Orange);
    private readonly SolidColorBrush _pingLine = new(Colors.Orange);

    private readonly Dictionary<string, SolidColorBrush> _markBrushes = new();
    private readonly SettingsStore _settings;
    private readonly ChatBadgeCatalog _badges;
    private readonly IModerationApi _moderation;
    private readonly ChatEmotes _emotes;
    private readonly HashSet<string> _shown = new();
    private readonly Queue<ChatMessage> _pending = new();
    private readonly List<ChatLine> _lines = new();
    private readonly HashSet<string> _deletedIds = new();
    private readonly DispatcherTimer _idle = new() { Interval = IdleExit };

    private readonly DispatcherTimer _front = new() { Interval = TimeSpan.FromSeconds(3) };
    private ShoutoutLimits _shoutouts = new();
    private bool _shouting;
    private bool _draining;
    private bool _menuOpen;
    private double _actionsGutter;
    private IntPtr _handle;
    private bool _positioning;
    private bool _interactive;
    private bool _fadingOut;
    private double _scroll;

    public ChatOverlayWindow(
        SettingsStore settings, ChatBadgeCatalog badges, IModerationApi moderation, ChatEmotes emotes, IVoteApi votes, VoteWatcher watcher, IChatSendApi chatSend,
        ChatterDirectory? present = null)
    {
        InitializeComponent();
        _settings = settings;
        _present = present;
        _badges = badges;
        _moderation = moderation;
        _emotes = emotes;
        MinWidth = ChatPlacement.MinWidth;
        MinHeight = ChatPlacement.MinHeight;
        _badges.Loaded += OnBadgesLoaded;
        _front.Tick += (_, _) => KeepInFront();
        _front.Start();
        Closed += (_, _) =>
        {
            _badges.Loaded -= OnBadgesLoaded;
            _idle.Stop();
            _front.Stop();
        };
        _idle.Tick += (_, _) => SetInteractive(false);
        PreviewMouseMove += (_, _) => RestartIdle();
        PreviewMouseDown += (_, _) => RestartIdle();
        PreviewMouseWheel += OnChatWheel;
        Lines.SizeChanged += (_, _) => SetScroll(_scroll);
        Viewport.SizeChanged += (_, _) => SetScroll(_scroll);
        InitVotes(votes, watcher);
        InitCompose(chatSend);
        ApplyAppearance();
        MoveToConfiguredPlace();
    }

    public event Action? GeometryChanged;

    public event Action<ChatMessage>? HistoryRequested;

    public bool IsInteractive => _interactive;

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        NativeMethods.AddExStyle(_handle, NativeMethods.WsExNoActivate | NativeMethods.WsExToolWindow | NativeMethods.WsExTransparent);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!_positioning) return;
        try
        {
            DragMove();
            GeometryChanged?.Invoke();
        }
        catch (InvalidOperationException)
        {
        }
    }

    public bool IsFadingOut => _fadingOut;

    public void Present()
    {
        var appearing = !IsVisible || _fadingOut;
        _fadingOut = false;
        if (!IsVisible)
        {
            BeginAnimation(OpacityProperty, null);
            Opacity = 0;
            Show();
        }
        if (_handle != IntPtr.Zero) NativeMethods.KeepOnTop(_handle);
        if (appearing) BeginAnimation(OpacityProperty, new DoubleAnimation(1, FadeIn) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } });
    }

    public void FadeOut()
    {
        if (!IsVisible || _fadingOut) return;
        _fadingOut = true;
        var fade = new DoubleAnimation(0, FadeOutTime) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn } };
        fade.Completed += (_, _) =>
        {
            if (!_fadingOut) return;
            _fadingOut = false;
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
        };
        BeginAnimation(OpacityProperty, fade);
    }

    public void SetPositioning(bool on)
    {
        if (on) EndTyping(returnFocus: true);
        _positioning = on;
        ApplyAppearance();
        PositionLabel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        foreach (var grip in new[] { GripTopLeft, GripTopRight, GripBottomLeft, GripBottomRight }) grip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateFrame();
        UpdateInputMode();
        UpdateCompose();
    }

    public void SetInteractive(bool on)
    {
        if (_interactive == on) return;
        _interactive = on;
        UpdateFrame();
        UpdateInputMode();
        SetVotesInteractive(on);
        UpdatePausedStrip();
        UpdateCompose();
        if (on)
        {
            foreach (var line in _lines)
            {
                if (line.Actions != null) line.Text.Margin = new Thickness(0, 0, ActionsGutter(line.Actions), 0);
            }
            _idle.Start();
            SetScroll(0);
            return;
        }

        _idle.Stop();
        foreach (var line in _lines)
        {
            if (line.Actions != null) line.Actions.Visibility = Visibility.Collapsed;
            line.Text.Margin = new Thickness(0);
            Disarm(line);
        }
        SetScroll(0);
        _ = DrainAsync();
    }

    private void OnChatWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_interactive) return;
        e.Handled = true;
        RestartIdle();
        SetScroll(_scroll + e.Delta / 120.0 * ChatPlacement.ClampFontSize(_settings.Current.ChatFontSize) * 3.5);
    }

    private void OnScrollThumbDrag(object sender, DragDeltaEventArgs e)
    {
        RestartIdle();
        SetScroll(ChatPlacement.ScrollAfterThumbDrag(_scroll, e.VerticalChange, Lines.ActualHeight, Viewport.ActualHeight));
    }

    private void SetScroll(double value)
    {
        _scroll = _interactive ? ChatPlacement.ClampScroll(value, Lines.ActualHeight, Viewport.ActualHeight) : 0;
        if (Math.Abs(Lines.Margin.Bottom + _scroll) > 0.1) Lines.Margin = new Thickness(0, 0, 0, -_scroll);
        var thumb = _interactive ? ChatPlacement.ScrollThumb(Lines.ActualHeight, Viewport.ActualHeight, _scroll) : null;
        if (thumb is not { } t)
        {
            ScrollThumb.Visibility = Visibility.Collapsed;
            return;
        }
        ScrollThumb.Height = t.Height;
        ScrollThumb.Margin = new Thickness(0, Viewport.Margin.Top + t.Top, 2, 0);
        ScrollThumb.Visibility = Visibility.Visible;
    }

    private void KeepInFront()
    {
        if (!IsVisible || _handle == IntPtr.Zero || _menuOpen || _interactive || _positioning) return;
        if (Application.Current?.Windows.OfType<Window>().Any(w => !ReferenceEquals(w, this) && w.IsActive) == true) return;
        NativeMethods.KeepOnTop(_handle);
    }

    private double ActionsGutter(FrameworkElement pill)
    {
        if (_actionsGutter > 0) return _actionsGutter;
        var was = pill.Visibility;
        pill.Visibility = Visibility.Visible;
        pill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = pill.DesiredSize.Width;
        pill.Visibility = was;
        _actionsGutter = Math.Ceiling(width) + 8;
        return _actionsGutter;
    }
    private void RestartIdle()    {
        if (!_interactive) return;
        _idle.Stop();
        _idle.Start();
    }

    private void UpdateFrame() =>
        Panel.BorderBrush = _positioning || _interactive ? (Brush)Application.Current.Resources["AccentBrush"] : Brushes.Transparent;

    private void UpdateInputMode()
    {
        if (_handle == IntPtr.Zero) return;
        if (_positioning || _interactive) NativeMethods.RemoveExStyle(_handle, NativeMethods.WsExTransparent);
        else NativeMethods.AddExStyle(_handle, NativeMethods.WsExTransparent);
    }

    public void ApplyAppearance()
    {
        var settings = _settings.Current;
        var background = Math.Max(ChatPlacement.ClampOpacity(settings.ChatOpacity), HitFloor);
        PanelBrush.Opacity = _positioning ? Math.Max(background, PositioningBackground) : background;
        _cellBack.Opacity = ChatPlacement.ClampCellOpacity(settings.ChatCellOpacity);
        foreach (var brush in _markBrushes.Values) brush.Opacity = MarkOpacity();
        _cellLine.Opacity = ChatPlacement.ClampOutlineOpacity(settings.ChatOutlineOpacity);
        ApplyPingBrushes();
        var size = ChatPlacement.ClampFontSize(settings.ChatFontSize);
        foreach (var line in _lines)
        {
            line.Box.Background = PlateFor(line.Message);
            line.Box.BorderBrush = LineFor(line.Message);
            line.Text.Foreground = TextFor(line.Message);
            line.Text.FontSize = size;
            if (line.Reply != null) line.Reply.FontSize = ReplySize(size);
            foreach (var inline in line.Text.Inlines)
            {
                if (inline is not InlineUIContainer { Child: Image image }) continue;
                if (image.Tag as string == ChatEmotes.ImageTag) image.Height = ChatEmotes.HeightFor(size);
                else image.Height = image.Width = BadgeSize(size);
            }
            line.Mute?.Rename(MuteName());
        }
    }

    public void MoveToConfiguredPlace()
    {
        var settings = _settings.Current;
        var work = SystemParameters.WorkArea;
        var (left, top, width, height) = ChatPlacement.Resolve(
            settings.ChatLeft, settings.ChatTop, settings.ChatWidth, settings.ChatHeight,
            new ScreenBounds(work.Left, work.Top, work.Right, work.Bottom),
            new ScreenBounds(
                SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth,
                SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight));
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    public void SaveGeometry()
    {
        var settings = _settings.Current;
        settings.ChatLeft = Left;
        settings.ChatTop = Top;
        settings.ChatWidth = Width;
        settings.ChatHeight = Height;
        _settings.Save();
    }

    public void AddMessage(ChatMessage message)
    {
        RememberChatter(message);
        if (message.MessageId.Length > 0)
        {
            if (_shown.Count > 500) _shown.Clear();
            if (!_shown.Add(message.MessageId)) return;
        }
        _pending.Enqueue(message);
        UpdatePausedStrip();
        if (!_draining) _ = DrainAsync();
    }

    private bool Paused => _interactive && !_typing && !_settings.Current.ChatTypingMode;

    private async Task DrainAsync()
    {
        if (Paused || _draining) return;
        _draining = true;
        try
        {
            if (!_badges.IsLoaded)
            {
                var load = _badges.EnsureLoadedAsync();
                if (_badges.IsLoading) await Task.WhenAny(load, Task.Delay(BadgeWait));
            }
            while (_pending.Count > 0 && !Paused)
            {
                await _emotes.PreloadAsync(_pending.Peek());
                if (Paused) break;
                Render(_pending.Dequeue());
            }
        }
        finally
        {
            _draining = false;
            UpdatePausedStrip();
        }
    }

    private void UpdatePausedStrip()
    {
        var waiting = Paused ? _pending.Count : 0;
        PausedStrip.Visibility = waiting > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (waiting > 0) PausedText.Text = ChatPlacement.PausedText(waiting);
    }

    private void Render(ChatMessage message)
    {
        var size = ChatPlacement.ClampFontSize(_settings.Current.ChatFontSize);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, Foreground = TextFor(message) };
        foreach (var badge in BadgeInlines(message, size)) text.Inlines.Add(badge);

        var name = NameOf(message);
        var (r, g, b) = ChatColors.Readable(message.Color, message.ChatterLogin.Length > 0 ? message.ChatterLogin : name);
        var nickBrush = Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
        text.Inlines.Add(new Run(name) { FontWeight = FontWeights.Bold, Foreground = nickBrush });
        text.Inlines.Add(new Run(": "));
        foreach (var inline in _emotes.Inlines(message, size)) text.Inlines.Add(inline);

        var root = new Grid { Background = Brushes.Transparent };
        var reply = message.Reply is { } parent ? ReplyLine(parent, size) : null;
        if (reply == null) root.Children.Add(text);
        else root.Children.Add(new StackPanel { Children = { reply, text } });
        var box = new Border
        {
            Child = root,
            Background = PlateFor(message),
            BorderBrush = LineFor(message),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 0, 2),
        };
        var line = new ChatLine(message, root, box, text, nickBrush) { Reply = reply };
        if (CanModerate(message)) AddActions(line);
        box.MouseRightButtonUp += (_, e) =>
        {
            if (!_interactive) return;
            e.Handled = true;
            ShowNickMenu(line);
        };

        if (_scroll > 0)
        {
            box.Loaded += (_, _) =>
            {
                if (_scroll > 0) SetScroll(_scroll + box.ActualHeight + box.Margin.Bottom);
            };
        }
        else if (IsVisible && !_fadingOut && ChatPlacement.ClampAppearMs(_settings.Current.ChatAppearMs) is > 0 and var appearMs)
        {
            Appear(box, TimeSpan.FromMilliseconds(appearMs));
        }
        Lines.Children.Add(box);
        _lines.Add(line);
        if (_deletedIds.Contains(message.MessageId)) MarkDeleted(line);
        while (Lines.Children.Count > MaxLines)
        {
            Lines.Children.RemoveAt(0);
            _lines.RemoveAt(0);
        }
    }

    public static double ReplySize(double size) => Math.Round(size * 0.85, 1);

    private static TextBlock ReplyLine(ChatReply parent, double size)
    {
        var line = new TextBlock { FontSize = ReplySize(size), Foreground = MutedBrush, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 0, 1) };
        line.Inlines.Add(new Run("↪ Ответ для "));
        line.Inlines.Add(new Run(parent.ParentLabel) { FontWeight = FontWeights.SemiBold });
        if (parent.ParentText.Length > 0) line.Inlines.Add(new Run(": " + parent.ParentText));
        return line;
    }

    private void Appear(Border box, TimeSpan duration)
    {
        box.Height = 0;
        box.Opacity = 0;
        box.ClipToBounds = true;
        void Start(object sender, RoutedEventArgs e)
        {
            box.Loaded -= Start;
            var inner = Math.Max(0, Lines.ActualWidth - box.Padding.Left - box.Padding.Right - box.BorderThickness.Left - box.BorderThickness.Right);
            box.Child.Measure(new Size(inner, double.PositiveInfinity));
            var target = box.Child.DesiredSize.Height + box.Padding.Top + box.Padding.Bottom + box.BorderThickness.Top + box.BorderThickness.Bottom;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var grow = new DoubleAnimation(0, target, duration) { EasingFunction = ease };
            grow.Completed += (_, _) =>
            {
                box.BeginAnimation(HeightProperty, null);
                box.ClearValue(HeightProperty);
                box.ClipToBounds = false;
            };
            var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
            fade.Completed += (_, _) =>
            {
                box.BeginAnimation(OpacityProperty, null);
                box.Opacity = 1;
            };
            box.BeginAnimation(HeightProperty, grow);
            box.BeginAnimation(OpacityProperty, fade);
        }
        box.Loaded += Start;
    }

    public void ResetChannel()
    {
        _chatters.Clear();
        HideMentions();
        HideEmotes(focusInput: false);
        Lines.Children.Clear();
        _lines.Clear();
        _deletedIds.Clear();
        _pending.Clear();
        _shown.Clear();
        _shoutouts = new ShoutoutLimits();
        ForgetReply();
        UpdatePausedStrip();
        SetScroll(0);
    }

    private sealed class ChatLine
    {
        public ChatLine(ChatMessage message, Grid root, Border box, TextBlock text, Brush nickBrush)
        {
            Message = message;
            Root = root;
            Box = box;
            Text = text;
            NickBrush = nickBrush;
        }

        public ChatMessage Message { get; }
        public Grid Root { get; }
        public Border Box { get; }
        public TextBlock Text { get; }
        public TextBlock? Reply { get; init; }
        public Brush NickBrush { get; }
        public Border? Actions { get; set; }
        public IconButton? Mute { get; set; }
        public IconButton? Custom { get; set; }
        public IconButton? Ban { get; set; }
        public IconButton? Undo { get; set; }
        public IconButton? Remove { get; set; }
        public Run? Status { get; set; }
        public Run? Removed { get; set; }
        public Run? Shout { get; set; }
        public DispatcherTimer? DisarmTimer { get; set; }
        public Armed Armed { get; set; }
    }

    private static string NameOf(ChatMessage message) => message.ChatterName.Length > 0 ? message.ChatterName : message.ChatterLogin;

    private void OnGripDrag(object sender, DragDeltaEventArgs e)
    {
        if (sender is not Thumb { Tag: string corner }) return;
        var (left, top, width, height) = ChatPlacement.Resize(corner, Left, Top, Width, Height, e.HorizontalChange, e.VerticalChange);
        Left = left;
        Top = top;
        Width = width;
        Height = height;
    }

    private void OnGripCompleted(object sender, DragCompletedEventArgs e) => GeometryChanged?.Invoke();

}
