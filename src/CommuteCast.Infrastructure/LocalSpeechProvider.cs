using CommuteCast.Core;
using System.Net.Http.Json;
using System.Text.Json;
using System.Diagnostics;

namespace CommuteCast.Infrastructure;

public sealed class LocalSpeechProvider(Workspace workspace) : ISpeechProvider, IDisposable
{
    private readonly HttpClient http = new(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
    private readonly SemaphoreSlim recoveryGate = new(1);
    private static string Container(string engine) => engine switch { "kokoro" => "commutecast-kokoro", "piper" => "commutecast-piper", _ => throw new ArgumentException("Choose Kokoro or Piper.") };
    private static Uri Endpoint(string engine, string route) => new($"http://127.0.0.1:{(engine == "kokoro" ? 8765 : engine == "piper" ? 8766 : throw new ArgumentException("Unsupported engine."))}/{route}");

    public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => ReadyAsync(engine, ct, false);
    public async Task ResetRecoveryBudgetAsync(string engine, CancellationToken ct = default)
    {
        await recoveryGate.WaitAsync(ct);
        try { await new RecoveryBudget(workspace).ResetAsync(engine); }
        finally { recoveryGate.Release(); }
    }
    public async Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct, bool explicitRetry)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(120));
        await recoveryGate.WaitAsync(deadline.Token);
        try
        {
            var budget = new RecoveryBudget(workspace);
            if (explicitRetry) await budget.ResetAsync(engine);
            await budget.BeginAsync(engine, deadline.Token);
            await VerifyContainerAsync(engine, true, deadline.Token);
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(110))
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    var info = await HealthAsync(engine, deadline.Token);
                    if (info.State == "ready" && info.Active == 0) { await budget.ResetAsync(engine); return info; }
                    if (info.State == "failed") throw new IOException("The model could not load. Check available memory and reprovision the speech service.");
                    // Loading and active inference are waited on, never restarted.
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(1500, deadline.Token);
            }
            throw new TimeoutException("Local speech did not become ready within two minutes. Validated audio is preserved; repair Docker or retry.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Speech readiness exceeded its two-minute limit. Check Docker Desktop, then retry."); }
        catch (TimeoutException error) { throw new TimeoutException(error.Message + " Repair Docker Desktop, then check speech readiness or retry. Validated local audio is preserved.", error); }
        finally { recoveryGate.Release(); }
    }

    private async Task VerifyContainerAsync(string engine, bool startIfStopped, CancellationToken ct)
    {
        var lockPath = Path.Combine(workspace.Root, "provider-lock.local.json");
        if (!File.Exists(lockPath)) throw new IOException("Speech services are not provisioned. Run scripts/Provision-Speech.ps1 -Build once, then check readiness.");
        using var pinned = JsonDocument.Parse(await File.ReadAllTextAsync(lockPath, ct));
        var image = pinned.RootElement.GetProperty("ImageId").GetString();
        var context = await ProcessRunner.RunAsync("docker", ["context", "inspect", "--format", "{{.Endpoints.docker.Host}}"], TimeSpan.FromSeconds(30), ct);
        if (context.ExitCode != 0 || !IsLocalContext(context.Output.Trim())) throw new IOException("Select the local Docker Desktop Linux context. Remote Docker engines are not permitted.");
        var daemon = await ProcessRunner.RunAsync("docker", ["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(15), ct);
        if (daemon.ExitCode != 0 && startIfStopped)
        {
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Docker", "Docker", "Docker Desktop.exe");
            if (!File.Exists(desktop)) throw new IOException("Docker Desktop is not installed. Install it through your approved IT process.");
            Process.Start(new ProcessStartInfo(desktop) { UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            for (var attempt = 0; attempt < 10 && daemon.ExitCode != 0; attempt++)
            {
                await Task.Delay(3000, ct);
                daemon = await ProcessRunner.RunAsync("docker", ["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(5), ct);
            }
        }
        if (daemon.ExitCode != 0) throw new IOException("Docker's local daemon is unavailable. Open Docker Desktop and retry.");
        var result = await ProcessRunner.RunAsync("docker", ["inspect", Container(engine)], TimeSpan.FromSeconds(30), ct);
        if (result.ExitCode != 0) throw new IOException("The configured CommuteCast container is missing. Re-run the provisioning script; no other containers were changed.");
        using var document = JsonDocument.Parse(result.Output);
        var container = document.RootElement[0];
        var labels = container.GetProperty("Config").GetProperty("Labels");
        if (container.GetProperty("Image").GetString() != image ||
            !labels.TryGetProperty("com.commutecast.owner", out var owner) || owner.GetString() != "CommuteCast" ||
            !labels.TryGetProperty("com.docker.compose.project", out var project) || project.GetString() != "commutecast" ||
            !labels.TryGetProperty("com.commutecast.contract", out var contract) || contract.GetString() != "1")
            throw new IOException("Container ownership, project, contract, or image identity does not match. Recovery was refused.");
        var expectedPort = engine == "kokoro" ? "8765" : "8766";
        var ports = container.GetProperty("HostConfig").GetProperty("PortBindings");
        if (!ports.TryGetProperty("8765/tcp", out var binding) || binding.GetArrayLength() != 1 || binding[0].GetProperty("HostIp").GetString() != "127.0.0.1" || binding[0].GetProperty("HostPort").GetString() != expectedPort)
            throw new IOException("Speech port binding is not the approved loopback configuration.");
        if (!container.GetProperty("State").GetProperty("Running").GetBoolean())
        {
            if (container.GetProperty("State").TryGetProperty("OOMKilled", out var oom) && oom.GetBoolean()) throw new IOException("The speech container ran out of memory. Repair the resource limit before reprovisioning or retrying; it was not restarted.");
            if (!startIfStopped) throw new IOException("The speech container stopped; retry to resume.");
            var start = await ProcessRunner.RunAsync("docker", ["start", Container(engine)], TimeSpan.FromSeconds(20), ct);
            if (start.ExitCode != 0) throw new IOException("The owned speech service could not start. Check for a port conflict or insufficient resources.");
        }
    }

    private async Task<ProviderInfo> HealthAsync(string engine, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var response = await http.GetAsync(Endpoint(engine, "health"), timeout.Token);
        response.EnsureSuccessStatusCode();
        using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = data.RootElement;
        if (root.GetProperty("service").GetString() != "CommuteCast" || root.GetProperty("contract").GetInt32() != 1 || root.GetProperty("engine").GetString() != engine)
            throw new IOException("Unexpected service on the speech port. No text has been sent.");
        if (root.GetProperty("state").GetString() == "ready" && !System.Text.RegularExpressions.Regex.IsMatch(root.GetProperty("fingerprint").GetString() ?? "", "^" + engine + ":contract-v1:[a-fA-F0-9]{64}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new IOException("Speech model fingerprint is invalid. No text has been sent.");
        return new(engine, root.GetProperty("fingerprint").GetString()!, root.GetProperty("voices").EnumerateArray().Select(v => v.GetString()!).ToArray(), root.GetProperty("state").GetString()!, root.GetProperty("active").GetInt32());
    }
    public static bool IsLocalContext(string endpoint) => endpoint.Equals("npipe:////./pipe/dockerDesktopLinuxEngine", StringComparison.OrdinalIgnoreCase);

    public async Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct)
    {
        await VerifyContainerAsync(settings.Engine, false, ct);
        var info = await HealthAsync(settings.Engine, ct);
        if (info.Fingerprint != settings.ProviderFingerprint || info.State != "ready" || !info.Voices.Contains(settings.Voice)) throw new IOException("The model or voice differs from this job's frozen configuration. Restore the original service or submit a new job.");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(settings.Engine, "speech")) { Content = JsonContent.Create(new { text, voice = settings.Voice, speed = settings.Speed, fingerprint = settings.ProviderFingerprint }) };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromMinutes(5));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
        if (!response.IsSuccessStatusCode) throw new IOException($"Local speech returned {(int)response.StatusCode}. Validated chunks are preserved; wait for service readiness and retry.");
        if (response.Content.Headers.ContentType?.MediaType != "audio/wav" || response.Content.Headers.ContentLength > 64 * 1024 * 1024) throw new IOException("The speech response has an invalid type or size.");
        await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
        await using (var file = new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920]; long total = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                total += read;
                if (total > 64 * 1024 * 1024) throw new IOException("The speech response exceeded its 64MB limit.");
                await file.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            }
            if (total < 44) throw new IOException("The speech response is truncated.");
        }
        await using var checkedFile = File.OpenRead(output);
        var header = new byte[12]; await checkedFile.ReadExactlyAsync(header, ct);
        if (System.Text.Encoding.ASCII.GetString(header, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(header, 8, 4) != "WAVE") throw new IOException("The service returned compressed or invalid audio instead of lossless WAV.");
    }
    public void Dispose() { http.Dispose(); recoveryGate.Dispose(); }
}
