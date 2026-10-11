using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using StreamHelper.Storage;
using static StreamHelper.Ui.Freezables;

namespace StreamHelper.Ui;

public partial class EmotePickerWindow : Window
{
    private const int ShownLimit = 600;
    private const double TileSize = 28;

    private static readonly Brush ShelfBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0xA3, 0xB2)));
    private static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush ActiveBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x9B, 0x8C, 0xFF)));
    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xB9, 0xA3, 0xFF)));
    private static readonly Brush TwitchBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xA9, 0x70, 0xFF)));
    private static readonly Brush SevenBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4F, 0xC3, 0xF7)));
    private static readonly Brush BttvBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x5C, 0x6C)));
    private static readonly Brush FfzBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xC9, 0xCF, 0xDA)));
    private static readonly Brush FaceBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x4B)));
    private const string TwitchGlyph = "M11.571 4.714h1.715v5.143H11.57zm4.715 0H18v5.143h-1.714zM6 0L1.714 4.286v15.428h5.143V24l4.286-4.286h3.428L22.286 12V0zm14.571 11.143l-3.428 3.428h-3.429l-3 3v-3H6.857V1.714h13.714Z";
    private static ControlTemplate? _tileTemplate;

    private readonly ChatEmotes _emotes;
    private readonly SettingsStore _settings;
    private readonly Action<string> _pick;
    private readonly EmoteAnimator _animator = new(ShownLimit);
    private readonly SemaphoreSlim _loads = new(6);
    private int _render;
    private readonly System.Collections.Generic.List<(FrameworkElement Header, Button Mark)> _marks = new();
    private Button? _active;
    private Button? _clicked;
    private double _clickedOffset;
    private static ControlTemplate? _sideTemplate;

    public EmotePickerWindow(ChatEmotes emotes, SettingsStore settings, Action<string> pick)
    {
        InitializeComponent();
        _emotes = emotes;
        _settings = settings;
        _pick = pick;
        Closed += (_, _) =>
        {
            _animator.Stop();
            _animator.Clear();
        };
    }

    public event Action? Escaped;

    public IntPtr Handle { get; private set; }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Handle = new WindowInteropHelper(this).Handle;
        NativeMethods.AddExStyle(Handle, NativeMethods.WsExToolWindow);
    }

    public async Task ShowNearAsync(Window chat)
    {
        PlaceNear(chat);
        EmoteSearch.Text = "";
        if (!IsVisible) Show();
        Activate();
        EmoteSearch.Focus();
        await ShowEmotesAsync();
    }

    public void HidePicker()
    {
        if (!IsVisible) return;
        _render++;
        _animator.Stop();
        _animator.Clear();
        Shelves.Children.Clear();
        Sidebar.Children.Clear();
        _marks.Clear();
        _active = null;
        _clicked = null;
        Hide();
    }

    private void PlaceNear(Window chat)
    {
        var handle = new WindowInteropHelper(chat).Handle;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var dpi = VisualTreeHelper.GetDpi(chat);
        var bounds = new Rect(area.Left / dpi.DpiScaleX, area.Top / dpi.DpiScaleY, area.Width / dpi.DpiScaleX, area.Height / dpi.DpiScaleY);
        var spot = EmotePickerPlacement.Place(new Rect(chat.Left, chat.Top, chat.ActualWidth, chat.ActualHeight), bounds, new Size(Width, Height));
        Left = spot.X;
        Top = spot.Y;
    }

    private async Task ShowEmotesAsync()
    {
        if (_emotes.Library is not { } library)
        {
            ShowStatus("Смайлы недоступны.");
            return;
        }
        Render();
        if (library.All.Count == 0 || library.IsLoading) ShowStatus("Загружаю смайлы…");
        await library.EnsureLoadedAsync();
        if (IsVisible) Render();
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Render()
    {
        var render = ++_render;
        _animator.Clear();
        Shelves.Children.Clear();
        Sidebar.Children.Clear();
        _marks.Clear();
        _active = null;
        _clicked = null;
        SearchHint.Visibility = EmoteSearch.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_emotes.Library is not { } library) return;
        var shelves = EmoteChoices.Shelves(library.All, _settings.Current.ChatRecentEmotes, EmoteSearch.Text, library.Owners);
        SideScroll.Visibility = EmoteSearch.Text.Trim().Length == 0 && shelves.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        var shown = 0;
        foreach (var shelf in shelves)
        {
            if (shown >= ShownLimit) break;
            var header = new TextBlock { Text = shelf.Title, Foreground = ShelfBrush, FontSize = 12, Margin = new Thickness(4, 6, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis };
            Shelves.Children.Add(header);
            var mark = SideButton(shelf, header, render);
            Sidebar.Children.Add(mark);
            _marks.Add((header, mark));
            var wrap = new WrapPanel();
            foreach (var emote in shelf.Emotes)
            {
                if (shown >= ShownLimit) break;
                wrap.Children.Add(Tile(emote, render));
                shown++;
            }
            Shelves.Children.Add(wrap);
        }
        var total = shelves.Sum(s => s.Emotes.Count);
        ShowStatus(library.All.Count == 0
            ? library.IsLoading ? "Загружаю смайлы…" : "Смайлов нет: подключи Twitch во вкладке «Twitch»."
            : total == 0 ? "Ничего не нашлось."
            : total > shown ? $"Показаны первые {shown}: уточни поиск."
            : !_settings.Current.HasEmotesScope ? "Свои смайлы с других каналов появятся после переподключения Twitch."
            : "");
        EmoteScroll.ScrollToTop();
        SideScroll.ScrollToTop();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, new Action(HighlightSection));
    }

    private Button SideButton(EmoteShelf shelf, FrameworkElement header, int render)
    {
        var mark = new Button
        {
            Width = 32,
            Height = 32,
            Margin = new Thickness(2, 0, 2, 4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = shelf.Title,
            Content = SideIcon(shelf, render),
            Template = SideTemplate(),
        };
        AutomationProperties.SetName(mark, "Раздел: " + shelf.Title);
        mark.Click += (_, _) =>
        {
            var y = header.TranslatePoint(new Point(0, 0), Shelves).Y;
            _clicked = mark;
            _clickedOffset = Math.Clamp(y - 2, 0, EmoteScroll.ScrollableHeight);
            if (Math.Abs(EmoteScroll.VerticalOffset - _clickedOffset) < 1) HighlightSection();
            else EmoteScroll.ScrollToVerticalOffset(_clickedOffset);
        };
        mark.MouseEnter += (_, _) => { if (!ReferenceEquals(mark, _active)) mark.Background = HoverBrush; };
        mark.MouseLeave += (_, _) => { if (!ReferenceEquals(mark, _active)) mark.Background = Brushes.Transparent; };
        return mark;
    }

    private FrameworkElement SideIcon(EmoteShelf shelf, int render)
    {
        switch (shelf.Key)
        {
            case "recent":
                var clock = new Canvas { Width = 20, Height = 20 };
                clock.Children.Add(new System.Windows.Shapes.Ellipse { Width = 18, Height = 18, Stroke = AccentBrush, StrokeThickness = 1.6 });
                Canvas.SetLeft(clock.Children[0], 1);
                Canvas.SetTop(clock.Children[0], 1);
                clock.Children.Add(new System.Windows.Shapes.Path { Data = Geometry.Parse("M10,5 V10.5 L13.5,12.5"), Stroke = AccentBrush, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
                return Centered(clock);
            case "twitch":
                return Centered(new Viewbox { Width = 18, Height = 18, Child = new Canvas { Width = 24, Height = 24, Children = { new System.Windows.Shapes.Path { Data = Geometry.Parse(TwitchGlyph), Fill = TwitchBrush } } } });
            case "7tv":
                return Centered(new TextBlock { Text = "7TV", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = SevenBrush });
            case "bttv":
                return Centered(new TextBlock { Text = "BTTV", FontSize = 8.5, FontWeight = FontWeights.Bold, Foreground = BttvBrush });
            case "ffz":
                return Centered(new TextBlock { Text = "FFZ", FontSize = 10, FontWeight = FontWeights.Bold, Foreground = FfzBrush });
            default:
                var avatar = new ChannelAvatar(shelf.Title.Replace("Канал · ", ""), 26, 12, FaceBrush);
                if (!string.IsNullOrEmpty(shelf.IconUrl)) _ = LoadFaceAsync(avatar, shelf.IconUrl, render);
                return avatar.Root;
        }
    }

    private static FrameworkElement Centered(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Center;
        element.VerticalAlignment = VerticalAlignment.Center;
        return element;
    }

    private async Task LoadFaceAsync(ChannelAvatar avatar, string url, int render)
    {
        await _loads.WaitAsync();
        try
        {
            if (render != _render) return;
            var frames = await _emotes.ImageAsync(url);
            if (frames != null && render == _render) avatar.Show(frames.Frames[0]);
        }
        catch (Exception ex)
        {
            Log.Write("Channel picture failed: " + ex.Message);
        }
        finally
        {
            _loads.Release();
        }
    }

    private void OnEmoteScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_clicked != null && e.VerticalChange != 0 && Math.Abs(EmoteScroll.VerticalOffset - _clickedOffset) >= 1) _clicked = null;
        HighlightSection();
    }

    private void HighlightSection()
    {
        if (_marks.Count == 0) return;
        var current = _marks[0].Mark;
        foreach (var (header, mark) in _marks)
        {
            if (!header.IsLoaded) continue;
            if (header.TranslatePoint(new Point(0, 0), Shelves).Y <= EmoteScroll.VerticalOffset + 4) current = mark;
        }
        if (EmoteScroll.ScrollableHeight > 0 && EmoteScroll.VerticalOffset >= EmoteScroll.ScrollableHeight - 1) current = _marks[^1].Mark;
        if (_clicked != null) current = _clicked;
        if (ReferenceEquals(current, _active)) return;
        if (_active != null) _active.Background = Brushes.Transparent;
        _active = current;
        _active.Background = ActiveBrush;
        _active.BringIntoView();
    }

    private Button Tile(EmoteChoice emote, int render)
    {
        var image = new Image { Width = TileSize, Height = TileSize, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var cached = _emotes.CachedImage(emote.Url);
        if (cached != null) Display(image, cached);
        else _ = LoadAsync(image, emote.Url, render);
        var button = new Button
        {
            Content = image,
            Focusable = false,
            Cursor = Cursors.Hand,
            ToolTip = emote.Name,
            Margin = new Thickness(1),
            Template = TileTemplate(),
        };
        AutomationProperties.SetName(button, emote.Name);
        button.Click += (_, _) => _pick(emote.Name);
        return button;
    }

    private void Display(Image image, EmoteFrames frames)
    {
        image.Source = frames.Frames[0];
        _animator.Add(image, frames);
    }

    private async Task LoadAsync(Image image, string url, int render)
    {
        await _loads.WaitAsync();
        try
        {
            if (render != _render) return;
            var frames = await _emotes.ImageAsync(url);
            if (frames != null && render == _render) Display(image, frames);
        }
        catch (Exception ex)
        {
            Log.Write("Emote picture failed: " + ex.Message);
        }
        finally
        {
            _loads.Release();
        }
    }

    private static ControlTemplate SideTemplate()
    {
        if (_sideTemplate != null) return _sideTemplate;
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        template.Seal();
        _sideTemplate = template;
        return template;
    }

    private static ControlTemplate TileTemplate()
    {
        if (_tileTemplate != null) return _tileTemplate;
        var border = new FrameworkElementFactory(typeof(Border), "bd");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(5));
        border.SetValue(Border.PaddingProperty, new Thickness(3));
        border.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, HoverBrush, "bd"));
        template.Triggers.Add(hover);
        template.Seal();
        _tileTemplate = template;
        return template;
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (IsVisible) Render();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Escaped?.Invoke();
            return;
        }
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        var first = _emotes.Library is { } library
            ? EmoteChoices.Shelves(library.All, _settings.Current.ChatRecentEmotes, EmoteSearch.Text).SelectMany(s => s.Emotes).FirstOrDefault()
            : null;
        if (first != null) _pick(first.Name);
        Escaped?.Invoke();
    }

}
