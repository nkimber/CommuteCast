using CommuteCast.Core;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace CommuteCast.Infrastructure;

[JsonConverter(typeof(JsonStringEnumConverter<SetupStatus>))]
public enum SetupStatus { Available, Missing, Unavailable, TimedOut, NeedsAttention, ReviewRequired }
public record SetupCheck(string Id, string Label, SetupStatus Status, string Detail, string NextStep);
public record SetupHost(bool Windows, string Architecture, Version WindowsVersion, string RuntimeVersion, int Processors, ulong? MemoryBytes, string? DesktopVersion, bool DesktopRunning);
public record SetupReport(DateTimeOffset CheckedUtc, SetupHost Host, IReadOnlyList<SetupCheck> Checks)
{
    public bool SelectedSpeechAvailable => Checks.Any(c => c.Id == "speech-selected" && c.Status == SetupStatus.Available);
    public string Display => string.Join("\n\n", Checks.Select(c => $"{c.Label}: {c.Status}\n{c.Detail}" + (c.NextStep.Length == 0 ? "" : "\n" + c.NextStep)));
}
public interface ISetupRuntime
{
    SetupHost InspectHost();
    Task<ProcessResult> RunAsync(string tool, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct);
    Task<ProviderInfo> ProbeSpeechAsync(string engine, CancellationToken ct);
}

public sealed class SetupRuntime(string privateRoot) : ISetupRuntime
{
    public SetupHost InspectHost()
    {
        var candidate = DockerDesktopLocation.Find();
        string? desktopVersion = null;
        if (candidate is not null) { var version = FileVersionInfo.GetVersionInfo(candidate); desktopVersion = SetupDiagnostics.SafeVersion(version.ProductVersion) ?? SetupDiagnostics.SafeVersion(version.FileVersion) ?? "unavailable"; }
        var desktopRunning = false;
        foreach (var process in Process.GetProcessesByName("Docker Desktop")) { desktopRunning = true; process.Dispose(); }
        ulong? memory = null;
        if (OperatingSystem.IsWindows()) { var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() }; if (GlobalMemoryStatusEx(ref status)) memory = status.TotalPhysical; }
        return new(OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture.ToString(), Environment.OSVersion.Version, Environment.Version.ToString(), Environment.ProcessorCount, memory, desktopVersion, desktopRunning);
    }
    public Task<ProcessResult> RunAsync(string tool, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct) => ProcessRunner.RunAsync(tool, args, timeout, ct);
    public async Task<ProviderInfo> ProbeSpeechAsync(string engine, CancellationToken ct)
    {
        // Missing setup does not create a private folder merely to inspect it.
        if (!Directory.Exists(privateRoot)) throw new IOException("Speech services are not provisioned.");
        using var provider = new LocalSpeechProvider(new Workspace(privateRoot)); return await provider.ProbeAsync(engine, ct);
    }
    [StructLayout(LayoutKind.Sequential)] private struct MemoryStatus
    { public uint Length, Load; public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
}

/// <summary>Read-only bounded observations. No raw subprocess output, paths or policy approval enter the report.</summary>
public sealed class SetupDiagnostics(ISetupRuntime runtime)
{
    public static string? SafeVersion(string? text)
    {
        var match = Regex.Match((text ?? "").Replace("\0", ""), @"(?<![\w.])\d{1,5}\.\d{1,5}(?:\.\d{1,5}){0,2}(?![\w.])", RegexOptions.CultureInvariant);
        return match.Success ? match.Value : null;
    }
    public async Task<SetupReport> CheckAsync(AppSettings settings, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token; var host = runtime.InspectHost(); var checks = new List<SetupCheck>();
        void Add(string id, string label, SetupStatus status, string detail, string next = "") => checks.Add(new(id, label, status, detail, next));
        Add("platform", "Windows platform", host.Windows && host.Architecture == "X64" && host.WindowsVersion.Build >= 22631 ? SetupStatus.Available : SetupStatus.NeedsAttention,
            $"Windows version {host.WindowsVersion}; architecture {host.Architecture}; running .NET {host.RuntimeVersion}.", "Release baseline: Windows 11 x64 23H2 or newer. IT must confirm the edition and servicing entitlement.");
        Add("resources", "Host resources", host.MemoryBytes is >= 8UL * 1024 * 1024 * 1024 ? SetupStatus.Available : SetupStatus.NeedsAttention,
            $"{host.Processors} logical processors; " + (host.MemoryBytes is null ? "physical memory could not be measured." : $"{host.MemoryBytes / 1073741824.0:0.0} GiB physical memory."), "8 GiB is a Docker prerequisite, not a measured narration performance guarantee. Verify virtualization and available capacity through the approved setup process.");
        Add("desktop", "Docker Desktop", host.DesktopVersion is null ? SetupStatus.Missing : SetupStatus.Available,
            host.DesktopVersion is null ? "No installation was found in the supported user/system locations." : $"Version {host.DesktopVersion}; process {(host.DesktopRunning ? "present" : "not observed")}.", "Use the approved IT setup or repair process. This check does not launch Docker Desktop.");
        Add("policy", "Corporate approval", SetupStatus.ReviewRequired, "Docker entitlement, endpoint policy, supported Windows servicing, redistribution and corporate phone access cannot be established by local tool checks.", "Confirm these with the release owner and IT before production use.");
        async Task<ProcessResult?> Probe(string id, string label, string tool, string[] args, string next)
        {
            try
            {
                var result = await runtime.RunAsync(tool, args, TimeSpan.FromSeconds(5), token).WaitAsync(TimeSpan.FromSeconds(5), token);
                if (result.ExitCode == 0) return result;
                Add(id, label, SetupStatus.Unavailable, "The read-only command failed; its output is withheld to avoid exposing local data.", next); return null;
            }
            catch (OperationCanceledException) { ct.ThrowIfCancellationRequested(); Add(id, label, SetupStatus.TimedOut, "The overall 30-second setup-check allowance expired.", next); return null; }
            catch (TimeoutException) { Add(id, label, SetupStatus.TimedOut, "The read-only command did not finish within five seconds.", next); return null; }
            catch (Win32Exception error) { Add(id, label, error.NativeErrorCode is 2 or 3 ? SetupStatus.Missing : SetupStatus.Unavailable, error.NativeErrorCode == 5 ? "Windows denied execution. Check approved permissions or endpoint policy." : "The tool could not be started.", next); return null; }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { Add(id, label, SetupStatus.Unavailable, "The configured tool could not be inspected.", next); return null; }
        }
        foreach (var (id, tool) in new[] { ("ffmpeg", settings.Ffmpeg), ("ffprobe", settings.Ffprobe) })
        {
            var result = await Probe(id, id == "ffmpeg" ? "Audio encoder" : "Audio validator", tool, ["-version"], "Choose an approved executable in Settings, then check again.");
            if (result is not null)
            {
                var version = Regex.Match(result.Output.Split('\n')[0], "^" + id + @" version ([A-Za-z0-9._+-]{1,128})(?:\s|$)", RegexOptions.CultureInvariant);
                Add(id, id == "ffmpeg" ? "Audio encoder" : "Audio validator", version.Success ? SetupStatus.Available : SetupStatus.NeedsAttention,
                    version.Success ? $"Build {version.Groups[1].Value}. Full encode/decode acceptance remains separate." : "The configured executable responded without the expected tool identity.", version.Success ? "" : "Choose the correct approved executable in Settings.");
            }
        }
        var wsl = await Probe("wsl", "WSL", "wsl.exe", ["--version"], "Ask IT to verify the configured Linux backend; this check does not install or update Windows features.");
        if (wsl is not null)
        {
            var version = SafeVersion(wsl.Output.Split('\n')[0]);
            Add("wsl", "WSL", Version.TryParse(version, out var parsed) && parsed >= new Version(2, 1, 5) ? SetupStatus.Available : SetupStatus.NeedsAttention,
                $"Version {version ?? "could not be parsed"}. WSL 2.1.5 or newer is required for the WSL backend.", "Backend selection, feature state and BIOS virtualization require the approved setup check.");
        }
        var docker = await Probe("docker-cli", "Docker CLI", "docker", ["--version"], "Use the approved Docker Desktop setup; then reopen CommuteCast.");
        if (docker is not null) Add("docker-cli", "Docker CLI", SetupStatus.Available, $"Version {SafeVersion(docker.Output) ?? "unparsed; executable responded"}.");
        var local = false;
        if (docker is not null)
        {
            var context = await Probe("context", "Docker context", "docker", ["context", "inspect", "--format", "{{.Endpoints.docker.Host}}"], "Select the local Docker Desktop Linux context through the approved setup process.");
            if (context is not null) { local = LocalSpeechProvider.IsLocalContext(context.Output.Trim()); Add("context", "Docker context", local ? SetupStatus.Available : SetupStatus.NeedsAttention, local ? "The configured endpoint is the approved local Linux pipe." : "The context is remote, Windows-container or otherwise unsupported. Its address is withheld.", local ? "" : "Select the local Docker Desktop Linux context. Remote engines are not inspected."); }
        }
        var daemonReady = false;
        if (local)
        {
            var daemon = await Probe("daemon", "Local Docker engine", "docker", ["version", "--format", "{{.Server.Version}}"], host.DesktopRunning ? "Docker Desktop is present but its engine is unavailable. Inspect Docker Desktop's error and use the approved repair process." : "Open the installed Docker Desktop and wait for the Linux engine, then check again.");
            if (daemon is not null) { daemonReady = true; Add("daemon", "Local Docker engine", SetupStatus.Available, $"Version {SafeVersion(daemon.Output) ?? "unparsed; engine responded"}."); }
        }
        else Add("daemon", "Local Docker engine", SetupStatus.Unavailable, "Not inspected because the local CLI/context prerequisite did not pass.");
        foreach (var engine in new[] { "kokoro", "piper" })
        {
            var id = settings.Engine == engine ? "speech-selected" : "speech-" + engine;
            if (!daemonReady) { Add(id, engine + " speech", SetupStatus.Unavailable, "Not inspected because the local engine is unavailable."); continue; }
            try
            {
                using var serviceDeadline = CancellationTokenSource.CreateLinkedTokenSource(token); serviceDeadline.CancelAfter(TimeSpan.FromSeconds(5));
                var info = await runtime.ProbeSpeechAsync(engine, serviceDeadline.Token).WaitAsync(serviceDeadline.Token);
                var selected = engine == settings.Engine;
                var ready = info.State == "ready" && info.Active == 0 && (!selected || info.Voices.Contains(settings.Voice));
                Add(id, engine + " speech", ready ? SetupStatus.Available : SetupStatus.NeedsAttention, $"Verified owned service: {info.State}; active requests {info.Active}; {info.Voices.Length} available voices." + (selected && !info.Voices.Contains(settings.Voice) ? " Selected voice is unavailable." : ""), ready ? "Voice quality and throughput still require listening and workload acceptance." : "Allow loading/active work to finish, or choose an installed supported voice. Repair failed service setup before readiness/retry.");
            }
            catch (OperationCanceledException) { ct.ThrowIfCancellationRequested(); Add(id, engine + " speech", SetupStatus.TimedOut, "The bounded service inspection did not finish.", "Inspect Docker Desktop and repair the approved service setup."); }
            catch (Exception error) when (error is IOException or System.Net.Http.HttpRequestException or TimeoutException or Win32Exception)
            { var failure = SpeechFailure(error); Add(id, engine + " speech", failure.Status, failure.Detail + " No speech was requested and no service was started.", "Use the approved provisioning/repair process, then check speech readiness. Raw errors are withheld."); }
        }
        ct.ThrowIfCancellationRequested(); return new(DateTimeOffset.UtcNow, host, checks);
    }
    private static (SetupStatus Status, string Detail) SpeechFailure(Exception error) => error switch
    {
        IOException when error.Message.Contains("not provisioned", StringComparison.Ordinal) => (SetupStatus.Missing, "Speech provisioning has not been recorded."),
        IOException when error.Message.Contains("container is missing", StringComparison.Ordinal) => (SetupStatus.Missing, "The configured owned speech container is missing."),
        IOException when error.Message.Contains("container stopped", StringComparison.Ordinal) => (SetupStatus.Unavailable, "The verified owned speech container is stopped."),
        IOException when error.Message.Contains("ran out of memory", StringComparison.Ordinal) => (SetupStatus.NeedsAttention, "The speech container exhausted its memory allocation."),
        IOException when error.Message.Contains("paused or restarting", StringComparison.Ordinal) => (SetupStatus.NeedsAttention, "The owned speech container is paused or restarting."),
        System.Net.Http.HttpRequestException => (SetupStatus.Unavailable, "The verified loopback speech API is unavailable or unhealthy."),
        TimeoutException => (SetupStatus.TimedOut, "The read-only speech inspection timed out."),
        _ => (SetupStatus.NeedsAttention, "The owned image/configuration/provider pin or health identity could not be verified.")
    };
}
