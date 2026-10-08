using System.Diagnostics;
using Serilog;

namespace CommuteCast.Infrastructure;

public enum StartupPhase { SetupCache, LaunchArguments, InstallationBinding, WorkspaceLease, RestoreRecovery, InstallationVerification, QueueValidation, SettingsLoad, EditorConstruction, EditorShow, DraftLoad, AuditionRecovery, QueueRecovery, SpeechReadiness }

// Runs independently of the WPF dispatcher, so a blocked UI still leaves a last-step heartbeat.
public sealed class StartupStepTrace : IDisposable
{
    private readonly object gate = new();
    private readonly ILogger logger;
    private readonly StartupPhase phase;
    private readonly Stopwatch elapsed = Stopwatch.StartNew();
    private readonly Timer timer;
    private bool completed, disposed;
    public StartupStepTrace(StartupPhase phase, ILogger? logger = null, TimeSpan? interval = null)
    {
        this.phase = phase; this.logger = logger ?? Log.Logger;
        this.logger.Information("Startup step {StartupStep} started", phase);
        var period = interval ?? TimeSpan.FromSeconds(10);
        timer = new Timer(_ =>
        {
            lock (gate) if (!disposed)
                this.logger.Warning("Startup step {StartupStep} still running after {ElapsedMs} ms", phase, elapsed.ElapsedMilliseconds);
        }, null, period, period);
    }
    public void Complete() { lock (gate) completed = true; }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true; timer.Dispose(); elapsed.Stop();
            logger.Information("Startup step {StartupStep} ended with {Outcome} in {ElapsedMs} ms", phase, completed ? "Completed" : "Interrupted", elapsed.ElapsedMilliseconds);
        }
    }
}
