using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommuteCast.Infrastructure;

public interface ILocalSpeechRuntime
{
    Task<ProcessResult> DockerAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct);
    void LaunchInstalledDesktop();
}

public sealed class LocalSpeechRuntime : ILocalSpeechRuntime
{
    public Task<ProcessResult> DockerAsync(IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct) => ProcessRunner.RunAsync("docker", arguments, timeout, ct);
    public void LaunchInstalledDesktop()
    {
        var desktop = DockerDesktopLocation.Find() ?? throw new IOException("Docker Desktop is not installed. Install it through your approved IT process.");
        Process.Start(new ProcessStartInfo(desktop) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
    }
}

public static class DockerDesktopLocation
{
    public static string? Find(string? localApplicationData = null, string? programFiles = null)
    {
        var candidates = new[] {
            Path.Combine(localApplicationData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "DockerDesktop", "Docker Desktop.exe"),
            Path.Combine(programFiles ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe") };
        foreach (var candidate in candidates)
            if (File.Exists(candidate)) { SqliteSchema.RejectLink(candidate); return Path.GetFullPath(candidate); }
        return null;
    }
}

public sealed record SpeechProviderLimits
{
    public TimeSpan Readiness { get; init; } = TimeSpan.FromSeconds(120);
    public TimeSpan Health { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan Synthesis { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan Quiescence { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan Poll { get; init; } = TimeSpan.FromMilliseconds(1500);
    public TimeSpan RetryBackoff { get; init; } = TimeSpan.FromSeconds(1);
    public int TransientRetries { get; init; } = 2;
    public void Validate()
    {
        if (Readiness <= TimeSpan.Zero || Readiness > TimeSpan.FromSeconds(120) || Health <= TimeSpan.Zero || Health > TimeSpan.FromSeconds(5) || Synthesis <= TimeSpan.Zero || Synthesis > TimeSpan.FromMinutes(5) || Quiescence <= TimeSpan.Zero || Quiescence > TimeSpan.FromSeconds(8) || Poll <= TimeSpan.Zero || RetryBackoff <= TimeSpan.Zero || TransientRetries is < 0 or > 2)
            throw new ArgumentException("Provider limits must remain within the approved recovery/request budgets.");
    }
}

public static class DockerContainerPolicy
{
    public static string Name(string engine) => engine switch { "kokoro" => "commutecast-kokoro", "piper" => "commutecast-piper", _ => throw new ArgumentException("Choose Kokoro or Piper.") };
    public static bool Evaluate(string json, string engine, string image)
    {
        try
        {
            if (!Regex.IsMatch(image, "^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new IOException("The pinned speech image identity is invalid. Reprovision the service.");
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1) throw new IOException("Docker did not return one configured speech container.");
            var container = document.RootElement[0]; var config = container.GetProperty("Config"); var labels = config.GetProperty("Labels");
            if (container.GetProperty("Name").GetString() != "/" + Name(engine))
                throw new SpeechSetupException("The speech container has an unexpected name.", "Ask your administrator to inspect the container named " + Name(engine) + ".");
            if (!labels.TryGetProperty("com.commutecast.owner", out var owner) || owner.GetString() != "CommuteCast")
                throw new SpeechSetupException("The container is not identified as owned by CommuteCast.", "Ask your administrator to inspect " + Name(engine) + "; repair cannot replace a container belonging to another application.");
            if (!labels.TryGetProperty("com.docker.compose.project", out var project) || project.GetString() != "commutecast")
                throw new SpeechSetupException("The container belongs to a different Docker Compose project.", "Ask your administrator to inspect " + Name(engine) + "; its Compose project must be commutecast.");
            if (!labels.TryGetProperty("com.commutecast.contract", out var contract) || contract.GetString() != "1")
                throw new SpeechSetupException("The installed speech service contract is incompatible with this application.", "Update the speech services using the provisioning steps below.");
            var host = container.GetProperty("HostConfig"); var ports = host.GetProperty("PortBindings");
            var expectedPort = engine == "kokoro" ? "8765" : "8766";
            if (ports.EnumerateObject().Count() != 1 || !ports.TryGetProperty("8765/tcp", out var binding) || binding.GetArrayLength() != 1 || binding[0].GetProperty("HostIp").GetString() != "127.0.0.1" || binding[0].GetProperty("HostPort").GetString() != expectedPort)
                throw new SpeechSetupException("Speech port binding is not the approved loopback configuration.", "The service must publish only 127.0.0.1:" + expectedPort + " to container port 8765. Ask your administrator to reprovision it; automatic repair preserves the existing container.");
            var expectedCommand = new[] { "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8765", "--no-access-log" };
            var env = config.GetProperty("Env").EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.StartsWith("COMMUTECAST_ENGINE=", StringComparison.Ordinal)).ToArray();
            var entrypoint = config.GetProperty("Entrypoint");
            var mismatches = new List<string>();
            if (!config.GetProperty("Cmd").EnumerateArray().Select(v => v.GetString()).SequenceEqual(expectedCommand)) mismatches.Add("startup command");
            if (entrypoint.ValueKind != JsonValueKind.Null && entrypoint.GetArrayLength() != 0) mismatches.Add("entrypoint");
            if (env.Length != 1 || env[0] != "COMMUTECAST_ENGINE=" + engine) mismatches.Add("engine selection");
            if (host.GetProperty("Privileged").GetBoolean()) mismatches.Add("privileged mode");
            if (host.GetProperty("NetworkMode").GetString() != "bridge") mismatches.Add("network mode");
            if (container.GetProperty("Mounts").GetArrayLength() != 0) mismatches.Add("host mounts");
            if (host.GetProperty("NanoCpus").GetInt64() != 2_000_000_000L) mismatches.Add("CPU limit (requires 2 CPUs)");
            if (host.GetProperty("Memory").GetInt64() != (engine == "kokoro" ? 2L : 1L) * 1024 * 1024 * 1024) mismatches.Add("memory limit (requires " + (engine == "kokoro" ? "2" : "1") + " GiB)");
            if (!host.GetProperty("SecurityOpt").EnumerateArray().Any(v => v.GetString() == "no-new-privileges:true")) mismatches.Add("no-new-privileges setting");
            if (mismatches.Count > 0)
                throw new SpeechSetupException("Speech configuration differs from the approved setup: " + string.Join(", ", mismatches) + ".", "Ask your administrator to reprovision this service. Automatic repair preserves containers with changed configuration.");
            var state = container.GetProperty("State");
            if (state.GetProperty("OOMKilled").GetBoolean()) throw new SpeechSetupException("The speech container ran out of memory.", "Free memory on this laptop, check Docker Desktop's memory allocation, then ask your administrator to reprovision the service. It was not restarted.");
            if (state.GetProperty("Paused").GetBoolean() || state.GetProperty("Restarting").GetBoolean()) throw new SpeechSetupException("The owned speech container is paused or restarting.", "In Docker Desktop, resume the paused CommuteCast service or let its restart finish, then choose Start / repair speech services.");
            if (container.GetProperty("Image").GetString() != image)
                throw new SpeechSetupException("The installed speech image differs from the image saved in this workspace.", "Choose Start / repair speech services to reconcile verified, idle installed services. If an older narration needs to resume, restore its original image instead.", imageMismatch: true);
            return state.GetProperty("Running").GetBoolean();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new IOException("Docker returned incomplete or incompatible container metadata. No recovery or text request was permitted.", error);
        }
    }
}

/// <summary>Only application-authored setup explanations may appear in the read-only setup report.</summary>
public sealed class SpeechSetupException(string explanation, string nextStep, bool imageMismatch = false) : IOException(
    explanation + "\n" + nextStep + "\n" + SpeechRepairGuidance.Provisioning)
{
    public string Explanation { get; } = explanation;
    public string ImmediateStep { get; } = nextStep;
    public string NextStep { get; } = nextStep + "\n" + SpeechRepairGuidance.Provisioning;
    public bool ImageMismatch { get; } = imageMismatch;
}

public static class SpeechRepairGuidance
{
    public const string Provisioning = "Manual setup:\n1. Pause future jobs and wait for current speech to finish.\n2. Open Docker Desktop and select Linux containers.\n3. In PowerShell, open the CommuteCast source folder. Run .\\scripts\\Provision-Speech.ps1 to use the installed image, or .\\scripts\\Provision-Speech.ps1 -Build if the image is missing or needs updating (downloads required). If you only have the installed app, ask your administrator to provision the matching speech services.\n4. Return to CommuteCast and choose Start / repair speech services.\nFinished MP3s remain available. Unfinished narrations made with another image require the original service or Use as a new draft.";
}
