using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using StreamHelper.Storage;

namespace StreamHelper.Ui;

public sealed class EmoteTextBox : RichTextBox
{
    private static readonly HashSet<ICommand> Blocked = new()
    {
        EditingCommands.ToggleBold, EditingCommands.ToggleItalic, EditingCommands.ToggleUnderline,
        EditingCommands.ToggleSubscript, EditingCommands.ToggleSuperscript,
        EditingCommands.IncreaseFontSize, EditingCommands.DecreaseFontSize,
        EditingCommands.AlignLeft, EditingCommands.AlignCenter, EditingCommands.AlignRight, EditingCommands.AlignJustify,
        EditingCommands.ToggleBullets, EditingCommands.ToggleNumbering,
        EditingCommands.IncreaseIndentation, EditingCommands.DecreaseIndentation,
        EditingCommands.EnterParagraphBreak, EditingCommands.EnterLineBreak,
        EditingCommands.TabForward, EditingCommands.TabBackward,
        ApplicationCommands.Undo, ApplicationCommands.Redo,
    };

    private readonly EmoteAnimator _animator = new(100);
    private bool _rebuilding;
    private int _render;

    public EmoteTextBox()
    {
        Document = new FlowDocument(NewLine()) { PagePadding = new Thickness(0), LineStackingStrategy = LineStackingStrategy.BlockLineHeight };
        ApplyLineHeight();
        AcceptsReturn = false;
        AcceptsTab = false;
        IsUndoEnabled = false;
        AllowDrop = false;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        CommandManager.AddPreviewExecutedHandler(this, OnPreviewExecuted);
        CommandManager.AddPreviewCanExecuteHandler(this, OnPreviewCanExecute);
        TextChanged += (_, _) => Normalize();
        Unloaded += (_, _) => _animator.Stop();
    }

    public int MaxLength { get; set; } = 500;

    public static double PictureHeight(double fontSize) => Math.Round(fontSize * 1.6);

    public static double LineHeightFor(double fontSize) => PictureHeight(fontSize) + 2;

    public Func<string, string?>? EmoteUrl { get; set; }

    public ChatEmotes? Images { get; set; }

    public string Text => Serialize(null);

    public int CaretIndex
    {
        get => OffsetOf(CaretPosition);
        set => CaretPosition = PointerAt(value);
    }

    public void Clear() => SetText("", 0);

    public void SetText(string text, int caret)
    {
        text = EmoteInput.Flatten(text);
        if (text.Length > MaxLength) text = text[..MaxLength];
        Rebuild(text, EmoteInput.Images(text, new HashSet<int>(), -1, IsEmote), caret);
    }

    public void Refresh() => Normalize();

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == FontSizeProperty) ApplyLineHeight();
    }

    private void ApplyLineHeight() => Document.LineHeight = LineHeightFor(FontSize);

    private bool IsEmote(string word) => EmoteUrl?.Invoke(word) != null;

    private static Paragraph NewLine() => new() { Margin = new Thickness(0) };

    private Paragraph Line => Document.Blocks.FirstBlock as Paragraph ?? NewLine();

    private void Normalize()
    {
        if (_rebuilding || !Selection.IsEmpty) return;
        var shown = new List<int>();
        var text = Serialize(shown);
        var caret = CaretIndex;
        var clean = EmoteInput.Flatten(text);
        if (clean.Length > MaxLength) clean = clean[..MaxLength];
        var spans = EmoteInput.Images(clean, shown.ToHashSet(), caret, IsEmote);
        if (clean == text && IsFlat() && spans.Select(s => s.Start).SequenceEqual(shown)) return;
        Rebuild(clean, spans, caret);
    }

    private bool IsFlat() =>
        Document.Blocks.Count == 1 && Document.Blocks.FirstBlock is Paragraph paragraph &&
        paragraph.Inlines.All(i => i is Run || i is InlineUIContainer { Tag: string });

    private void Rebuild(string text, IReadOnlyList<EmoteSpan> spans, int caret)
    {
        _rebuilding = true;
        BeginChange();
        try
        {
            _render++;
            _animator.Clear();
            var line = NewLine();
            var at = 0;
            foreach (var span in spans)
            {
                if (span.Start > at) line.Inlines.Add(new Run(text[at..span.Start]));
                line.Inlines.Add(Picture(text.Substring(span.Start, span.Length)));
                at = span.Start + span.Length;
            }
            if (at < text.Length) line.Inlines.Add(new Run(text[at..]));
            Document.Blocks.Clear();
            Document.Blocks.Add(line);
            CaretIndex = Math.Clamp(caret, 0, text.Length);
        }
        finally
        {
            EndChange();
            _rebuilding = false;
        }
    }

    private InlineUIContainer Picture(string name)
    {
        var height = PictureHeight(FontSize);
        var image = new Image { Height = height, Stretch = Stretch.Uniform, Margin = new Thickness(1, 0, 1, 0), Tag = ChatEmotes.ImageTag };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        AutomationProperties.SetName(image, name);
        var container = new InlineUIContainer(image) { Tag = name, BaselineAlignment = BaselineAlignment.Center };
        var url = EmoteUrl?.Invoke(name);
        var cached = url != null && Images != null ? Images.CachedImage(url) : null;
        if (cached != null) Show(image, cached);
        else if (url != null && Images != null)
        {
            image.Width = height;
            _ = LoadAsync(container, image, url, name, _render);
        }
        else container.Child = Word(name);
        return container;
    }

    private void Show(Image image, EmoteFrames frames)
    {
        image.ClearValue(WidthProperty);
        image.Source = frames.Frames[0];
        _animator.Add(image, frames);
    }

    private TextBlock Word(string name) => new() { Text = name, Foreground = Foreground, FontSize = FontSize };

    private async Task LoadAsync(InlineUIContainer container, Image image, string url, string name, int render)
    {
        EmoteFrames? frames = null;
        try
        {
            frames = await Images!.ImageAsync(url);
        }
        catch (Exception ex)
        {
            Log.Write("Emote picture failed: " + ex.Message);
        }
        if (render != _render) return;
        if (frames != null) Show(image, frames);
        else container.Child = Word(name);
    }

    private string Serialize(List<int>? shown)
    {
        var text = new StringBuilder();
        var first = true;
        foreach (var block in Document.Blocks)
        {
            if (block is not Paragraph paragraph) continue;
            if (!first) text.Append(' ');
            first = false;
            Append(paragraph.Inlines, text, shown);
        }
        return text.ToString();
    }

    private static void Append(InlineCollection inlines, StringBuilder text, List<int>? shown)
    {
        foreach (var inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    text.Append(run.Text);
                    break;
                case InlineUIContainer { Tag: string name }:
                    shown?.Add(text.Length);
                    text.Append(name);
                    break;
                case Span span:
                    Append(span.Inlines, text, shown);
                    break;
                case LineBreak:
                    text.Append(' ');
                    break;
            }
        }
    }

    private static int LengthOf(Inline inline) => inline switch
    {
        Run run => run.Text.Length,
        InlineUIContainer { Tag: string name } => name.Length,
        Span span => span.Inlines.Sum(LengthOf),
        LineBreak => 1,
        _ => 0,
    };

    private int OffsetOf(TextPointer position)
    {
        var offset = 0;
        var first = true;
        foreach (var block in Document.Blocks)
        {
            if (block is not Paragraph paragraph) continue;
            if (!first) offset++;
            first = false;
            if (position.CompareTo(paragraph.ContentStart) <= 0) return offset;
            foreach (var inline in paragraph.Inlines)
            {
                if (position.CompareTo(inline.ElementStart) <= 0) return offset;
                var length = LengthOf(inline);
                if (inline is Run run && position.CompareTo(run.ContentEnd) <= 0) return offset + Math.Clamp(run.ContentStart.GetOffsetToPosition(position), 0, length);
                if (position.CompareTo(inline.ElementEnd) < 0) return offset + length;
                offset += length;
            }
            if (position.CompareTo(paragraph.ContentEnd) <= 0) return offset;
        }
        return offset;
    }

    private TextPointer PointerAt(int offset)
    {
        var line = Line;
        offset = Math.Max(0, offset);
        foreach (var inline in line.Inlines)
        {
            var length = LengthOf(inline);
            if (inline is Run run && offset <= length) return run.ContentStart.GetPositionAtOffset(offset) ?? run.ContentEnd;
            if (offset == 0) return inline.ElementStart;
            if (offset < length) return inline.ElementEnd;
            offset -= length;
        }
        return line.ContentEnd;
    }

    private (int Start, int End) SelectionRange()
    {
        var a = OffsetOf(Selection.Start);
        var b = OffsetOf(Selection.End);
        return a <= b ? (a, b) : (b, a);
    }

    private IEnumerable<EmoteSpan> ShownSpans()
    {
        var shown = new List<int>();
        var text = Serialize(shown);
        foreach (var start in shown)
        {
            var end = start;
            while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
            yield return new EmoteSpan(start, end - start);
        }
    }

    private void Replace(int start, int end, string insert)
    {
        var (text, caret, shown) = EmoteInput.Splice(Text, ShownSpans().ToList(), start, end, insert, MaxLength);
        Rebuild(text, EmoteInput.Images(text, shown, caret, IsEmote), caret);
    }

    protected override void OnPreviewTextInput(TextCompositionEventArgs e)
    {
        if (e.Text.Length > 0)
        {
            var (start, end) = SelectionRange();
            if (Text.Length - (end - start) + e.Text.Length > MaxLength)
            {
                e.Handled = true;
                return;
            }
        }
        base.OnPreviewTextInput(e);
    }

    private void OnPreviewCanExecute(object sender, CanExecuteRoutedEventArgs e)
    {
        if (!Blocked.Contains(e.Command)) return;
        e.CanExecute = false;
        e.Handled = true;
    }

    private void OnPreviewExecuted(object sender, ExecutedRoutedEventArgs e)
    {
        if (Blocked.Contains(e.Command))
        {
            e.Handled = true;
            return;
        }
        if (e.Command == ApplicationCommands.Copy || e.Command == ApplicationCommands.Cut)
        {
            e.Handled = true;
            var (start, end) = SelectionRange();
            if (end <= start) return;
            if (!ClipboardText.TrySet(Text[start..end])) return;
            if (e.Command == ApplicationCommands.Cut && !IsReadOnly) Replace(start, end, "");
            return;
        }
        if (e.Command == ApplicationCommands.Paste)
        {
            e.Handled = true;
            if (IsReadOnly) return;
            string pasted;
            try
            {
                pasted = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
            {
                Log.Write("Pasting into the typed text failed: " + ex.Message);
                return;
            }
            if (pasted.Length == 0) return;
            var (start, end) = SelectionRange();
            Replace(start, end, pasted);
        }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new EmoteTextBoxPeer(this);

    private sealed class EmoteTextBoxPeer : RichTextBoxAutomationPeer, IValueProvider
    {
        private readonly EmoteTextBox _box;

        public EmoteTextBoxPeer(EmoteTextBox box) : base(box) => _box = box;

        public override object GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Value ? this : base.GetPattern(patternInterface);

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Edit;

        public bool IsReadOnly => _box.IsReadOnly;

        public string Value => _box.Text;

        public void SetValue(string value)
        {
            if (!_box.IsEnabled) throw new ElementNotEnabledException();
            value ??= "";
            _box.SetText(value, value.Length);
        }
    }
}
