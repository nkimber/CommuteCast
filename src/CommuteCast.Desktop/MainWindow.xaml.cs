using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace CommuteCast.Desktop;
using System.ComponentModel;
using CommuteCast.Infrastructure;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel model;
    private bool closing, closed;
    public MainWindow(MainViewModel model)
    {
        this.model = model;
        InitializeComponent();
        DataContext = model;
        model.DraftQueued += () => SourceInput.Focus();
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        try { await model.InitializeAsync(); }
        catch (Exception error) { MessageBox.Show(this, QueueCoordinator.FriendlyError(error), "CommuteCast"); }
    }
    private async void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (closed) return;
        e.Cancel = true;
        if (closing) return;
        if (model.Jobs.Any(j => j.Job.Stage is Core.JobStage.Synthesizing or Core.JobStage.Assembling or Core.JobStage.Exporting or Core.JobStage.WaitingForService or Core.JobStage.Validating) && MessageBox.Show(this, "Closing stops generation on this laptop. Validated chunks and queued jobs will be saved for relaunch. Close CommuteCast?", "Close CommuteCast", MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
        closing = true; IsEnabled = false;
        try { await model.DisposeAsync(); closed = true; Close(); }
        catch (Exception error)
        {
            var methods = new System.Diagnostics.StackTrace(error, false).GetFrames().Select(f => f.GetMethod()).Where(m => m is not null).Select(m => m!.DeclaringType?.FullName + "." + m.Name);
            await Workspace.AtomicWriteAsync(System.IO.Path.Combine(model.Workspace.Root, "last-shutdown-error.json"), System.Text.Json.JsonSerializer.Serialize(new { type = error.GetType().Name, methods }));
            MessageBox.Show(this, "Shutdown could not finish: " + QueueCoordinator.FriendlyError(error) + "\nThe durable queue is retained.", "CommuteCast"); closed = true; Close();
        }
    }
}
