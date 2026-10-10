using CommuteCast.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Documents;

namespace CommuteCast.Desktop;

public sealed class PreparationWindow : Window
{
    public PreparationWindow(PreparedText prepared, string source, Func<string, Task>? audition = null, Action? stopAudition = null, Func<string, int, Task>? auditionAt = null)
    {
        Title = "Review narration preparation · CommuteCast"; Width = 1000; Height = 740; MinWidth = 700; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["Canvas"]; Foreground = (Brush)Application.Current.Resources["Ink"];
        var panel = new DockPanel { Margin = new(24) };
        var heading = new TextBlock { Text = $"{source.Length:N0} source characters accounted for · {prepared.Spans.Count:N0} spans · {prepared.Spans.Count(s => s.Kind == "explicit exclusion")} explicitly excluded spans\nPreparation {prepared.Version}. Automated accounting does not prove exact spoken fidelity.", Margin = new(0, 0, 0, 18), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        if (prepared.ProfileReview is { } review)
        {
            var summary = new TextBlock { Text = $"{review.Profile.Version} · {review.Profile.Language} · {review.Profile.Numbers} · {review.Profile.Acronyms} · {review.Profile.Dates}\nDictionary {review.Profile.DictionaryVersion}, revision {review.DictionaryRevision[..Math.Min(12, review.DictionaryRevision.Length)]}. {review.Changes.Count:N0} changes / {review.Changes.Count(c => c.Warning)} warnings. Changes appear in execution order; dictionary cascades can cover the same source twice. Offsets use UTF-16 characters.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) };
            DockPanel.SetDock(summary, Dock.Top); panel.Children.Add(summary);
        }
        var close = new Button { Content = "Close review", Padding = new(20, 10, 20, 10), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0), IsCancel = true };
        DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        var tabs = new TabControl();
        var spoken = HighlightedText(ReviewHighlights.Spoken(prepared), "Prepared narration script");
        var spokenPanel = new DockPanel();
        var previewActions = new StackPanel { Margin = new(0, 0, 0, 12) };
        previewActions.Children.Add(new TextBlock { Text = "Highlighted passages changed during preparation. Select up to 900 characters here to hear the exact spoken text with the captured voice and pace. Preview uses the current installed speech model.", TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { Margin = new(0, 8, 0, 0) };
        var play = new Button { Content = "Play selected spoken text", Padding = new(12, 8, 12, 8), Margin = new(0, 0, 8, 0), IsEnabled = false };
        var stop = new Button { Content = "Stop review audition", Padding = new(12, 8, 12, 8), IsEnabled = audition is not null };
        var previewStatus = new TextBlock { Text = "Select a short passage in Spoken text.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 8, 0, 0) };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(previewStatus, System.Windows.Automation.AutomationLiveSetting.Polite);
        var generating = false; var previewStarted = false; var previewAttempt = 0; var closed = false;
        spoken.SelectionChanged += (_, _) =>
        {
            var text = SelectedSpokenText(spoken);
            play.IsEnabled = audition is not null && !generating && !string.IsNullOrWhiteSpace(text) && text.Length <= AuditionRequest.MaximumCharacters;
            if (!generating) previewStatus.Text = text.Length > AuditionRequest.MaximumCharacters ? "Select at most 900 characters; nothing will be shortened." : $"{text.Length:N0} characters selected.";
        };
        play.Click += async (_, _) =>
        {
            var text = SelectedSpokenText(spoken); var attempt = ++previewAttempt; generating = true; previewStarted = true; play.IsEnabled = false;
            previewStatus.Text = "Preparing review audition; it waits for current narration. Stop cancels it.";
            try { if (auditionAt is not null) await auditionAt(text, new TextRange(spoken.Document.ContentStart, spoken.Selection.Start).Text.Length); else await audition!(text); if (!closed && previewAttempt == attempt) previewStatus.Text = "Review audition request finished. Stop or close review to end playback."; }
            catch (Exception error) { if (!closed && previewAttempt == attempt) previewStatus.Text = Infrastructure.QueueCoordinator.FriendlyError(error); }
            finally { generating = false; var selected = SelectedSpokenText(spoken); play.IsEnabled = !closed && audition is not null && !string.IsNullOrWhiteSpace(selected) && selected.Length <= AuditionRequest.MaximumCharacters; }
        };
        stop.Click += (_, _) => { ++previewAttempt; stopAudition?.Invoke(); previewStatus.Text = "Review audition stopped; cancellation requested for pending speech."; };
        Closed += (_, _) => { closed = true; ++previewAttempt; if (previewStarted) stopAudition?.Invoke(); };
        buttons.Children.Add(play); buttons.Children.Add(stop); previewActions.Children.Add(buttons); previewActions.Children.Add(previewStatus);
        DockPanel.SetDock(previewActions, Dock.Top); spokenPanel.Children.Add(previewActions); spokenPanel.Children.Add(spoken);
        tabs.Items.Add(new TabItem { Header = "Spoken text", Content = spokenPanel });
        var originalPanel = new DockPanel();
        var legend = new TextBlock { Text = "Orange highlights: explicitly excluded source. Yellow highlights: pronunciation replacements. Original text remains unchanged and copyable.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 12) };
        DockPanel.SetDock(legend, Dock.Top); originalPanel.Children.Add(legend); originalPanel.Children.Add(HighlightedText(ReviewHighlights.Original(prepared, source), "Original submitted source with preparation highlights"));
        tabs.Items.Add(new TabItem { Header = "Original source", Content = originalPanel });
        if (prepared.ProfileReview is { } pronunciation)
        {
            var view = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, ItemsSource = pronunciation.Changes, CanUserAddRows = false, EnableRowVirtualization = true };
            foreach (var field in new[] { "SourceStart", "SourceLength", "Original", "Before", "After", "Rule", "Warning" })
                view.Columns.Add(new DataGridTextColumn { Header = field, Binding = new Binding(field), Width = field is "SourceStart" or "SourceLength" or "Warning" ? new DataGridLength(85) : new DataGridLength(1, DataGridLengthUnitType.Star) });
            System.Windows.Automation.AutomationProperties.SetName(view, "Pronunciation changes in execution order, source positions in UTF-16 characters");
            tabs.Items.Add(new TabItem { Header = "Pronunciation changes", Content = view });
        }
        var map = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, ItemsSource = prepared.Spans, CanUserAddRows = false, EnableRowVirtualization = true };
        map.Columns.Add(new DataGridTextColumn { Header = "Start", Binding = new Binding("Start"), Width = 65 });
        map.Columns.Add(new DataGridTextColumn { Header = "Length", Binding = new Binding("Length"), Width = 65 });
        map.Columns.Add(new DataGridTextColumn { Header = "Accounting", Binding = new Binding("Kind"), Width = 170 });
        map.Columns.Add(new DataGridTextColumn { Header = "Original", Binding = new Binding("Original"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        map.Columns.Add(new DataGridTextColumn { Header = "Narration", Binding = new Binding("Narration"), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        tabs.Items.Add(new TabItem { Header = "Changes & source coverage", Content = map });
        var chunks = Chunker.Split(prepared.Script, 450);
        var chunkView = new DataGrid { IsReadOnly = true, AutoGenerateColumns = true, ItemsSource = chunks, CanUserAddRows = false };
        tabs.Items.Add(new TabItem { Header = $"Chunk plan · {chunks.Count(c => c.HardSplit)} fallback splits", Content = chunkView });
        panel.Children.Add(tabs); Content = panel;
    }
    internal static RichTextBox HighlightedText(IReadOnlyList<ReviewRun> runs, string name)
    {
        var paragraph = new Paragraph { Margin = new(0) };
        foreach (var segment in runs)
        {
            var run = new Run(segment.Text);
            if (segment.Kind != ReviewHighlightKind.Unchanged)
            {
                run.Background = SystemParameters.HighContrast ? SystemColors.HighlightBrush : new SolidColorBrush(segment.Kind == ReviewHighlightKind.Excluded ? Color.FromRgb(255, 199, 159) : Color.FromRgb(255, 232, 159));
                run.Foreground = SystemParameters.HighContrast ? SystemColors.HighlightTextBrush : Brushes.Black;
            }
            paragraph.Inlines.Add(run);
        }
        var box = new RichTextBox { IsReadOnly = true, Document = new FlowDocument(paragraph) { PagePadding = new(0) },
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new(18), FontSize = 15,
            Background = (Brush)Application.Current.Resources["Surface"], Foreground = (Brush)Application.Current.Resources["Ink"] };
        System.Windows.Automation.AutomationProperties.SetName(box, name); return box;
    }
    internal static string SelectedSpokenText(RichTextBox box)
    {
        var text = box.Selection.Text;
        // WPF adds a document paragraph separator when selecting through ContentEnd.
        // Remove only that synthetic separator; approved content and its whitespace remain intact.
        return box.Selection.End.CompareTo(box.Document.ContentEnd) == 0 && text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2] : text;
    }
    private static TextBox TextArea(string text, string name)
    {
        var box = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new(18), FontSize = 15, Background = (Brush)Application.Current.Resources["Surface"], Foreground = (Brush)Application.Current.Resources["Ink"] };
        System.Windows.Automation.AutomationProperties.SetName(box, name); return box;
    }
}
public sealed class DeleteWindow : Window
{
    private readonly CheckBox exports = new() { Content = "Also remove matching managed MP3s in the output folder", Margin = new(0, 16, 0, 16) };
    public bool DeleteExports => exports.IsChecked == true;
    public DeleteWindow(int count, int recordedExportRemovals = 0, int pendingRequests = 0)
    {
        Title = "Remove managed narrations · CommuteCast"; Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(26) };
        panel.Children.Add(new TextBlock { Text = $"Delete {count} narration{(count == 1 ? "" : "s")}?", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Local source, prepared text, history, and audio for these items will be removed. Active generation stops first. Unrelated output files and speech models are preserved.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 14, 0, 0) });
        panel.Children.Add(exports);
        if (pendingRequests > 0)
            panel.Children.Add(new TextBlock { Text = $"{pendingRequests} selected item{(pendingRequests == 1 ? " already has" : "s already have")} removal recorded. Closing this dialog leaves those requests pending; they can finish when CommuteCast restarts.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) });
        if (recordedExportRemovals > 0)
            panel.Children.Add(new TextBlock { Text = $"{recordedExportRemovals} selected item{(recordedExportRemovals == 1 ? " already has" : "s already have")} export removal recorded. Retrying completes that existing request even with the box unchecked; checking it adds export removal for the other selected items.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 14) });
        panel.Children.Add(new TextBlock { Text = "Your current editor draft, local migration backups and recovered copies are retained separately. Removing an exported file may synchronize its deletion through OneDrive. Cloud retention, recycle bins, and phone copies cannot be erased here.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 24, 0, 0) };
        buttons.Children.Add(new Button { Content = pendingRequests > 0 ? "Close" : "Keep narrations", IsCancel = true, Padding = new(16, 9, 16, 9), Margin = new(0, 0, 10, 0) });
        var delete = new Button { Content = "Delete managed items", Padding = new(16, 9, 16, 9) };
        delete.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(delete); panel.Children.Add(buttons); Content = panel;
    }
}
