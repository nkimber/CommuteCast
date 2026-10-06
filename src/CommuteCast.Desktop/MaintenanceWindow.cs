using CommuteCast.Infrastructure;
using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace CommuteCast.Desktop;

/// <summary>Shown after the editor's workers and commands settle. The application retains its workspace lease.</summary>
public sealed class MaintenanceWindow : Window
{
    private readonly WorkspaceMaintenance maintenance;
    private readonly WorkspaceLease lease;
    private readonly StackPanel actions = new();
    private readonly TextBlock status = Text("Ready. Generation is stopped while this screen is open.");
    private readonly TextBox summary = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, MinHeight = 165, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new(14) };
    private readonly ProgressBar progress = new() { Height = 4, IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly Button restore;
    private BackupReview? reviewed;
    private bool busy;
    public MaintenanceWindow(WorkspaceLease lease)
    {
        this.lease = lease; maintenance = new(lease);
        Title = "Backup & restore · CommuteCast"; Width = 900; Height = 750; MinWidth = 650; MinHeight = 540;
        FontFamily = new("Segoe UI"); FontSize = 14; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Canvas"); SetResourceReference(ForegroundProperty, "Ink");
        var panel = new DockPanel { Margin = new(30) }; Content = panel;
        var heading = new StackPanel(); heading.Children.Add(Text("Keep your listening library safe.", 28));
        heading.Children.Add(Text("Generation and playback are stopped. Backups contain private source, draft, settings and current job audio. Keep them in local nonsynced storage.", margin: new(0, 12, 0, 18)));
        heading.Children.Add(Text("Private workspace: " + lease.Workspace.Root, 12, new(0, 0, 0, 18)));
        DockPanel.SetDock(heading, Dock.Top); panel.Children.Add(heading);
        var footer = new StackPanel { Margin = new(0, 18, 0, 0) };
        AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); footer.Children.Add(status); footer.Children.Add(progress);
        DockPanel.SetDock(footer, Dock.Bottom); panel.Children.Add(footer);
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = actions });
        actions.Children.Add(Text("Current-state backup", 21));
        actions.Children.Add(Text("Create a verified copy under the private backups folder. Historical backup/recovery copies, exports, provisioning models and Docker images are separate.", margin: new(0, 8, 0, 12)));
        actions.Children.Add(ActionButton("Create verified backup", CreateAsync));
        actions.Children.Add(Text("Restore a completed backup", 21, new(0, 20, 0, 8)));
        actions.Children.Add(Text("Choose a completed backup folder. Verification checks every recorded file before restore is offered. Previous local state is retained for recovery; exported MP3s and Docker artifacts remain separate.", margin: new(0, 0, 0, 12)));
        actions.Children.Add(ActionButton("Choose & verify backup…", ChooseAsync));
        summary.Text = "No backup selected.";
        summary.SetResourceReference(BackgroundProperty, "Surface"); summary.SetResourceReference(ForegroundProperty, "Ink"); summary.SetResourceReference(BorderBrushProperty, "Line");
        AutomationProperties.SetName(summary, "Verified backup details"); actions.Children.Add(summary);
        restore = ActionButton("Restore reviewed backup…", RestoreAsync); restore.IsEnabled = false; actions.Children.Add(restore);
        actions.Children.Add(ActionButton("Recover an interrupted restore", RecoverAsync));
        actions.Children.Add(Text("Restoring replaces current local records, including jobs submitted after the backup. Old source and audio remain in retained recovery storage. Cache cleanup and narration deletion do not remove backups or recovery copies.", 12, new(0, 14, 0, 12)));
        actions.Children.Add(ActionButton("Return to editor", ReturnAsync));
        actions.Children.Add(Text("Returning reloads the saved draft and settings. Queued generation resumes if the saved queue is not paused. Close this window to leave the application stopped.", 12, new(0, 4, 0, 0)));
        Closing += OnClosing;
    }
    private static TextBlock Text(string value, double size = 14, Thickness? margin = null)
    {
        var block = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new(0) };
        block.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); return block;
    }
    private Button ActionButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Padding = new(18, 10, 18, 10), Margin = new(0, 0, 0, 10) };
        button.SetResourceReference(BackgroundProperty, "Surface"); button.SetResourceReference(ForegroundProperty, "Ink"); button.SetResourceReference(BorderBrushProperty, "Line");
        button.Click += async (_, _) => await RunAsync(action); return button;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return;
        busy = true; actions.IsEnabled = false; progress.Visibility = Visibility.Visible;
        try { await action(); }
        catch (Exception error) { status.Text = QueueCoordinator.FriendlyError(error); }
        finally { busy = false; actions.IsEnabled = true; progress.Visibility = Visibility.Collapsed; restore.IsEnabled = reviewed is not null; }
    }
    private void ClearReview() { reviewed = null; summary.Text = "No backup selected."; restore.IsEnabled = false; }
    private async Task CreateAsync()
    {
        ClearReview(); status.Text = "Creating and verifying a private backup…";
        reviewed = await Task.Run(() => maintenance.CreateAsync()); summary.Text = reviewed.Summary;
        status.Text = "Verified backup saved. The summary above identifies the complete private copy.";
    }
    private async Task ChooseAsync()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a completed local CommuteCast backup folder", Multiselect = false };
        var parent = Path.Combine(lease.Workspace.Root, "backups"); if (Directory.Exists(parent)) dialog.InitialDirectory = parent;
        if (dialog.ShowDialog(this) != true) return;
        ClearReview(); status.Text = "Verifying the selected backup and its queue…";
        reviewed = await Task.Run(() => maintenance.ReviewAsync(dialog.FolderName)); summary.Text = reviewed.Summary;
        status.Text = "Backup verified. Review its date and record count before choosing Restore.";
    }
    private async Task RestoreAsync()
    {
        var selected = reviewed ?? throw new IOException("Choose and verify a backup first.");
        var confirmation = new RestoreConfirmationWindow(selected) { Owner = this };
        if (confirmation.ShowDialog() != true) { status.Text = "Restore cancelled. Current local state was kept."; return; }
        ClearReview(); status.Text = "Restoring verified local state. Previous files are being retained for recovery…";
        var result = await Task.Run(() => maintenance.RestoreAsync(selected, true));
        summary.Text = "Restore completed. Previous local state was retained at:\n" + result.PreviousState;
        status.Text = "Local state restored. Return to the editor to reload it; the saved queue pause setting controls dispatch.";
    }
    private async Task RecoverAsync()
    {
        ClearReview(); status.Text = "Checking interrupted restore recovery…";
        var recovered = await Task.Run(() => maintenance.RecoverAsync());
        status.Text = recovered ? "Interrupted restore settled. Return to the editor to reload the verified state." : "No unfinished restore was found.";
    }
    private async Task ReturnAsync()
    {
        status.Text = "Checking local state before reopening the editor…";
        await ((App)Application.Current).ReturnToEditorAsync(this);
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!busy) return;
        e.Cancel = true; status.Text = "Wait for the current operation to settle before closing. Recovery files are retained if the process is interrupted.";
    }
    internal void CloseAfterTransition() { Closing -= OnClosing; Close(); }
}

public sealed class RestoreConfirmationWindow : Window
{
    public RestoreConfirmationWindow(BackupReview review)
    {
        Title = "Confirm local restore · CommuteCast"; Width = 640; Height = 530; MinWidth = 520; MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner; FontFamily = new("Segoe UI"); FontSize = 14;
        SetResourceReference(BackgroundProperty, "Canvas"); SetResourceReference(ForegroundProperty, "Ink");
        var panel = new DockPanel { Margin = new(26) }; Content = panel;
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 18, 0, 0) };
        buttons.Children.Add(new Button { Content = "Keep current state", IsCancel = true, IsDefault = true, Padding = new(16, 10, 16, 10), Margin = new(0, 0, 10, 0) });
        var replace = new Button { Content = "Restore local state", Padding = new(16, 10, 16, 10) }; replace.Click += (_, _) => DialogResult = true; buttons.Children.Add(replace);
        DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var details = new StackPanel();
        details.Children.Add(new TextBlock { Text = "Replace the current local library?", FontSize = 23, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        details.Children.Add(new TextBlock { Text = review.Summary, TextWrapping = TextWrapping.Wrap, Margin = new(0, 18, 0, 18) });
        details.Children.Add(new TextBlock { Text = "This replaces the current queue/history, draft, settings, provider/recovery records and job files. Jobs submitted after the backup leave the active library. Previous local state is retained in private recovery storage. Exported MP3s, models and Docker images remain separate. Queued work can resume when you return to the editor using the restored pause setting.", TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new ScrollViewer { Content = details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
}
