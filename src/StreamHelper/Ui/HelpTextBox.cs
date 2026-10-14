using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Navigation;

namespace StreamHelper.Ui;

public sealed class HelpTextBox : RichTextBox
{
    public static readonly DependencyProperty ParagraphsProperty = DependencyProperty.Register(
        nameof(Paragraphs), typeof(IReadOnlyList<string>), typeof(HelpTextBox), new PropertyMetadata(null, (d, _) => ((HelpTextBox)d).Build()));

    private static readonly FontFamily CodeFont = new("Consolas");
    private static readonly Brush CodeBack = Freezables.Solid(Color.FromRgb(0x2A, 0x2E, 0x37));

    public HelpTextBox()
    {
        AddHandler(Hyperlink.RequestNavigateEvent, new RequestNavigateEventHandler(OnNavigate));
        DataObject.AddCopyingHandler(this, OnCopying);
    }

    public IReadOnlyList<string>? Paragraphs
    {
        get => (IReadOnlyList<string>?)GetValue(ParagraphsProperty);
        set => SetValue(ParagraphsProperty, value);
    }

    private void Build()
    {
        var document = new FlowDocument { PagePadding = new Thickness(0) };
        foreach (var text in Paragraphs ?? Array.Empty<string>())
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6), LineHeight = 19 };
            foreach (var span in HelpText.Parse(text)) paragraph.Inlines.Add(InlineFor(span));
            document.Blocks.Add(paragraph);
        }
        Document = document;
    }

    private static Inline InlineFor(HelpSpan span) => span.Kind switch
    {
        HelpSpanKind.Link => new Hyperlink(new Run(span.Text)) { NavigateUri = new Uri(span.Url), ToolTip = span.Url },
        HelpSpanKind.Code => new Run(HelpText.ForDisplay(span.Text)) { FontFamily = CodeFont, Background = CodeBack },
        _ => new Run(span.Text),
    };

    private static void OnNavigate(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        BrowserLauncher.Open(e.Uri.AbsoluteUri);
    }

    private static void OnCopying(object sender, DataObjectCopyingEventArgs e)
    {
        if (sender is not HelpTextBox box) return;
        var text = HelpText.ForCopy(box.Selection.Text);
        if (e.IsDragDrop)
        {
            e.DataObject.SetData(DataFormats.UnicodeText, text);
            return;
        }
        e.CancelCommand();
        ClipboardText.TrySet(text);
    }
}
