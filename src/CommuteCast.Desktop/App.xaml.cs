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
    private CachedSetupUse? setupCacheUse;
    private Installation? installation;
    private string? launchAfterExit;
    private static bool dark;
    private StartupWindow? startupWindow;
    private readonly CancellationTokenSource startupCancellation = new();
    protected override async void OnStartup(StartupEventArgs e)
    {
        AppLogging.Start("desktop");
        var assembly = typeof(App).Assembly;
        Serilog.Log.Information("Desktop build {BuildVersion}, configuration {Configuration}",
            System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>(assembly)?.InformationalVersion,
            System.Reflection.CustomAttributeExtensions.GetCustomAttribute<System.Reflection.AssemblyConfigurationAttribute>(assembly)?.Configuration);
        DispatcherUnhandledException += (_, args) => AppLogging.Failure("DispatcherUnhandledException", args.Exception, Serilog.Events.LogEventLevel.Fatal);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => { if (args.ExceptionObject is Exception error) AppLogging.Failure("UnhandledException", error, Serilog.Events.LogEventLevel.Fatal); };
        TaskScheduler.UnobservedTaskException += (_, args) => AppLogging.Failure("UnobservedTaskException", args.Exception);
        base.OnStartup(e);
        instance = new Mutex(true, "Local\\CommuteCast-" + Environment.UserName, out ownsMutex);
        if (!ownsMutex) { MessageBox.Show("CommuteCast or its setup is already open for this Windows user. Close it before launching another mode.", "CommuteCast"); Shutdown(); return; }
        SetTheme();
        try
        {
            startupWindow = new StartupWindow(() => startupCancellation.Cancel()); MainWindow = startupWindow; startupWindow.Show();
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            Serilog.Log.Information("Startup status window shown; debugger attached {DebuggerAttached}", System.Diagnostics.Debugger.IsAttached);
            setupCacheUse = await StartupAsync(StartupPhase.SetupCache, "Checking the application package", () => SetupCache.AcquireHostUseAsync(AppContext.BaseDirectory, startupCancellation.Token));
            var startup = await StartupAsync(StartupPhase.LaunchArguments, "Reading launch settings", () => Task.FromResult(DesktopStartup.Parse(e.Args, string.Equals(Path.GetFileName(Environment.ProcessPath), "CommuteCast.Setup.exe", StringComparison.OrdinalIgnoreCase))));
            Serilog.Log.Information("Startup mode {Mode}", startup.Mode);
            if (setupCacheUse is not null) startup = startup.BindCachedSetup(setupCacheUse.InstallationRoot, setupCacheUse.PrivateRoot);
            if (startup.Mode == DesktopMode.Setup)
            {
                var package = Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar))!.FullName;
                var setup = new SetupWindow(package, startup.InstallRoot, startup.PrivateRoot, setupCacheUse is not null); MainWindow = setup; setup.Show(); startupWindow.Finish(); startupWindow = null; return;
            }
            var maintenance = startup.Mode == DesktopMode.Maintenance;
            var owner = await StartupAsync(StartupPhase.InstallationBinding, "Checking the installed release", async () =>
            {
                var installationRoot = Installation.FindRoot(AppContext.BaseDirectory);
                installation = installationRoot is null ? null : new Installation(installationRoot);
                return installation is null ? null : await installation.ReadOwnerAsync(startupCancellation.Token);
            });
            workspaceLease = await StartupAsync(StartupPhase.WorkspaceLease, "Opening local narration storage", () => Task.FromResult(WorkspaceLease.Acquire(new Workspace(owner?.WorkspaceRoot))));
            if (maintenance)
            {
                await StartupAsync(StartupPhase.InstallationVerification, "Verifying the installed application", () => VerifyInstallationAsync(true));
                var window = new MaintenanceWindow(workspaceLease); MainWindow = window; window.Show();
            }
            else await ShowEditorAsync();
            startupWindow.Finish(); startupWindow = null;
        }
        catch (OperationCanceledException) when (startupCancellation.IsCancellationRequested) { Serilog.Log.Information("Startup cancelled after local work settled"); Shutdown(); }
        catch (Exception error) { AppLogging.Failure("DesktopStartupOrLaunch", error); MessageBox.Show(QueueCoordinator.FriendlyError(error) + "\nStartup step timings are in " + AppLogging.DefaultDirectory + "\nFor local queue or settings repair, close the application and launch CommuteCast.Desktop.exe --maintenance from the current release. Deployment recovery remains a separate maintenance-tool operation.", "CommuteCast startup"); Shutdown(1); }
        finally { startupWindow?.Finish(); startupWindow = null; }
    }
    private async Task<T> StartupAsync<T>(StartupPhase phase, string message, Func<Task<T>> action)
    {
        startupCancellation.Token.ThrowIfCancellationRequested();
        startupWindow?.Report(message);
        using var trace = new StartupStepTrace(phase);
        try { var result = await Task.Run(action); trace.Complete(); return result; }
        catch (Exception error) { AppLogging.Failure("Startup" + phase, error); throw; }
    }
    private Task StartupAsync(StartupPhase phase, string message, Func<Task> action) => StartupAsync(phase, message, async () => { await action(); return true; });
    private async Task VerifyInstallationAsync(bool maintenance)
    {
        if (installation is null) return;
        if (installation.HasPendingOperation) throw new IOException("Deployment is unfinished. Close CommuteCast and run recover-install from an extracted portable package before relaunching.");
        var active = maintenance ? await installation.InspectForMaintenanceAsync(workspaceLease!) : await installation.InspectAsync(workspaceLease!);
        if (active.Executable is null || !Path.GetFullPath(active.Executable).Equals(Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
            throw new IOException("This release is archived or uninstalled. Launch the current installed release instead.");
    }
    private async Task ShowEditorAsync()
    {
        await StartupAsync(StartupPhase.RestoreRecovery, "Checking interrupted local recovery", () => WorkspaceBackup.RecoverInterruptedAsync(workspaceLease!, startupCancellation.Token));
        await StartupAsync(StartupPhase.InstallationVerification, "Verifying the installed application", () => VerifyInstallationAsync(false));
        var workspace = workspaceLease!.Workspace;
        var database = Path.Combine(workspace.Root, "queue.db");
        await StartupAsync(StartupPhase.QueueValidation, "Validating the saved narration library", async () =>
        { if (installation is null && File.Exists(database)) await SqliteSchema.ValidateDatabaseAsync(database, startupCancellation.Token); });
        var settings = await StartupAsync(StartupPhase.SettingsLoad, "Loading application settings", () => workspace.LoadSettingsAsync());
        startupCancellation.Token.ThrowIfCancellationRequested();
        startupWindow?.Report("Opening your listening library");
        MainWindow window;
        using (var trace = new StartupStepTrace(StartupPhase.EditorConstruction)) { window = new MainWindow(new MainViewModel(settings, workspace)); trace.Complete(); }
        MainWindow = window;
        using (var trace = new StartupStepTrace(StartupPhase.EditorShow)) { window.Show(); trace.Complete(); }
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
        Serilog.Log.Information("Desktop exit requested with code {ExitCode}", e.ApplicationExitCode);
        workspaceLease?.Dispose();
        setupCacheUse?.Dispose();
        if (ownsMutex) instance?.ReleaseMutex();
        instance?.Dispose();
        base.OnExit(e);
        if (launchAfterExit is not null)
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(launchAfterExit) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(launchAfterExit)!, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden }); }
            catch (Exception error) { AppLogging.Failure("DesktopStartupOrLaunch", error); MessageBox.Show(QueueCoordinator.FriendlyError(error), "CommuteCast launch"); }
        AppLogging.Stop();
    }
}
