using System.Windows;
namespace CommuteCast.Desktop;
public partial class PodcastCastWindow : Window
{
    public PodcastCastWindow() => InitializeComponent();
    private void Done(object sender, RoutedEventArgs e) => Close();
}
