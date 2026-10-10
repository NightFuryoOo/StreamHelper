using System;
using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using StreamHelper.Sync;

namespace StreamHelper.Ui;

public partial class ReleaseNotesWindow : Window
{
    private static ReleaseNotesWindow? _open;

    private readonly UpdateService _updates;
    private readonly UpdateRelease _release;
    private readonly bool _offerUpdate;

    private ReleaseNotesWindow(UpdateService updates, UpdateRelease release, bool offerUpdate)
    {
        InitializeComponent();
        _updates = updates;
        _release = release;
        _offerUpdate = offerUpdate;
        var version = release.Version.ToString(4);
        Title = "Обновление " + version;
        HeadingText.Text = "StreamHelper " + version;
        DateText.Text = release.PublishedUtc is { } at
            ? "Вышла " + DateTime.SpecifyKind(at, DateTimeKind.Utc).ToLocalTime().ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("ru-RU"))
            : "";
        DateText.Visibility = DateText.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        PageButton.Visibility = release.PageUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Visibility = offerUpdate && AppUpdate.IsNewer(release, updates.Current) && release.CanInstall ? Visibility.Visible : Visibility.Collapsed;
        NotesBox.Document = Build(release.Body.Length > 0 ? release.Body : release.Notes);
        Closed += (_, _) =>
        {
            if (ReferenceEquals(_open, this)) _open = null;
        };
    }

    public static void ShowFor(Window? owner, UpdateService updates, UpdateRelease? chosen = null, bool offerUpdate = true)
    {
        if ((chosen ?? updates.Release) is not { } release) return;
        if (_open != null && _open._release.Version == release.Version && ReferenceEquals(_open.Owner, owner) && _open._offerUpdate == offerUpdate)
        {
            _open.Activate();
            return;
        }
        _open?.Close();
        var window = new ReleaseNotesWindow(updates, release, offerUpdate);
        if (owner is { IsVisible: true }) window.Owner = owner;
        else window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        _open = window;
        window.Show();
        window.Activate();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.UseDarkTitleBar(new System.Windows.Interop.WindowInteropHelper(this).Handle, 0x231D1B, 0xEEE8E6, 0x4B3F3A);
    }

    private static FlowDocument Build(string body)
    {
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = new System.Windows.Media.FontFamily("Segoe UI"), FontSize = 14 };
        foreach (var block in ReleaseNotesFormat.Parse(body))
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, block.GapBefore ? 10 : 2, 0, 0), LineHeight = 20 };
            switch (block.Kind)
            {
                case NotesBlockKind.Heading:
                    paragraph.FontSize = block.Level <= 1 ? 19 : block.Level == 2 ? 17 : 15;
                    paragraph.FontWeight = FontWeights.SemiBold;
                    paragraph.Margin = new Thickness(0, block.GapBefore || document.Blocks.Count > 0 ? 12 : 0, 0, 2);
                    break;
                case NotesBlockKind.Bullet:
                    paragraph.Margin = new Thickness(18, block.GapBefore ? 10 : 3, 0, 0);
                    paragraph.TextIndent = -14;
                    paragraph.Inlines.Add(new Run(block.Marker + " ") { Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("AccentBrush") });
                    break;
            }
            foreach (var span in block.Spans)
            {
                Inline run = new Run(span.Text);
                if (span.Bold) run = new Bold(run);
                paragraph.Inlines.Add(run);
            }
            document.Blocks.Add(paragraph);
        }
        return document;
    }

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        Close();
        await _updates.UpdateAsync();
    }

    private void OnOpenPage(object sender, RoutedEventArgs e) => BrowserLauncher.Open(_release.PageUrl);

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }
}
