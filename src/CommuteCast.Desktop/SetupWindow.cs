using CommuteCast.Infrastructure;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace CommuteCast.Desktop;

public sealed class SetupWindow : Window
{
    private readonly string packageRoot;
    private readonly string? privateOverride;
    private readonly TextBox installRoot;
    private readonly TextBox details = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 220, Padding = new(14) };
    private readonly TextBlock status = Text("Review the package and installation before choosing an action.");
    private readonly TextBlock setup = Text("Prerequisites have not been inspected. Inspection never starts services.", 12);
    private readonly StackPanel panel = new();
    private readonly ProgressBar progress = new() { Height = 4, IsIndeterminate = true, Visibility = Visibility.Collapsed };
    private readonly List<Button> mutations = [];
    private readonly Button rollback, uninstall, recover, launch, inspect;
    private DeploymentSession? session;
    private DeploymentReview? review;
    private bool busy;
    public SetupWindow(string packageRoot, string? installationRoot, string? privateRoot)
    {
        this.packageRoot = packageRoot; privateOverride = privateRoot;
        Title = "Setup · CommuteCast"; Width = 1050; Height = 850; MinWidth = 700; MinHeight = 560; FontFamily = new("Segoe UI"); FontSize = 14; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        SetResourceReference(BackgroundProperty, "Canvas"); SetResourceReference(ForegroundProperty, "Ink");
        var container = new DockPanel { Margin = new(30) }; Content = container;
        var heading = new StackPanel(); heading.Children.Add(Text("Set up CommuteCast for this Windows user.", 26));
        heading.Children.Add(Text("Install verified app binaries without elevation. Docker/WSL, speech artifacts, signing and corporate approval remain separate prerequisites.", 14, new(0, 12, 0, 18)));
        DockPanel.SetDock(heading, Dock.Top); container.Children.Add(heading);
        var footer = new StackPanel { Margin = new(0, 16, 0, 0) }; AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite); footer.Children.Add(status); footer.Children.Add(progress); DockPanel.SetDock(footer, Dock.Bottom); container.Children.Add(footer);
        container.Children.Add(new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        panel.Children.Add(Text("Installation folder", 16));
        installRoot = new TextBox { Text = installationRoot ?? Installation.DefaultRoot, Padding = new(12), Margin = new(0, 8, 0, 12) };
        AutomationProperties.SetName(installRoot, "Per-user installation folder"); ThemeBox(installRoot); panel.Children.Add(installRoot);
        panel.Children.Add(Text("Use a dedicated local binary folder. Private source and queue data stay in a separate bound folder. Changing the path requires a new review.", 12, new(0, 0, 0, 12)));
        panel.Children.Add(ActionButton("Review package & installation", ReviewAsync)); ThemeBox(details); AutomationProperties.SetName(details, "Reviewed deployment details"); panel.Children.Add(details);
        inspect = ActionButton("Check prerequisites without changes", InspectAsync); panel.Children.Add(inspect); panel.Children.Add(setup);
        var buttons = new WrapPanel { Margin = new(0, 20, 0, 0) };
        var install = ActionButton("Install / update reviewed package…", () => ApplyAsync(DeploymentAction.Install)); mutations.Add(install); buttons.Children.Add(install);
        rollback = ActionButton("Roll back app & local state…", () => ApplyAsync(DeploymentAction.Rollback)); mutations.Add(rollback); buttons.Children.Add(rollback);
        uninstall = ActionButton("Uninstall…", UninstallAsync); mutations.Add(uninstall); buttons.Children.Add(uninstall);
        recover = ActionButton("Recover deployment…", () => ApplyAsync(DeploymentAction.Recover)); mutations.Add(recover); buttons.Children.Add(recover);
        launch = ActionButton("Launch installed CommuteCast", LaunchAsync); buttons.Children.Add(launch); panel.Children.Add(buttons);
        panel.Children.Add(Text("Updates take a compatible private snapshot before activation. Rollback restores the recorded previous binary and snapshot; later work is retained in recovery storage. Uninstall defaults to keeping local data. Exports, models and Docker artifacts are separate.", 12, new(0, 10, 0, 0)));
        panel.Children.Add(Text("Checksums verify inventory; signing and Windows registration remain release work. The installed launcher can prepare an external setup recovery copy, which remains after uninstall. A separately extracted portable setup also works. Run setup outside the installation folder to remove installed binaries.", 12, new(0, 10, 0, 12)));
        installRoot.TextChanged += (_, _) => { review = null; details.Text = "The installation folder changed. Review this destination before continuing."; setup.Text = "Prerequisites have not been inspected for this destination."; UpdateButtons(); };
        Loaded += async (_, _) => await RunAsync(ReviewAsync); Closing += (_, e) => { if (busy) { e.Cancel = true; status.Text = "Wait for setup to settle before closing. Pending deployment has recorded recovery state."; } };
        UpdateButtons();
    }
    private static TextBlock Text(string value, double size = 14, Thickness? margin = null)
    { var block = new TextBlock { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = margin ?? new(0) }; block.SetResourceReference(TextBlock.ForegroundProperty, "Ink"); return block; }
    private static void ThemeBox(TextBox box)
    { box.SetResourceReference(BackgroundProperty, "Surface"); box.SetResourceReference(ForegroundProperty, "Ink"); box.SetResourceReference(BorderBrushProperty, "Line"); }
    private Button ActionButton(string label, Func<Task> action)
    {
        var button = new Button { Content = label, HorizontalAlignment = HorizontalAlignment.Left, Padding = new(16, 10, 16, 10), Margin = new(0, 0, 10, 10) };
        button.SetResourceReference(BackgroundProperty, "Surface"); button.SetResourceReference(ForegroundProperty, "Ink"); button.SetResourceReference(BorderBrushProperty, "Line"); button.Click += async (_, _) => await RunAsync(action); return button;
    }
    private void UpdateButtons()
    {
        foreach (var button in mutations) button.IsEnabled = review is not null && !review.Overview.PendingRecovery;
        rollback.IsEnabled = review?.Overview.State?.Previous is not null && !review.Overview.PendingRecovery;
        uninstall.IsEnabled = review?.Overview.State?.CurrentPackageId is not null && !review.Overview.PendingRecovery && !Workspace.IsWithin(review.InstallRoot, AppContext.BaseDirectory);
        recover.IsEnabled = review?.Overview.Owner is not null && review.Overview.PendingRecovery;
        launch.IsEnabled = review?.Overview.State?.CurrentPackageId is not null && !review.Overview.PendingRecovery;
        inspect.IsEnabled = review is not null;
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) return; busy = true; panel.IsEnabled = false; progress.Visibility = Visibility.Visible;
        try { await action(); }
        catch (Exception error) { status.Text = QueueCoordinator.FriendlyError(error); }
        finally { busy = false; panel.IsEnabled = true; progress.Visibility = Visibility.Collapsed; UpdateButtons(); }
    }
    private async Task ReviewAsync()
    {
        review = null; setup.Text = "Prerequisites have not been inspected for this review."; details.Text = "Verifying the package and reviewing local state…"; status.Text = "Reviewing setup without creating or changing local files…";
        session = new(packageRoot, installRoot.Text, privateOverride); review = await Task.Run(() => session.ReviewAsync()); details.Text = review.Summary;
        status.Text = "Review complete. Choose a prerequisite check or a deliberate setup action.";
    }
    private async Task InspectAsync()
    {
        var selected = review ?? throw new IOException("Review the package first."); status.Text = "Inspecting prerequisites without changing services…";
        setup.Text = (await Task.Run(() => session!.CheckSetupAsync(selected))).Display; status.Text = "Inspection finished. Missing prerequisites do not prevent installing binaries; generation still requires repair and approval.";
    }
    private async Task ApplyAsync(DeploymentAction action, bool? removeData = null)
    {
        var selected = review ?? throw new IOException("Review the package first.");
        var dialog = new SetupConfirmationWindow(selected, action, removeData) { Owner = this };
        if (dialog.ShowDialog() != true) { status.Text = "Setup action cancelled. Current installation and local data were kept."; return; }
        review = null; status.Text = "Applying the reviewed action. Verified copies and recovery records are retained…";
        try
        {
            var outcome = await Task.Run(() => session!.ApplyAsync(selected, action, true));
            await ReviewAsync();
            if (outcome.Setup is not null) setup.Text = outcome.Setup.Display;
            status.Text = action == DeploymentAction.Recover ? "Deployment recovery settled. Review the resulting state before launch." : "Setup action completed. The reviewed state above reflects the result; voice and device acceptance remain separate.";
        }
        catch { details.Text = "The action did not finish. Review the installation again to inspect any pending deployment and its recovery option."; throw; }
    }
    private async Task UninstallAsync()
    {
        var selected = review ?? throw new IOException("Review the installation first.");
        var choice = new UninstallScopeWindow(selected) { Owner = this }; if (choice.ShowDialog() != true) { status.Text = "Uninstall cancelled. Current files were kept."; return; }
        await ApplyAsync(choice.RemoveLocalData ? DeploymentAction.UninstallRemove : DeploymentAction.UninstallRetain, choice.RemoveLocalData);
    }
    private async Task LaunchAsync()
    {
        var executable = await Task.Run(() => session!.FindLaunchAsync(review ?? throw new IOException("Review the installation first.")));
        ((App)Application.Current).LaunchAfterExit(executable);
    }
}

public sealed class SetupConfirmationWindow : Window
{
    public SetupConfirmationWindow(DeploymentReview review, DeploymentAction action, bool? removeData)
    {
        Title = "Confirm setup action · CommuteCast"; Width = 690; Height = 620; MinWidth = 560; MinHeight = 440; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "Canvas"); SetResourceReference(ForegroundProperty, "Ink"); FontSize = 14;
        var panel = new DockPanel { Margin = new(26) }; Content = panel;
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 18, 0, 0) };
        buttons.Children.Add(new Button { Content = "Keep current state", IsCancel = true, IsDefault = true, Padding = new(16, 10, 16, 10), Margin = new(0, 0, 10, 0) });
        var apply = new Button { Content = action switch { DeploymentAction.Install => "Install reviewed package", DeploymentAction.Rollback => "Restore previous release & state", DeploymentAction.Recover => "Recover recorded deployment", _ => "Uninstall reviewed application" }, Padding = new(16, 10, 16, 10) }; apply.Click += (_, _) => DialogResult = true; buttons.Children.Add(apply); DockPanel.SetDock(buttons, Dock.Bottom); panel.Children.Add(buttons);
        var description = action switch
        {
            DeploymentAction.Install => "Verify and stage this package, snapshot the current private state, check compatible schema and activate the release. Read-only prerequisite results are reported. No software prerequisites, images or models are installed automatically.",
            DeploymentAction.Rollback => "Restore the recorded previous binary and its pre-update queue, draft, settings and job artifacts. Later work leaves the active library and remains in retained recovery storage; an undo snapshot is recorded.",
            DeploymentAction.Recover => "Settle the recorded deployment journal. Before activation this restores the original private state; after activation it retains the committed release or finishes its recorded uninstall scope.",
            _ => removeData == true ? "Remove recorded binary releases and reviewed local queue/history, draft, settings, job audio, backups and recovery files. Exports, models, Docker artifacts and unrelated root files remain separate. Private removal cannot be undone from the files it removes." : "Remove recorded binary releases and keep private source, draft, settings, queue/history, audio, backups and recovery files. Exports, models and Docker artifacts remain separate."
        };
        panel.Children.Add(new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new TextBlock { Text = description + "\n\n" + review.Summary, TextWrapping = TextWrapping.Wrap } });
    }
}
public sealed class UninstallScopeWindow : Window
{
    private readonly CheckBox remove = new() { Content = "Also remove reviewed managed local data, including backups and recovery copies", Margin = new(0, 18, 0, 18) };
    public bool RemoveLocalData => remove.IsChecked == true;
    public UninstallScopeWindow(DeploymentReview review)
    {
        Title = "Choose uninstall scope · CommuteCast"; Width = 680; Height = 330; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new(26) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = $"Keep local data by default. Removal would cover {review.LocalFiles:N0} managed private files ({review.LocalBytes / 1048576.0:0.0} MiB).", TextWrapping = TextWrapping.Wrap }); panel.Children.Add(remove);
        panel.Children.Add(new TextBlock { Text = "Only recorded binary packages are removed. Exported MP3s, Docker images/models and unrelated root files are separate. Review and confirmation follow before anything changes.", TextWrapping = TextWrapping.Wrap });
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0, 20, 0, 0) }; buttons.Children.Add(new Button { Content = "Keep installed", IsCancel = true, IsDefault = true, Padding = new(16, 10, 16, 10), Margin = new(0, 0, 10, 0) }); var next = new Button { Content = "Review uninstall…", Padding = new(16, 10, 16, 10) }; next.Click += (_, _) => DialogResult = true; buttons.Children.Add(next); panel.Children.Add(buttons);
    }
}
