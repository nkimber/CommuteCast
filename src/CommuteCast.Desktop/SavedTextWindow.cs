using System.Windows;
using System.Windows.Controls;

namespace CommuteCast.Desktop;

public sealed class SavedTextWindow : Window
{
    private readonly TextBox text;
    public string TextContent => text.Text;
    public SavedTextWindow(string title, string content, bool editable = false, Action<string>? copyText = null)
    {
        Title = title; Width = 900; Height = 650; MinWidth = 650; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Canvas"); SetResourceReference(ForegroundProperty, "Ink");
        var layout = new Grid { Margin = new Thickness(24) };
        layout.RowDefinitions.Add(new() { Height = GridLength.Auto }); layout.RowDefinitions.Add(new()); layout.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var heading = new TextBlock { Text = editable ? "Paste the exact prompt used to generate this script. Leave empty to remove it." : title,
            TextWrapping = TextWrapping.Wrap, FontSize = 18, Margin = new Thickness(0, 0, 0, 16) };
        layout.Children.Add(heading);
        text = new TextBox { Text = content, IsReadOnly = !editable, AcceptsReturn = true, AcceptsTab = true,
            TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, FontSize = 16, Padding = new Thickness(16),
            MaxLength = editable ? 100000 : 0 };
        text.SetResourceReference(Control.BackgroundProperty, "Surface"); text.SetResourceReference(Control.ForegroundProperty, "Ink");
        System.Windows.Automation.AutomationProperties.SetName(text, editable ? "Generation prompt" : title);
        Grid.SetRow(text, 1); layout.Children.Add(text);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        var copy = new Button { Content = "Copy text", Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 12, 0) };
        copy.Click += (_, _) =>
        {
            var copied = editable ? TextContent : content;
            if (copied.Length == 0) return;
            try { (copyText ?? Clipboard.SetText)(copied); }
            catch (System.Runtime.InteropServices.ExternalException) { heading.Text = "The clipboard is busy. Try Copy text again."; }
        };
        actions.Children.Add(copy);
        if (editable)
        {
            var save = new Button { Content = "Save prompt", IsDefault = true, Padding = new Thickness(16, 10, 16, 10), Margin = new Thickness(0, 0, 12, 0) };
            save.Click += (_, _) => DialogResult = true; actions.Children.Add(save);
        }
        actions.Children.Add(new Button { Content = editable ? "Cancel" : "Close", IsCancel = true, Padding = new Thickness(16, 10, 16, 10) });
        foreach (Button button in actions.Children)
        {
            button.SetResourceReference(Control.BackgroundProperty, "Surface"); button.SetResourceReference(Control.ForegroundProperty, "Ink");
            button.SetResourceReference(Control.BorderBrushProperty, "Line");
        }
        Grid.SetRow(actions, 2); layout.Children.Add(actions); Content = layout;
    }
}
