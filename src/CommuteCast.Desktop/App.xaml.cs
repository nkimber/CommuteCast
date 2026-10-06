using System.Configuration;
using System.Data;
using System.Windows;
using System.IO;

namespace CommuteCast.Desktop;
using System.Windows.Media;
using CommuteCast.Infrastructure;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private Mutex? instance;
    private bool ownsMutex;
    private WorkspaceLease? workspaceLease;
    private Installation? installation;
    private string? launchAfterExit;
    private static bool dark;
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        instance = new Mutex(true, "Local\\CommuteCast-" + Environment.UserName, out ownsMutex);
        if (!ownsMutex) { MessageBox.Show("CommuteCast or its setup is already open for this Windows user. Close it before launching another mode.", "CommuteCast"); Shutdown(); return; }
        SetTheme();
        try
        {
            var startup = DesktopStartup.Parse(e.Args, string.Equals(Path.GetFileName(Environment.ProcessPath), "CommuteCast.Setup.exe", StringComparison.OrdinalIgnoreCase));
            if (startup.Mode == DesktopMode.Setup)
            {
                var package = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
                var setup = new SetupWindow(package, startup.InstallRoot, startup.PrivateRoot); MainWindow = setup; setup.Show(); return;
            }
            var maintenance = startup.Mode == DesktopMode.Maintenance;
            var installationRoot = Installation.FindRoot(AppContext.BaseDirectory);
            installation = installationRoot is null ? null : new Installation(installationRoot);
            var owner = installation is null ? null : await installation.ReadOwnerAsync();
            var workspace = new Workspace(owner?.WorkspaceRoot);
            workspaceLease = WorkspaceLease.Acquire(workspace);
            if (installation?.HasPendingOperation == true) throw new IOException("Deployment is unfinished. Close CommuteCast and run recover-install from an extracted portable package before relaunching.");
            if (maintenance)
            {
                await VerifyInstallationAsync(true);
                var window = new MaintenanceWindow(workspaceLease); MainWindow = window; window.Show();
            }
            else await ShowEditorAsync();
        }
        catch (Exception error) { MessageBox.Show(QueueCoordinator.FriendlyError(error) + "\nFor local queue or settings repair, close the application and launch CommuteCast.Desktop.exe --maintenance from the current release. Deployment recovery remains a separate maintenance-tool operation.", "CommuteCast startup"); Shutdown(1); }
    }
    private async Task VerifyInstallationAsync(bool maintenance)
    {
        if (installation is null) return;
        var active = maintenance ? await installation.InspectForMaintenanceAsync(workspaceLease!) : await installation.InspectAsync(workspaceLease!);
        if (active.Executable is null || !Path.GetFullPath(active.Executable).Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("This release is archived or uninstalled. Launch the current installed release instead.");
    }
    private async Task ShowEditorAsync()
    {
        await WorkspaceBackup.RecoverInterruptedAsync(workspaceLease!);
        await VerifyInstallationAsync(false);
        var workspace = workspaceLease!.Workspace;
        var database = Path.Combine(workspace.Root, "queue.db");
        if (installation is null && File.Exists(database)) await SqliteSchema.ValidateDatabaseAsync(database);
        var settings = await workspace.LoadSettingsAsync();
        var window = new MainWindow(new MainViewModel(settings, workspace)); MainWindow = window; window.Show();
    }
    internal void EnterMaintenance(MainWindow previous)
    {
        var window = new MaintenanceWindow(workspaceLease!); MainWindow = window; window.Show(); previous.CloseAfterTransition();
    }
    internal async Task ReturnToEditorAsync(MaintenanceWindow previous)
    { await ShowEditorAsync(); previous.CloseAfterTransition(); }
    internal void LaunchAfterExit(string executable) { launchAfterExit = executable; Shutdown(); }
    public static void ToggleTheme() { dark = !dark; SetTheme(); }
    private static void SetTheme()
    {
        var palette = dark
            ? new Dictionary<string, string> { ["Canvas"] = "#202722", ["Surface"] = "#2A332D", ["Ink"] = "#F3F0E8", ["Muted"] = "#B7C2BA", ["Line"] = "#536056", ["Tint"] = "#343E33", ["Sidebar"] = "#18221B", ["Accent"] = "#AA4B27", ["Error"] = "#FFB193" }
            : new Dictionary<string, string> { ["Canvas"] = "#F7F5EF", ["Surface"] = "#FFFEFA", ["Ink"] = "#26352B", ["Muted"] = "#616B62", ["Line"] = "#DADFD5", ["Tint"] = "#ECEEE1", ["Sidebar"] = "#26382C", ["Accent"] = "#AA4B27", ["Error"] = "#A43520" };
        foreach (var (key, value) in palette) Current.Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        Current.Resources["SidebarInk"] = new SolidColorBrush(Color.FromRgb(248, 244, 234));
        Current.Resources["SidebarMuted"] = new SolidColorBrush(Color.FromRgb(173, 183, 174));
        if (SystemParameters.HighContrast)
        {
            foreach (var key in new[] { "Canvas", "Surface", "Tint", "Sidebar" }) Current.Resources[key] = SystemColors.WindowBrush;
            foreach (var key in new[] { "Ink", "Muted", "Error", "Line" }) Current.Resources[key] = SystemColors.WindowTextBrush;
            Current.Resources["Accent"] = SystemColors.HighlightBrush;
            Current.Resources["SidebarInk"] = SystemColors.WindowTextBrush;
            Current.Resources["SidebarMuted"] = SystemColors.WindowTextBrush;
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        workspaceLease?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
        if (launchAfterExit is not null)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launchAfterExit) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(launchAfterExit)!, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden }); }
            catch (Exception error) { MessageBox.Show(QueueCoordinator.FriendlyError(error), "CommuteCast launch"); }
    }
}
