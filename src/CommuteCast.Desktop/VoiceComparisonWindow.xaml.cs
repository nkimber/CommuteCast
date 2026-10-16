using System.Windows;

namespace CommuteCast.Desktop;
public partial class VoiceComparisonWindow : Window
{
    public VoiceComparisonWindow() => InitializeComponent();
    private void Done(object sender, RoutedEventArgs e) => Close();
}
