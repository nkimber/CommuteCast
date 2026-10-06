using CommuteCast.Core;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace CommuteCast.Desktop;

public sealed class PreparationWindow : Window
{
    public PreparationWindow(PreparedText prepared, string source)
    {
        Title = "Review narration preparation · CommuteCast"; Width = 1000; Height = 740; MinWidth = 700; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["Canvas"]; Foreground = (Brush)Application.Current.Resources["Ink"];
        var panel = new DockPanel { Margin = new(24) };
        var heading = new TextBlock { Text = $"{source.Length:N0} source characters accounted for · {prepared.Spans.Count:N0} spans · {prepared.Spans.Count(s => s.Kind == "explicit exclusion")} explicitly excluded spans\nPreparation {prepared.Version}. Automated accounting does not prove exact spoken fidelity.", Margin = new(0, 0, 0, 18), TextWrapping = TextWrapping.Wrap };
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var close = new Button { Content = "Close review", Padding = new(20, 10, 20, 10), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 16, 0, 0), IsCancel = true };
        DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Spoken text", Content = TextArea(prepared.Script, "Prepared narration script") });
        tabs.Items.Add(new TabItem { Header = "Original source", Content = TextArea(source, "Original submitted source") });
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
    public DeleteWindow(int count)
    {
        Title = "Remove managed narrations · CommuteCast"; Width = 540; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(26) };
        panel.Children.Add(new TextBlock { Text = $"Delete {count} narration{(count == 1 ? "" : "s")}?", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "Local source, prepared text, history, and audio for these items will be removed. Active generation stops first. Unrelated output files and speech models are preserved.", TextWrapping = TextWrapping.Wrap, Margin = new(0, 14, 0, 0) });
        panel.Children.Add(exports);
        panel.Children.Add(new TextBlock { Text = "Local migration backups and recovered draft copies are retained separately. Removing an exported file may synchronize its deletion through OneDrive. Cloud retention, recycle bins, and phone copies cannot be erased here.", TextWrapping = TextWrapping.Wrap, FontSize = 12 });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 24, 0, 0) };
        buttons.Children.Add(new Button { Content = "Keep narrations", IsCancel = true, Padding = new(16, 9, 16, 9), Margin = new(0, 0, 10, 0) });
        var delete = new Button { Content = "Delete managed items", Padding = new(16, 9, 16, 9) };
        delete.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(delete); panel.Children.Add(buttons); Content = panel;
    }
}
