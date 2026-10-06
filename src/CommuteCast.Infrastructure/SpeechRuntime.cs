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
            if (container.GetProperty("Name").GetString() != "/" + Name(engine) || container.GetProperty("Image").GetString() != image ||
                !labels.TryGetProperty("com.commutecast.owner", out var owner) || owner.GetString() != "CommuteCast" ||
                !labels.TryGetProperty("com.docker.compose.project", out var project) || project.GetString() != "commutecast" ||
                !labels.TryGetProperty("com.commutecast.contract", out var contract) || contract.GetString() != "1")
                throw new IOException("Container ownership, project, name, contract, or image identity does not match. Recovery was refused.");
            var host = container.GetProperty("HostConfig"); var ports = host.GetProperty("PortBindings");
            var expectedPort = engine == "kokoro" ? "8765" : "8766";
            if (ports.EnumerateObject().Count() != 1 || !ports.TryGetProperty("8765/tcp", out var binding) || binding.GetArrayLength() != 1 || binding[0].GetProperty("HostIp").GetString() != "127.0.0.1" || binding[0].GetProperty("HostPort").GetString() != expectedPort)
                throw new IOException("Speech port binding is not the approved loopback configuration.");
            var expectedCommand = new[] { "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8765", "--no-access-log" };
            var env = config.GetProperty("Env").EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.StartsWith("COMMUTECAST_ENGINE=", StringComparison.Ordinal)).ToArray();
            var entrypoint = config.GetProperty("Entrypoint");
            if (!config.GetProperty("Cmd").EnumerateArray().Select(v => v.GetString()).SequenceEqual(expectedCommand) ||
                (entrypoint.ValueKind != JsonValueKind.Null && entrypoint.GetArrayLength() != 0) || env.Length != 1 || env[0] != "COMMUTECAST_ENGINE=" + engine ||
                host.GetProperty("Privileged").GetBoolean() || host.GetProperty("NetworkMode").GetString() != "bridge" ||
                container.GetProperty("Mounts").GetArrayLength() != 0 || host.GetProperty("NanoCpus").GetInt64() != 2_000_000_000L ||
                host.GetProperty("Memory").GetInt64() != (engine == "kokoro" ? 2L : 1L) * 1024 * 1024 * 1024 ||
                !host.GetProperty("SecurityOpt").EnumerateArray().Any(v => v.GetString() == "no-new-privileges:true"))
                throw new IOException("Speech command, engine, mounts, network, resources, or privilege configuration differs from the approved manifest. Reprovision explicitly.");
            var state = container.GetProperty("State");
            if (state.GetProperty("OOMKilled").GetBoolean()) throw new IOException("The speech container ran out of memory. Repair its resource limit before reprovisioning or retrying; it was not restarted.");
            if (state.GetProperty("Paused").GetBoolean() || state.GetProperty("Restarting").GetBoolean()) throw new IOException("The owned speech container is paused or restarting. Repair its state, then retry.");
            return state.GetProperty("Running").GetBoolean();
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw new IOException("Docker returned incomplete or incompatible container metadata. No recovery or text request was permitted.", error);
        }
    }
}
