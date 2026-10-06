using CommuteCast.Core;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommuteCast.Infrastructure;

public sealed class LocalSpeechProvider : ISpeechProvider, IDisposable
{
    private readonly Workspace workspace;
    private readonly ILocalSpeechRuntime runtime;
    private readonly SpeechProviderLimits limits;
    private readonly HttpClient http;
    private readonly SemaphoreSlim recoveryGate = new(1);
    private const int MaximumAudioBytes = 64 * 1024 * 1024;
    public LocalSpeechProvider(Workspace workspace, ILocalSpeechRuntime? runtime = null, HttpMessageHandler? handler = null, SpeechProviderLimits? limits = null)
    {
        this.workspace = workspace; this.runtime = runtime ?? new LocalSpeechRuntime(); this.limits = limits ?? new(); this.limits.Validate();
        http = new(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    private static Uri Endpoint(string engine, string route) => new($"http://127.0.0.1:{(engine == "kokoro" ? 8765 : engine == "piper" ? 8766 : throw new ArgumentException("Unsupported engine."))}/{route}");
    public static bool IsLocalContext(string endpoint) => endpoint.Equals("npipe:////./pipe/dockerDesktopLinuxEngine", StringComparison.OrdinalIgnoreCase);
    public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => ReadyAsync(engine, ct, false);
    public async Task ResetRecoveryBudgetAsync(string engine, CancellationToken ct = default)
    {
        await recoveryGate.WaitAsync(ct);
        try { await new RecoveryBudget(workspace).ResetAsync(engine); }
        finally { recoveryGate.Release(); }
    }
    public async Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct, bool explicitRetry)
    {
        DockerContainerPolicy.Name(engine);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(limits.Readiness);
        var entered = false;
        try
        {
            await recoveryGate.WaitAsync(deadline.Token); entered = true;
            var budget = new RecoveryBudget(workspace);
            if (explicitRetry) await budget.ResetAsync(engine);
            await budget.BeginAsync(engine, deadline.Token);
            await VerifyContainerAsync(engine, true, deadline.Token);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    var info = await HealthAsync(engine, deadline.Token);
                    if (info.State == "ready" && info.Active == 0) { await budget.ResetAsync(engine); return info; }
                    if (info.State == "failed") throw new IOException("The model could not load. Check available memory and reprovision the speech service.");
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(limits.Poll, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Speech readiness exceeded its bounded allowance. Repair Docker Desktop, then check readiness or retry. Saved audio is retained."); }
        catch (TimeoutException error) { throw new TimeoutException(error.Message + " Repair Docker Desktop, then check speech readiness or retry. Validated local audio is preserved.", error); }
        finally { if (entered) recoveryGate.Release(); }
    }
    private async Task VerifyContainerAsync(string engine, bool startIfStopped, CancellationToken ct)
    {
        var lockPath = Path.Combine(workspace.Root, "provider-lock.local.json");
        if (!File.Exists(lockPath)) throw new IOException("Speech services are not provisioned. Run scripts/Provision-Speech.ps1 -Build once, then check readiness.");
        string image;
        try
        {
            using var pinned = JsonDocument.Parse(await File.ReadAllTextAsync(lockPath, ct));
            image = pinned.RootElement.GetProperty("ImageId").GetString() ?? "";
            if (pinned.RootElement.GetProperty("Contract").GetInt32() != 1 || !Regex.IsMatch(image, "^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new IOException("The locally pinned speech contract/image is incompatible. Reprovision explicitly.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { throw new IOException("The local provider pin is unreadable. Reprovision explicitly; no text has been sent.", error); }
        var context = await runtime.DockerAsync(["context", "inspect", "--format", "{{.Endpoints.docker.Host}}"], TimeSpan.FromSeconds(30), ct);
        if (context.ExitCode != 0 || !IsLocalContext(context.Output.Trim())) throw new IOException("Select the local Docker Desktop Linux context. Remote Docker engines are not permitted.");
        var daemon = await runtime.DockerAsync(["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(15), ct);
        if (daemon.ExitCode != 0 && startIfStopped)
        {
            runtime.LaunchInstalledDesktop();
            for (var attempt = 0; attempt < 10 && daemon.ExitCode != 0; attempt++)
            {
                await Task.Delay(limits.Poll, ct);
                daemon = await runtime.DockerAsync(["version", "--format", "{{.Server.Version}}"], TimeSpan.FromSeconds(5), ct);
            }
        }
        if (daemon.ExitCode != 0) throw new IOException("Docker's local daemon is unavailable. Open Docker Desktop and retry.");
        var result = await runtime.DockerAsync(["inspect", DockerContainerPolicy.Name(engine)], TimeSpan.FromSeconds(30), ct);
        if (result.ExitCode != 0) throw new IOException("The configured CommuteCast container is missing. Re-run the provisioning script; no other containers were changed.");
        if (!DockerContainerPolicy.Evaluate(result.Output, engine, image))
        {
            if (!startIfStopped) throw new IOException("The speech container stopped; retry to resume.");
            var start = await runtime.DockerAsync(["start", DockerContainerPolicy.Name(engine)], TimeSpan.FromSeconds(20), ct);
            if (start.ExitCode != 0) throw new IOException("The owned speech service could not start. Check for a port conflict or insufficient resources.");
            var started = await runtime.DockerAsync(["inspect", DockerContainerPolicy.Name(engine)], TimeSpan.FromSeconds(30), ct);
            if (started.ExitCode != 0 || !DockerContainerPolicy.Evaluate(started.Output, engine, image)) throw new IOException("The owned speech service did not remain running. Repair it, then retry.");
        }
    }
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var pending = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        try { return await pending.WaitAsync(ct); }
        catch (OperationCanceledException)
        {
            _ = pending.ContinueWith(t => { if (t.Status == TaskStatus.RanToCompletion) t.Result.Dispose(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            throw;
        }
    }
    private async Task<ProviderInfo> HealthAsync(string engine, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(limits.Health);
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint(engine, "health"));
        using var response = await SendAsync(request, timeout.Token); response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentLength > 65536) throw new IOException("Speech health returned an invalid type or size. No text has been sent.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token); using var bytes = new MemoryStream();
        await CopyBoundedAsync(stream, bytes, 65536, timeout.Token);
        try
        {
            using var data = JsonDocument.Parse(bytes.ToArray()); var root = data.RootElement;
            if (root.GetProperty("service").GetString() != "CommuteCast" || root.GetProperty("contract").GetInt32() != 1 || root.GetProperty("engine").GetString() != engine) throw new IOException("Unexpected service on the speech port. No text has been sent.");
            var state = root.GetProperty("state").GetString() ?? ""; var fingerprint = root.GetProperty("fingerprint").GetString() ?? "";
            var active = root.GetProperty("active").GetInt32(); var voices = root.GetProperty("voices").EnumerateArray().Select(v => v.GetString() ?? "").ToArray();
            if (state is not ("ready" or "loading" or "failed") || active is < 0 or > 1 || voices.Length > 256 || voices.Distinct().Count() != voices.Length || voices.Any(v => !Regex.IsMatch(v, "^[a-zA-Z0-9_-]{1,80}$", RegexOptions.CultureInvariant))) throw new IOException("Speech health contains incompatible state or voice metadata. No text has been sent.");
            if (state == "ready" && (voices.Length == 0 || !Regex.IsMatch(fingerprint, "^" + engine + ":contract-v1:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))) throw new IOException("Speech model fingerprint or voice inventory is invalid. No text has been sent.");
            return new(engine, fingerprint, voices, state, active);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { throw new IOException("Speech health returned incomplete or incompatible metadata. No text has been sent.", error); }
    }
    private static async Task<long> CopyBoundedAsync(Stream input, Stream output, int maximum, CancellationToken ct)
    {
        var buffer = new byte[81920]; long total = 0; int read;
        while (true)
        {
            try { read = await input.ReadAsync(buffer, ct).AsTask().WaitAsync(ct); }
            catch (IOException error) { throw new HttpRequestException("The local speech connection was interrupted while reading audio.", error); }
            if (read == 0) break;
            total += read;
            if (total > maximum) throw new IOException("The speech response exceeded its permitted size.");
            await output.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return total;
    }
    public async Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > 900 || settings.Speed is < .7 or > 1.4 || !double.IsFinite(settings.Speed)) throw new ArgumentException("Choose valid text within the 900-character provider limit and a supported speaking pace.");
        output = Path.GetFullPath(output);
        if (!Workspace.IsWithin(workspace.Root, output)) throw new IOException("Speech output must remain in the private workspace.");
        Workspace.RejectReparsePoints(Path.GetDirectoryName(output)!);
        if (File.Exists(output) && File.GetAttributes(output).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Speech output is a symbolic link. Generation was refused.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(limits.Synthesis);
        var temporary = output + ".attempt-" + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    await VerifyContainerAsync(settings.Engine, false, deadline.Token);
                    var info = await HealthAsync(settings.Engine, deadline.Token);
                    if (info.Fingerprint != settings.ProviderFingerprint || info.State != "ready" || !info.Voices.Contains(settings.Voice)) throw new IOException("The model or voice differs from this job's frozen configuration. Restore the original service or submit a new job.");
                    while (info.Active != 0)
                    {
                        await Task.Delay(limits.Poll, deadline.Token); info = await HealthAsync(settings.Engine, deadline.Token);
                        if (info.State != "ready" || info.Fingerprint != settings.ProviderFingerprint || !info.Voices.Contains(settings.Voice)) throw new IOException("Speech identity, voice, or readiness changed while waiting. Restore the configured service, then retry.");
                    }
                    using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(settings.Engine, "speech")) { Content = JsonContent.Create(new { text, voice = settings.Voice, speed = settings.Speed, fingerprint = settings.ProviderFingerprint }) };
                    using var response = await SendAsync(request, deadline.Token);
                    if ((int)response.StatusCode is 429 or 500 or 502 or 503 or 504) throw new HttpRequestException("The local speech service returned a transient failure.", null, response.StatusCode);
                    if (!response.IsSuccessStatusCode) throw new IOException($"Local speech returned {(int)response.StatusCode}. Validated chunks are preserved; repair settings or service readiness and retry.");
                    if (response.Content.Headers.ContentType?.MediaType != "audio/wav" || response.Content.Headers.ContentLength > MaximumAudioBytes) throw new IOException("The speech response has an invalid type or size.");
                    await using (var stream = await response.Content.ReadAsStreamAsync(deadline.Token))
                    await using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                    {
                        var received = await CopyBoundedAsync(stream, file, MaximumAudioBytes, deadline.Token);
                        if (received < 44 || response.Content.Headers.ContentLength is { } declared && declared != received) throw new IOException("The speech response is truncated or its declared size is inconsistent.");
                        await file.FlushAsync(deadline.Token);
                    }
                    WaveAudio.DataRegion(temporary, false);
                    deadline.Token.ThrowIfCancellationRequested();
                    File.Move(temporary, output, true);
                    return;
                }
                catch (HttpRequestException) when (attempt < limits.TransientRetries && !deadline.IsCancellationRequested)
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                    await Task.Delay(TimeSpan.FromTicks(limits.RetryBackoff.Ticks * (1L << attempt)), deadline.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Speech synthesis exceeded its five-minute request/retry budget. Validated chunks are retained; check the local service and retry."); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose() { http.Dispose(); recoveryGate.Dispose(); }
}
