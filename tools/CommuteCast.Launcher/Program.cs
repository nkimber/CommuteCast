using CommuteCast.Infrastructure;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;

internal static class Program
{
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(IntPtr owner, string text, string caption, uint type);
    [STAThread] private static int Main(string[] args)
    {
        AppLogging.Start("launcher");
        Mutex? mutex = null; var held = false; var inspect = args.Contains("--inspect", StringComparer.Ordinal);
        try
        {
            if (!OperatingSystem.IsWindows()) throw new IOException("CommuteCast launcher requires Windows x64.");
            string? root = null; var setup = false; var maintenance = false; var inspected = false;
            for (var i = 0; i < args.Length; i++)
                switch (args[i])
                {
                    case "--setup" when !setup && !maintenance: setup = true; break;
                    case "--maintenance" when !setup && !maintenance: maintenance = true; break;
                    case "--inspect" when !inspected: inspected = true; break;
                    case "--install-root" when root is null && i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal): root = Path.GetFullPath(args[++i]); break;
                    default: throw new ArgumentException("Use CommuteCast.exe with no arguments, --maintenance, or --setup. --inspect reports the verified plan without starting the application.");
                }
            var ownRoot = Path.GetDirectoryName(Environment.ProcessPath)!;
            root ??= File.Exists(Path.Combine(ownRoot, "installation.owner.json")) ? ownRoot : Installation.DefaultRoot;
            mutex = new Mutex(false, "Local\\CommuteCast-" + Environment.UserName);
            try { held = mutex.WaitOne(0); } catch (AbandonedMutexException) { held = true; }
            if (!held) throw new IOException("CommuteCast or setup is already open. Close it before selecting another mode.");
            var plan = LauncherPlan.CreateAsync(root, setup, maintenance).GetAwaiter().GetResult();
            mutex.ReleaseMutex(); held = false; mutex.Dispose(); mutex = null;
            if (inspect)
            {
                var extraction = Environment.GetEnvironmentVariable("DOTNET_BUNDLE_EXTRACT_BASE_DIR");
                using var process = Process.GetCurrentProcess();
                var runtime = process.Modules.Cast<ProcessModule>().SingleOrDefault(module => module.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
                // The Windows single-file superhost statically links CLR; older bundle layouts extract it.
                bool? bundledRuntimeLoaded = runtime is null ? true : extraction is null ? null : Workspace.IsWithin(extraction, runtime.FileName);
                var runtimeKind = runtime is null ? "statically linked" : bundledRuntimeLoaded == true ? "extracted bundle" : "external or unclassified";
                Console.WriteLine(JsonSerializer.Serialize(new { plan.Executable, plan.Arguments, plan.PackageId, plan.ExternalSetup, bundledRuntimeLoaded, runtimeKind })); return 0;
            }
            var start = new ProcessStartInfo(plan.Executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(plan.Executable)! };
            foreach (var argument in plan.Arguments) start.ArgumentList.Add(argument);
            Process.Start(start); return 0;
        }
        catch (Exception error)
        {
            AppLogging.Failure("InstalledLaunch", error);
            var message = QueueCoordinator.FriendlyError(error);
            if (inspect) Console.Error.WriteLine(message); else MessageBoxW(IntPtr.Zero, message + "\nUse a complete verified portable setup package for installation recovery.", "CommuteCast", 0x10);
            return 1;
        }
        finally { if (held) mutex?.ReleaseMutex(); mutex?.Dispose(); AppLogging.Stop(); }
    }
}
