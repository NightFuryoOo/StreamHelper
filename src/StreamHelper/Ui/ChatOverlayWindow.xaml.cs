using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using StreamHelper.Api;
using StreamHelper.Storage;
using StreamHelper.Sync;

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
    private static readonly Brush ActionsBack = Frozen(new SolidColorBrush(Color.FromArgb(0xEE, 0x14, 0x17, 0x1D)));
    private static readonly Brush ActionsLine = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4B)));
    private static readonly Brush DoneBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)));
    private static readonly Brush ErrorBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x5C)));

    private readonly SolidColorBrush _cellBack = new(Color.FromRgb(0xC3, 0xC3, 0xC3));
    private readonly SolidColorBrush _cellLine = new(Colors.Black);

    private readonly Dictionary<string, SolidColorBrush> _markBrushes = new();
    private readonly SettingsStore _settings;
    private readonly ChatBadgeCatalog _badges;
    private readonly IModerationApi _moderation;
    private readonly ChatEmotes _emotes;
    private readonly Dictionary<string, BitmapImage> _images = new();
    private readonly HashSet<string> _shown = new();
    private readonly Queue<ChatMessage> _pending = new();
    private readonly List<ChatLine> _lines = new();
    private readonly DispatcherTimer _idle = new() { Interval = IdleExit };

    private readonly DispatcherTimer _front = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _draining;
    private bool _menuOpen;
    private double _actionsGutter;
    private IntPtr _handle;
    private bool _positioning;
    private bool _interactive;
    private bool _fadingOut;

    public ChatOverlayWindow(SettingsStore settings, ChatBadgeCatalog badges, IModerationApi moderation, ChatEmotes emotes)
    {
        InitializeComponent();
        _settings = settings;
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
        _positioning = on;
        ApplyAppearance();
        PositionLabel.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Grip.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        UpdateFrame();
        UpdateInputMode();
    }

    public void SetInteractive(bool on)
    {
        if (_interactive == on) return;
        _interactive = on;
        UpdateFrame();
        UpdateInputMode();
        if (on)
        {
            foreach (var line in _lines)
            {
                if (line.Actions != null) line.Text.Margin = new Thickness(0, 0, ActionsGutter(line.Actions), 0);
            }
            _idle.Start();
            return;
        }

        _idle.Stop();
        foreach (var line in _lines)
        {
            if (line.Actions != null) line.Actions.Visibility = Visibility.Collapsed;
            line.Text.Margin = new Thickness(0);
            Disarm(line);
        }
        _ = DrainAsync();
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
        var size = ChatPlacement.ClampFontSize(settings.ChatFontSize);
        foreach (var line in _lines)
        {
            line.Text.FontSize = size;
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
        if (message.MessageId.Length > 0)
        {
            if (_shown.Count > 500) _shown.Clear();
            if (!_shown.Add(message.MessageId)) return;
        }
        _pending.Enqueue(message);
        if (!_draining) _ = DrainAsync();
    }

    private async Task DrainAsync()
    {
        if (_interactive || _draining) return;
        _draining = true;
        try
        {
            if (!_badges.IsLoaded)
            {
                var load = _badges.EnsureLoadedAsync();
                if (_badges.IsLoading) await Task.WhenAny(load, Task.Delay(BadgeWait));
            }
            while (_pending.Count > 0 && !_interactive)
            {
                await _emotes.PreloadAsync(_pending.Peek());
                if (_interactive) break;
                Render(_pending.Dequeue());
            }
        }
        finally
        {
            _draining = false;
        }
    }

    private void Render(ChatMessage message)
    {
        var size = ChatPlacement.ClampFontSize(_settings.Current.ChatFontSize);
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = size, Foreground = TextBrush };
        foreach (var badge in BadgeInlines(message, size)) text.Inlines.Add(badge);

        var name = NameOf(message);
        var (r, g, b) = ChatColors.Readable(message.Color, message.ChatterLogin.Length > 0 ? message.ChatterLogin : name);
        var nickBrush = Frozen(new SolidColorBrush(Color.FromRgb(r, g, b)));
        text.Inlines.Add(new Run(name) { FontWeight = FontWeights.Bold, Foreground = nickBrush });
        text.Inlines.Add(new Run(": "));
        foreach (var inline in _emotes.Inlines(message, size)) text.Inlines.Add(inline);

        var root = new Grid { Background = Brushes.Transparent };
        root.Children.Add(text);
        var box = new Border
        {
            Child = root,
            Background = PlateFor(message),
            BorderBrush = _cellLine,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(0, 0, 0, 2),
        };
        var line = new ChatLine(message, root, box, text, nickBrush);
        if (CanModerate(message)) AddActions(line);
        box.MouseRightButtonUp += (_, e) =>
        {
            if (!_interactive) return;
            e.Handled = true;
            ShowNickMenu(line);
        };

        Lines.Children.Add(box);
        _lines.Add(line);
        while (Lines.Children.Count > MaxLines)
        {
            Lines.Children.RemoveAt(0);
            _lines.RemoveAt(0);
        }
    }

    private void ShowNickMenu(ChatLine line)
    {
        var key = ChatFeed.KeyOf(line.Message);
        var current = HighlightOf(line.Message);
        var menu = new ContextMenu
        {
            PlacementTarget = line.Text,
            Placement = PlacementMode.MousePoint,
        };

        var history = new MenuItem { Header = "История" };
        System.Windows.Automation.AutomationProperties.SetName(history, "История");
        history.Click += (_, _) => HistoryRequested?.Invoke(line.Message);
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;
        menu.Items.Add(history);

        var label = new MenuItem { Header = "Подсветить", IsEnabled = false };
        System.Windows.Automation.AutomationProperties.SetName(label, "Подсветить");
        menu.Items.Add(label);

        var flat = (Style)Application.Current.Resources["FlatButton"];
        var strip = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var swatch in ChatHighlights.Palette)
        {
            var (r, g, b) = ChatHighlights.Parse(swatch.Hex);
            var square = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(Color.FromRgb(r, g, b)),
                BorderBrush = string.Equals(current, swatch.Hex, StringComparison.OrdinalIgnoreCase) ? Brushes.White : Brushes.Transparent,
                BorderThickness = new Thickness(2),
            };
            var button = new Button { Style = flat, Content = square, Padding = new Thickness(3), ToolTip = swatch.Name };
            System.Windows.Automation.AutomationProperties.SetName(button, swatch.Name);
            var hex = swatch.Hex;
            button.Click += (_, _) =>
            {
                menu.IsOpen = false;
                SetHighlight(key, hex);
            };
            strip.Children.Add(button);
        }
        var more = new Button { Style = flat, Content = PaletteIcon(), Padding = new Thickness(3), ToolTip = "Другой цвет" };
        System.Windows.Automation.AutomationProperties.SetName(more, "Другой цвет");
        more.Click += (_, _) =>
        {
            menu.IsOpen = false;
            Dispatcher.InvokeAsync(() => ChooseCustomColor(line));
        };
        strip.Children.Add(more);

        var swatches = new MenuItem { Header = strip };
        menu.Items.Add(swatches);

        if (current != null)
        {
            var remove = new MenuItem { Header = "Убрать подсветку" };
            System.Windows.Automation.AutomationProperties.SetName(remove, "Убрать подсветку");
            remove.Click += (_, _) => SetHighlight(key, null);
            menu.Items.Add(remove);
        }
        menu.IsOpen = true;
    }

    private static FrameworkElement PaletteIcon()
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M8,1.6 C4.2,1.6 1.6,4.4 1.6,8 C1.6,11.6 4.4,14.4 8,14.4 C9.2,14.4 9.6,13.5 9,12.7 C8.4,11.9 9,11 10.1,11 H12.2 C13.5,11 14.4,10.1 14.4,8.8 C14.4,4.6 11.6,1.6 8,1.6 Z"),
            Stroke = TextBrush,
            StrokeThickness = 1.3,
            StrokeLineJoin = PenLineJoin.Round,
        });
        void Spot(double x, double y, Color color)
        {
            var spot = new Ellipse { Width = 2.6, Height = 2.6, Fill = new SolidColorBrush(color) };
            Canvas.SetLeft(spot, x - 1.3);
            Canvas.SetTop(spot, y - 1.3);
            canvas.Children.Add(spot);
        }
        Spot(4.9, 6.4, Color.FromRgb(0xFF, 0x45, 0x3A));
        Spot(8.0, 4.5, Color.FromRgb(0xFF, 0xD6, 0x0A));
        Spot(11.1, 6.4, Color.FromRgb(0x30, 0xD1, 0x58));
        Spot(4.9, 9.7, Color.FromRgb(0x0A, 0x84, 0xFF));
        return new Viewbox { Width = 18, Height = 18, Child = canvas };
    }

    private string? HighlightOf(ChatMessage message) =>
        _settings.Current.ChatHighlights.TryGetValue(ChatFeed.KeyOf(message), out var hex) ? ChatHighlights.Normalize(hex) : null;

    private double MarkOpacity() => ChatPlacement.ClampCellOpacity(_settings.Current.ChatCellOpacity);

    private Brush PlateFor(ChatMessage message)
    {
        var hex = HighlightOf(message);
        if (hex == null) return _cellBack;
        if (!_markBrushes.TryGetValue(hex, out var brush))
        {
            var (r, g, b) = ChatHighlights.Parse(hex);
            brush = new SolidColorBrush(Color.FromRgb(r, g, b));
            _markBrushes[hex] = brush;
        }
        brush.Opacity = MarkOpacity();
        return brush;
    }

    private void SetHighlight(string key, string? hex)
    {
        var marks = _settings.Current.ChatHighlights;
        if (hex == null) marks.Remove(key);
        else marks[key] = hex;
        _settings.Save();
        foreach (var line in _lines)
        {
            if (ChatFeed.KeyOf(line.Message) == key) line.Box.Background = PlateFor(line.Message);
        }
    }

    private void ChooseCustomColor(ChatLine line)
    {
        _idle.Stop();
        var previous = NativeMethods.GetForegroundWindow();
        string? hex;
        try
        {
            hex = HighlightColorDialog.Ask(this, NameOf(line.Message), line.NickBrush, HighlightOf(line.Message));
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != _handle && NativeMethods.Exists(previous)) NativeMethods.SetForegroundWindow(previous);
            RestartIdle();
        }
        if (hex != null) SetHighlight(ChatFeed.KeyOf(line.Message), hex);
    }
    private bool CanModerate(ChatMessage message) =>
        message.ChatterId.Length > 0 && message.ChatterId != _settings.Current.TwitchUserId;

    private const string MuteGlyph = "M2,6 H4.8 L8.6,2.8 V13.2 L4.8,10 H2 Z M11,5.8 L14.2,10.2 M14.2,5.8 L11,10.2";
    private const string ClockGlyph = "M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 M8,5 V8.2 L10.4,9.8";
    private const string BanGlyph = "M8,2 A6,6 0 1 1 8,14 A6,6 0 1 1 8,2 M3.8,3.8 L12.2,12.2";
    private const string UndoGlyph = "M5,4 L2.5,6.5 L5,9 M2.8,6.5 H9 A3.5,3.5 0 0 1 9,13.5 H6";
    private const string CheckGlyph = "M3,8.5 L6.4,12 L13,4.5";

    private enum Armed
    {
        None,
        Mute,
        Ban,
    }

    private sealed class IconButton
    {
        private readonly Path _glyph;
        private readonly TextBlock _label;
        private readonly string _idleGlyph;
        private readonly Brush _idleStroke;
        private string _name;

        public IconButton(string glyph, Brush stroke, Style style, string name)
        {
            _idleGlyph = glyph;
            _idleStroke = stroke;
            _name = name;
            _glyph = new Path
            {
                Data = Geometry.Parse(glyph),
                Stroke = stroke,
                StrokeThickness = 1.6,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 16,
                Height = 16,
                Stretch = Stretch.None,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _label = new TextBlock
            {
                Text = "Точно?",
                FontSize = 12,
                Margin = new Thickness(4, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed,
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(_glyph);
            content.Children.Add(_label);
            Button = new Button { Content = content, Style = style, ToolTip = name };
            System.Windows.Automation.AutomationProperties.SetName(Button, name);
        }

        public Button Button { get; }

        public void Rename(string name)
        {
            _name = name;
            Button.ToolTip = name;
            System.Windows.Automation.AutomationProperties.SetName(Button, name);
        }

        public void ShowConfirm(Brush brush)
        {
            _glyph.Data = Geometry.Parse(CheckGlyph);
            _glyph.Stroke = brush;
            _label.Foreground = brush;
            _label.Visibility = Visibility.Visible;
            System.Windows.Automation.AutomationProperties.SetName(Button, "Точно? " + _name);
        }

        public void ShowIdle()
        {
            _glyph.Data = Geometry.Parse(_idleGlyph);
            _glyph.Stroke = _idleStroke;
            _label.Visibility = Visibility.Collapsed;
            System.Windows.Automation.AutomationProperties.SetName(Button, _name);
        }
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
        public Brush NickBrush { get; }
        public Border? Actions { get; set; }
        public IconButton? Mute { get; set; }
        public IconButton? Custom { get; set; }
        public IconButton? Ban { get; set; }
        public IconButton? Undo { get; set; }
        public Run? Status { get; set; }
        public DispatcherTimer? DisarmTimer { get; set; }
        public Armed Armed { get; set; }
    }

    private string MuteName() => "Мут на " + ChatPlacement.DescribeDuration(ChatPlacement.ClampMuteMinutes(_settings.Current.ChatMuteMinutes) * 60);

    private static string NameOf(ChatMessage message) => message.ChatterName.Length > 0 ? message.ChatterName : message.ChatterLogin;

    private void AddActions(ChatLine line)
    {
        var style = (Style)FindResource("ChatAction");
        var mute = new IconButton(MuteGlyph, TextBrush, style, MuteName());
        var custom = new IconButton(ClockGlyph, TextBrush, style, "Мут на своё время");
        var ban = new IconButton(BanGlyph, ErrorBrush, style, "Бан");
        var undo = new IconButton(UndoGlyph, TextBrush, style, "Снять");
        undo.Button.Visibility = Visibility.Collapsed;
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var button in new[] { mute, custom, ban, undo }) buttons.Children.Add(button.Button);
        var panel = new Border
        {
            Child = buttons,
            Background = ActionsBack,
            BorderBrush = ActionsLine,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Visibility = Visibility.Collapsed,
        };
        line.Actions = panel;
        line.Mute = mute;
        line.Custom = custom;
        line.Ban = ban;
        line.Undo = undo;

        mute.Button.Click += async (_, _) =>
        {
            if (line.Armed != Armed.Mute)
            {
                Arm(line, Armed.Mute);
                return;
            }
            var seconds = ChatPlacement.ClampMuteMinutes(_settings.Current.ChatMuteMinutes) * 60;
            await ModerateAsync(line, seconds, "замучен на " + ChatPlacement.DescribeDuration(seconds));
        };
        custom.Button.Click += async (_, _) => await MuteForChosenTimeAsync(line);
        ban.Button.Click += async (_, _) =>
        {
            if (line.Armed != Armed.Ban)
            {
                Arm(line, Armed.Ban);
                return;
            }
            await ModerateAsync(line, null, "забанен");
        };
        undo.Button.Click += async (_, _) => await LiftAsync(line);

        line.Root.Children.Add(panel);
        line.Box.MouseEnter += (_, _) =>
        {
            if (_interactive) panel.Visibility = Visibility.Visible;
        };
        line.Box.MouseLeave += (_, _) =>
        {
            panel.Visibility = Visibility.Collapsed;
            Disarm(line);
        };
    }

    private async Task MuteForChosenTimeAsync(ChatLine line)
    {
        Disarm(line);
        _idle.Stop();
        var previous = NativeMethods.GetForegroundWindow();
        int? seconds;
        try
        {
            seconds = MuteDialog.Ask(this, NameOf(line.Message), line.NickBrush, ChatPlacement.ClampMuteMinutes(_settings.Current.ChatMuteMinutes));
        }
        finally
        {
            if (previous != IntPtr.Zero && previous != _handle && NativeMethods.Exists(previous)) NativeMethods.SetForegroundWindow(previous);
            RestartIdle();
        }
        if (seconds is { } chosen) await ModerateAsync(line, chosen, "замучен на " + ChatPlacement.DescribeDuration(chosen));
    }

    private void Arm(ChatLine line, Armed kind)
    {
        Disarm(line);
        line.Armed = kind;
        if (kind == Armed.Mute) line.Mute?.ShowConfirm(DoneBrush);
        else line.Ban?.ShowConfirm(ErrorBrush);
        line.DisarmTimer ??= new DispatcherTimer { Interval = BanConfirmWindow };
        line.DisarmTimer.Tick -= OnDisarmTick;
        line.DisarmTimer.Tick += OnDisarmTick;
        line.DisarmTimer.Tag = line;
        line.DisarmTimer.Stop();
        line.DisarmTimer.Start();
    }

    private static void OnDisarmTick(object? sender, EventArgs e)
    {
        if (sender is DispatcherTimer { Tag: ChatLine line }) Disarm(line);
    }

    private static void Disarm(ChatLine line)
    {
        line.DisarmTimer?.Stop();
        if (line.Armed == Armed.None) return;
        line.Armed = Armed.None;
        line.Mute?.ShowIdle();
        line.Ban?.ShowIdle();
    }

    private async Task ModerateAsync(ChatLine line, int? seconds, string doneText)
    {
        var chatter = line.Message.ChatterId;
        SetBusy(chatter, true);
        ModerationResult result;
        try
        {
            result = await _moderation.BanAsync(chatter, seconds, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write("Moderation failed: " + ex.Message);
            result = new ModerationResult(false, "Ошибка: " + ex.Message);
        }
        SetBusy(chatter, false);
        if (result.Success) MarkModerated(chatter, doneText);
        else ShowStatus(line, result.Message, ErrorBrush);
        Disarm(line);
        RestartIdle();
    }

    private async Task LiftAsync(ChatLine line)
    {
        var chatter = line.Message.ChatterId;
        SetBusy(chatter, true);
        ModerationResult result;
        try
        {
            result = await _moderation.UnbanAsync(chatter, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Write("Moderation failed: " + ex.Message);
            result = new ModerationResult(false, "Ошибка: " + ex.Message);
        }
        SetBusy(chatter, false);
        if (result.Success) MarkLifted(chatter);
        else ShowStatus(line, result.Message, ErrorBrush);
        RestartIdle();
    }

    private IEnumerable<ChatLine> LinesOf(string chatterId) => _lines.Where(l => l.Message.ChatterId == chatterId);

    private void SetBusy(string chatterId, bool busy)
    {
        foreach (var line in LinesOf(chatterId))
        {
            foreach (var button in new[] { line.Mute, line.Custom, line.Ban, line.Undo })
            {
                if (button != null) button.Button.IsEnabled = !busy;
            }
        }
    }

    private void MarkModerated(string chatterId, string text)
    {
        foreach (var line in LinesOf(chatterId))
        {
            ShowStatus(line, text, DoneBrush);
            SetVisible(line.Mute, false);
            SetVisible(line.Custom, false);
            SetVisible(line.Ban, false);
            SetVisible(line.Undo, true);
        }
    }

    private void MarkLifted(string chatterId)
    {
        foreach (var line in LinesOf(chatterId))
        {
            ClearStatus(line);
            SetVisible(line.Mute, true);
            SetVisible(line.Custom, true);
            SetVisible(line.Ban, true);
            SetVisible(line.Undo, false);
        }
    }

    private static void SetVisible(IconButton? button, bool visible)
    {
        if (button != null) button.Button.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void ShowStatus(ChatLine line, string text, Brush brush)
    {
        ClearStatus(line);
        line.Status = new Run("  · " + text) { Foreground = brush, FontSize = line.Text.FontSize * 0.9 };
        line.Text.Inlines.Add(line.Status);
    }

    private static void ClearStatus(ChatLine line)
    {
        if (line.Status == null) return;
        line.Text.Inlines.Remove(line.Status);
        line.Status = null;
    }

    private List<Inline> BadgeInlines(ChatMessage message, double fontSize)
    {
        var inlines = new List<Inline>();
        if (message.Badges == null) return inlines;
        foreach (var badge in message.Badges)
        {
            var url = _badges.UrlFor(badge.SetId, badge.Id);
            if (url == null) continue;
            var image = new Image { Source = ImageFor(url), Width = BadgeSize(fontSize), Height = BadgeSize(fontSize), Margin = new Thickness(0, 0, 4, 0) };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
        }
        return inlines;
    }

    private void OnBadgesLoaded() => Dispatcher.InvokeAsync(AddMissingBadges);

    private void AddMissingBadges()
    {
        foreach (var line in _lines)
        {
            if (line.Message.Badges is not { Count: > 0 }) continue;
            if (line.Text.Inlines.OfType<InlineUIContainer>().Any(i => i.Child is Image { Tag: not "emote" })) continue;
            var first = line.Text.Inlines.FirstInline;
            foreach (var badge in BadgeInlines(line.Message, line.Text.FontSize)) line.Text.Inlines.InsertBefore(first, badge);
        }
    }

    private static double BadgeSize(double fontSize) => Math.Round(fontSize * 1.2);

    private BitmapImage ImageFor(string url)
    {
        if (_images.TryGetValue(url, out var cached)) return cached;
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(url);
        image.CacheOption = BitmapCacheOption.OnDemand;
        image.EndInit();
        _images[url] = image;
        return image;
    }

    private void OnGripDrag(object sender, DragDeltaEventArgs e)
    {
        Width = Math.Max(ChatPlacement.MinWidth, Width + e.HorizontalChange);
        Height = Math.Max(ChatPlacement.MinHeight, Height + e.VerticalChange);
    }

    private void OnGripCompleted(object sender, DragCompletedEventArgs e) => GeometryChanged?.Invoke();

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
