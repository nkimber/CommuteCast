using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Diagnostics;

namespace CommuteCast.Desktop;

public sealed class StartupWindow : Window
{
    private readonly TextBlock status = new() { Text = "Preparing launch", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 14) };
    private readonly TextBlock timing = new() { Text = "Elapsed 00:00 · local startup checks", Margin = new Thickness(0, 8, 0, 0) };
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool allowClose, cancelling;
    public StartupWindow(Action cancel)
    {
        Title = "Starting CommuteCast"; Width = 500; Height = 300; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Canvas"); SetResourceReference(ForegroundProperty, "Ink");
        var panel = new StackPanel { Margin = new Thickness(26) };
        System.Windows.Automation.AutomationProperties.SetLiveSetting(status, System.Windows.Automation.AutomationLiveSetting.Polite);
        panel.Children.Add(new TextBlock { Text = "Starting CommuteCast", FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(status); panel.Children.Add(new ProgressBar { IsIndeterminate = true, Height = 5 });
        panel.Children.Add(timing);
        var button = new Button { Content = "Cancel startup", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 16, 0, 0) };
        void RequestCancel()
        {
            if (cancelling) return; cancelling = true; button.IsEnabled = false;
            status.Text = "Stopping startup safely. Waiting for the current local operation to finish."; cancel();
        }
        button.Click += (_, _) => RequestCancel(); panel.Children.Add(button); Content = panel;
        Closing += (_, e) => { if (!allowClose) { e.Cancel = true; RequestCancel(); } };
        timer.Tick += (_, _) => timing.Text = $"Elapsed {elapsed.Elapsed:mm\\:ss} · local startup checks";
        timer.Start(); Closed += (_, _) => timer.Stop();
    }
    public void Report(string message) { if (!cancelling) status.Text = message; }
    public void Finish() { allowClose = true; Close(); }
}
