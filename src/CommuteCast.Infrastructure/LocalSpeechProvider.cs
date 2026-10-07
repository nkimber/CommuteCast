using CommuteCast.Core;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommuteCast.Infrastructure;

public sealed class LocalSpeechProvider : IDurableSpeechProvider, IDurableAuditionSpeechProvider, IDisposable
{
    private readonly Workspace workspace;
    private readonly ILocalSpeechRuntime runtime;
    private readonly SpeechProviderLimits limits;
    private readonly HttpClient http;
    private readonly SemaphoreSlim recoveryGate = new(1);
    private readonly SemaphoreSlim admissionGate = new(1);
    private const int MaximumAudioBytes = 64 * 1024 * 1024;
    public LocalSpeechProvider(Workspace workspace, ILocalSpeechRuntime? runtime = null, HttpMessageHandler? handler = null, SpeechProviderLimits? limits = null)
    {
        this.workspace = workspace; this.runtime = runtime ?? new LocalSpeechRuntime(); this.limits = limits ?? new(); this.limits.Validate();
        http = new(handler ?? new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    private static Uri Endpoint(string engine, string route) => new($"http://127.0.0.1:{(engine == "kokoro" ? 8765 : engine == "piper" ? 8766 : throw new ArgumentException("Unsupported engine."))}/{route}");
    public static bool IsLocalContext(string endpoint) => endpoint.Equals("npipe:////./pipe/dockerDesktopLinuxEngine", StringComparison.OrdinalIgnoreCase);
    public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => ReadyAsync(engine, ct, false);
    /// <summary>Inspect the configured owned service without spending recovery allowance or starting anything.</summary>
    public async Task<ProviderInfo> ProbeAsync(string engine, CancellationToken ct = default)
    {
        try { return await ProbeIdentityAsync(engine, ct); }
        catch (ServiceUnavailableException error) { throw new IOException(error.Message, error); }
    }
    private async Task<ProviderInfo> ProbeIdentityAsync(string engine, CancellationToken ct)
    {
        var image = await VerifyContainerAsync(engine, false, ct);
        var info = await HealthAsync(engine, ct);
        await RequireUnchangedPinAsync(image, ct);
        return info with { ImageId = image };
    }
    /// <summary>Capture immutable metadata without waiting for active inference when a compatible identity is known.</summary>
    public async Task<ProviderInfo> CaptureForSubmissionAsync(string engine, ProviderInfo? cached, CancellationToken ct = default)
    {
        DockerContainerPolicy.Name(engine);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(limits.Readiness);
        try
        {
            var image = await ReadPinnedImageAsync(deadline.Token);
            if (cached is not null && cached.ImageId == image && IsCaptureMetadataValid(cached, engine))
                return cached with { Voices = cached.Voices.ToArray() };
            ProviderInfo info;
            try { info = await ProbeIdentityAsync(engine, deadline.Token); }
            catch (Exception error) when (error is ServiceUnavailableException or HttpRequestException || error is OperationCanceledException && !deadline.IsCancellationRequested)
            { info = await ReadyAsync(engine, deadline.Token); }
            if (info.State != "ready") info = await ReadyAsync(engine, deadline.Token);
            await RequireUnchangedPinAsync(image, deadline.Token);
            if (info.ImageId != image || !IsCaptureMetadataValid(info, engine)) throw new IOException("Speech identity changed during configuration capture. Check readiness and submit again; your draft is retained.");
            return info with { Voices = info.Voices.ToArray() };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Speech configuration capture exceeded its bounded allowance. Check readiness and submit again; your draft is retained."); }
    }
    private static bool IsCaptureMetadataValid(ProviderInfo info, string engine) => info.Engine == engine && info.State == "ready" && info.Active is >= 0 and <= 1 &&
        info.Fingerprint is not null && Regex.IsMatch(info.Fingerprint, "^" + engine + ":contract-v1:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant) &&
        info.Voices is { Length: > 0 and <= 256 } && info.Voices.Distinct().Count() == info.Voices.Length &&
        info.Voices.All(v => v is not null && Regex.IsMatch(v, "^[a-zA-Z0-9_-]{1,80}$", RegexOptions.CultureInvariant));
    private class ServiceUnavailableException(string message) : IOException(message);
    private sealed class OwnedServiceStoppedException() : ServiceUnavailableException("The verified owned speech container stopped; retry to resume.");
    private async Task RequireUnchangedPinAsync(string image, CancellationToken ct)
    {
        if (await ReadPinnedImageAsync(ct) != image) throw new IOException("The speech image pin changed during configuration verification. Check readiness and submit again; saved jobs and draft are retained.");
    }
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
        var entered = false; var admissionEntered = false; var settlingAdmission = false;
        try
        {
            await admissionGate.WaitAsync(deadline.Token); admissionEntered = true;
            settlingAdmission = true; await ReconcileAdmissionAsync(deadline.Token); settlingAdmission = false;
            await recoveryGate.WaitAsync(deadline.Token); entered = true;
            var budget = new RecoveryBudget(workspace);
            if (explicitRetry) await budget.ResetAsync(engine);
            await budget.BeginAsync(engine, deadline.Token);
            var image = await VerifyContainerAsync(engine, true, deadline.Token);
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    var info = await HealthAsync(engine, deadline.Token);
                    if (info.State == "ready" && info.Active == 0) { await RequireUnchangedPinAsync(image, deadline.Token); await budget.ResetAsync(engine); return info with { ImageId = image }; }
                    if (info.State == "failed") throw new IOException("The model could not load. Check available memory and reprovision the speech service.");
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(limits.Poll, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Speech readiness exceeded its bounded allowance. Repair Docker Desktop, then check readiness or retry. Saved audio is retained."); }
        catch (TimeoutException error) when (!settlingAdmission) { throw new TimeoutException(error.Message + " Repair Docker Desktop, then check speech readiness or retry. Validated local audio is preserved.", error); }
        finally { if (entered) recoveryGate.Release(); if (admissionEntered) admissionGate.Release(); }
    }
    private async Task<string> ReadPinnedImageAsync(CancellationToken ct)
    {
        var lockPath = Path.Combine(workspace.Root, "provider-lock.local.json");
        SqliteSchema.RejectLink(lockPath);
        if (!File.Exists(lockPath)) throw new IOException("Speech services are not provisioned. Run scripts/Provision-Speech.ps1 -Build once, then check readiness.");
        if (new FileInfo(lockPath).Length > 65536) throw new IOException("The local provider pin exceeds its safe size. Reprovision explicitly; no text has been sent.");
        string image;
        try
        {
            using var pinned = JsonDocument.Parse(await File.ReadAllTextAsync(lockPath, ct));
            image = pinned.RootElement.GetProperty("ImageId").GetString() ?? "";
            if (pinned.RootElement.GetProperty("Contract").GetInt32() != 1 || !Regex.IsMatch(image, "^sha256:[a-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new IOException("The locally pinned speech contract/image is incompatible. Reprovision explicitly.");
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { throw new IOException("The local provider pin is unreadable. Reprovision explicitly; no text has been sent.", error); }
        return image;
    }
    private async Task<string> VerifyContainerAsync(string engine, bool startIfStopped, CancellationToken ct)
    {
        var image = await ReadPinnedImageAsync(ct);
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
        if (daemon.ExitCode != 0)
        {
            const string message = "Docker's local daemon is unavailable. Open Docker Desktop and retry.";
            if (!startIfStopped) throw new ServiceUnavailableException(message);
            throw new IOException(message);
        }
        var result = await runtime.DockerAsync(["inspect", DockerContainerPolicy.Name(engine)], TimeSpan.FromSeconds(30), ct);
        if (result.ExitCode != 0) throw new IOException("The configured CommuteCast container is missing. Re-run the provisioning script; no other containers were changed.");
        if (!DockerContainerPolicy.Evaluate(result.Output, engine, image))
        {
            if (!startIfStopped) { await RequireUnchangedPinAsync(image, ct); throw new OwnedServiceStoppedException(); }
            var start = await runtime.DockerAsync(["start", DockerContainerPolicy.Name(engine)], TimeSpan.FromSeconds(20), ct);
            if (start.ExitCode != 0) throw new IOException("The owned speech service could not start. Check for a port conflict or insufficient resources.");
            var started = await runtime.DockerAsync(["inspect", DockerContainerPolicy.Name(engine)], TimeSpan.FromSeconds(30), ct);
            if (started.ExitCode != 0 || !DockerContainerPolicy.Evaluate(started.Output, engine, image)) throw new IOException("The owned speech service did not remain running. Repair it, then retry.");
        }
        await RequireUnchangedPinAsync(image, ct);
        return image;
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
            string? instance = null; long? sequence = null;
            if (root.TryGetProperty("admission", out var admission))
            {
                instance = root.GetProperty("instance").GetString(); sequence = root.GetProperty("sequence").GetInt64();
                if (admission.GetInt32() != 1 || instance is null || !Regex.IsMatch(instance, "^[a-f0-9]{32}$") || sequence is < 0 or >= 9007199254740991)
                    throw new IOException("Speech reservation metadata is incompatible. Reprovision the local service explicitly.");
            }
            return new(engine, fingerprint, voices, state, active, InstanceId: instance, AdmissionSequence: sequence);
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
    public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct) =>
        SynthesizeWithJournalAsync(null, settings, text, output, null, ct);

    public Task SynthesizeAsync(Job job, NarrationSettings settings, string text, string output, Func<Task> checkpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        PrivateJobFiles.Inventory(job);
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(output)), workspace.JobDirectory(job.Id), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Durable speech output must stay in its own job directory.");
        return SynthesizeWithJournalAsync(job, settings, text, output, checkpoint, ct);
    }

    public Task SynthesizeAuditionAsync(AuditionWriteJournal journal, NarrationSettings settings, string text, string output, Func<Task> checkpoint, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(checkpoint); AuditionOwnershipStore.Validate(journal);
        if (!string.Equals(Path.GetFullPath(output), OwnedFileRemoval.Resolve(workspace.Root, "auditions/audition-" + journal.Id + ".wav"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Durable preview output must stay in its own private audition path.");
        return SynthesizeWithJournalAsync(new Job { Id = journal.Id, PrivateArtifacts = journal.Artifacts }, settings, text, output, checkpoint, ct, true);
    }

    private async Task SynthesizeWithJournalAsync(Job? job, NarrationSettings settings, string text, string output, Func<Task>? checkpoint, CancellationToken ct, bool retainIdentity = false)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(limits.Synthesis);
        var entered = false;
        try
        {
            await admissionGate.WaitAsync(deadline.Token); entered = true;
            await SynthesizeCoreAsync(job, settings, text, output, checkpoint, deadline.Token, retainIdentity);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Speech synthesis exceeded its five-minute admission/request/retry budget. Validated chunks and any unresolved reservation are retained; check readiness and retry."); }
        finally { if (entered) admissionGate.Release(); }
    }
    private async Task SynthesizeCoreAsync(Job? job, NarrationSettings settings, string text, string output, Func<Task>? checkpoint, CancellationToken ct, bool retainIdentity)
    {
        settings.ValidateProviderImage();
        settings.Profile?.Validate(settings.Engine);
        if (string.IsNullOrWhiteSpace(text) || text.Length > 900 || settings.Speed is < .7 or > 1.4 || !double.IsFinite(settings.Speed)) throw new ArgumentException("Choose valid text within the 900-character provider limit and a supported speaking pace.");
        output = Path.GetFullPath(output);
        if (!Workspace.IsWithin(workspace.Root, output)) throw new IOException("Speech output must remain in the private workspace.");
        Workspace.RejectReparsePoints(Path.GetDirectoryName(output)!);
        if (File.Exists(output) && File.GetAttributes(output).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Speech output is a symbolic link. Generation was refused.");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(limits.Synthesis);
        var temporary = output + ".attempt-" + Guid.NewGuid().ToString("N") + ".partial";
        var originalOutputReceipt = job?.PrivateArtifacts.FirstOrDefault(r => r.RelativePath.Equals(Path.GetFileName(output), StringComparison.OrdinalIgnoreCase));
        ExportStagingFile? attemptFile = null;
        async Task RemoveAttemptAsync()
        {
            var held = attemptFile;
            if (held is null) return;
            var removed = false;
            try { held.Delete(); removed = true; }
            finally
            {
                if (removed && job is not null) job.PrivateArtifacts.RemoveAll(r => r.RelativePath.Equals(Path.GetFileName(temporary), StringComparison.OrdinalIgnoreCase) || r.RelativePath.Equals(Path.GetFileName(output), StringComparison.OrdinalIgnoreCase) && !ReferenceEquals(r, originalOutputReceipt));
                attemptFile = null; await held.DisposeAsync();
            }
        }
        var settlementAttempted = true;
        async Task SettleAttemptAsync() { settlementAttempted = true; await ReconcileAdmissionAsync(CancellationToken.None); }
        try
        {
            await ReconcileAdmissionAsync(deadline.Token);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    var image = await VerifyContainerAsync(settings.Engine, false, deadline.Token);
                    if (settings.ProviderImageId is not null && settings.ProviderImageId != image) throw new IOException("The speech image differs from this job's frozen configuration. Restore the original service or submit a new job.");
                    var info = await HealthAsync(settings.Engine, deadline.Token);
                    if (info.Fingerprint != settings.ProviderFingerprint || info.State != "ready" || !info.Voices.Contains(settings.Voice)) throw new IOException("The model or voice differs from this job's frozen configuration. Restore the original service or submit a new job.");
                    while (info.Active != 0)
                    {
                        await Task.Delay(limits.Poll, deadline.Token); info = await HealthAsync(settings.Engine, deadline.Token);
                        if (info.State != "ready" || info.Fingerprint != settings.ProviderFingerprint || !info.Voices.Contains(settings.Voice)) throw new IOException("Speech identity, voice, or readiness changed while waiting. Restore the configured service, then retry.");
                    }
                    RequireAdmission(info);
                    await RequireUnchangedPinAsync(image, deadline.Token);
                    var admission = new PendingSpeechAdmission(1, settings.Engine, image, settings.ProviderFingerprint, info.InstanceId!, info.AdmissionSequence!.Value + 1);
                    settlementAttempted = false;
                    await admission.SaveAsync(workspace, deadline.Token);
                    if (await AdmissionControlAsync(admission, "reserve", deadline.Token) != "reserved") throw new IOException("Speech reservation was not admitted. No source text was sent.");
                    using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(settings.Engine, "speech")) { Content = JsonContent.Create(new { text, voice = settings.Voice, speed = settings.Speed, fingerprint = settings.ProviderFingerprint, instance = admission.Instance, sequence = admission.Sequence }) };
                    using var response = await SendAsync(request, deadline.Token);
                    if ((int)response.StatusCode is 429 or 500 or 502 or 503 or 504) throw new HttpRequestException("The local speech service returned a transient failure.", null, response.StatusCode);
                    if (!response.IsSuccessStatusCode) throw new IOException($"Local speech returned {(int)response.StatusCode}. Validated chunks are preserved; repair settings or service readiness and retry.");
                    if (response.Content.Headers.ContentType?.MediaType != "audio/wav" || response.Content.Headers.ContentLength > MaximumAudioBytes) throw new IOException("The speech response has an invalid type or size.");
                    await using (var stream = await response.Content.ReadAsStreamAsync(deadline.Token))
                    {
                        attemptFile = ExportStagingFile.Create(Path.GetDirectoryName(temporary)!, Path.GetFileName(temporary));
                        if (job is not null)
                        {
                            job.PrivateArtifacts.Add(new(Path.GetFileName(temporary), "", CreationIdentity: attemptFile.Identity));
                            await checkpoint!(); deadline.Token.ThrowIfCancellationRequested();
                        }
                        var received = await CopyBoundedAsync(stream, attemptFile.Stream, MaximumAudioBytes, deadline.Token);
                        if (received < 44 || response.Content.Headers.ContentLength is { } declared && declared != received) throw new IOException("The speech response is truncated or its declared size is inconsistent.");
                        await attemptFile.Stream.FlushAsync(deadline.Token);
                        attemptFile.Stream.Flush(true);
                    }
                    WaveAudio.DataRegion(attemptFile.Stream, false);
                    // Keep validated audio private until the reservation is retired.
                    // A failed settlement must not leave a published, unrecorded WAV.
                    await SettleAttemptAsync();
                    deadline.Token.ThrowIfCancellationRequested();
                    if (job is null)
                    {
                        try { attemptFile.Rename(Path.GetFileName(output)); }
                        catch (IOException error) { throw new IOException("The speech output is occupied or inaccessible. Existing files were preserved; repair private storage and retry.", error); }
                    }
                    else await PrivateJobFiles.CompleteAndMoveCreatedAsync(job, Path.GetDirectoryName(output)!, Path.GetFileName(temporary), Path.GetFileName(output), attemptFile, checkpoint!, deadline.Token, retainIdentity);
                    var published = attemptFile; attemptFile = null;
                    await published.DisposeAsync();
                    return;
                }
                catch (HttpRequestException) when (attempt < limits.TransientRetries && !deadline.IsCancellationRequested)
                {
                    await SettleAttemptAsync();
                    await RemoveAttemptAsync();
                    await Task.Delay(TimeSpan.FromTicks(limits.RetryBackoff.Ticks * (1L << attempt)), deadline.Token);
                }
            }
        }
        catch (ServiceUnavailableException error) { throw new IOException(error.Message, error); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new TimeoutException("Speech synthesis exceeded its five-minute request/retry budget. Validated chunks are retained; check the local service and retry."); }
        finally
        {
            try { await RemoveAttemptAsync(); }
            finally
            {
                // Cancellation can end HTTP while ONNX is still running. Keep the
                // caller's gate until a bounded settlement attempt finishes. If it
                // cannot finish, the durable fence blocks both engines on retry.
                if (!settlementAttempted && File.Exists(PendingSpeechAdmission.PathFor(workspace)))
                    try { await ReconcileAdmissionAsync(CancellationToken.None); }
                    catch (Exception error) when (error is IOException or HttpRequestException or OperationCanceledException or TimeoutException) { }
            }
        }
    }
    private static void RequireAdmission(ProviderInfo info)
    {
        if (info.InstanceId is null || info.AdmissionSequence is null)
            throw new IOException("The local speech service lacks cancellation reservations. Reprovision the updated speech image before generating; no source text was sent.");
    }
    private async Task<string> AdmissionControlAsync(PendingSpeechAdmission admission, string route, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(limits.Health);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint(admission.Engine, route))
        { Content = JsonContent.Create(new { instance = admission.Instance, sequence = admission.Sequence, fingerprint = admission.Fingerprint }) };
        using var response = await SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentLength > 16384) throw new IOException("Speech reservation response has an invalid type or size.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token); using var bytes = new MemoryStream();
        await CopyBoundedAsync(stream, bytes, 16384, timeout.Token);
        try
        {
            using var document = JsonDocument.Parse(bytes.ToArray()); var data = document.RootElement;
            var state = data.GetProperty("state").GetString();
            if (data.GetProperty("service").GetString() != "CommuteCast" || data.GetProperty("contract").GetInt32() != 1 || data.GetProperty("engine").GetString() != admission.Engine ||
                data.GetProperty("instance").GetString() != admission.Instance || data.GetProperty("sequence").GetInt64() != admission.Sequence || state is not ("reserved" or "active" or "settled"))
                throw new IOException("Speech reservation response does not match its saved identity.");
            return state;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        { throw new IOException("Speech reservation response is incomplete or incompatible.", error); }
    }
    private async Task ReconcileAdmissionAsync(CancellationToken ct)
    {
        var pending = await PendingSpeechAdmission.LoadAsync(workspace, ct);
        if (pending is null) return;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(limits.Quiescence);
        var (admission, hash) = pending.Value;
        try
        {
            string image;
            try { image = await VerifyContainerAsync(admission.Engine, false, deadline.Token); }
            catch (OwnedServiceStoppedException)
            {
                // Docker verified the full owned-container policy and a stable
                // pin before proving the old process stopped. Any later process
                // gets a new instance token and rejects the old request.
                await RemoveAdmissionAsync(hash); return;
            }
            while (true)
            {
                var info = await HealthAsync(admission.Engine, deadline.Token); RequireAdmission(info);
                if (info.InstanceId != admission.Instance)
                {
                    // Delayed callbacks carry the old process identity and cannot
                    // reserve or start speech in this replacement process.
                    if (info.Active != 0) throw new IOException("The replacement speech process is active. Wait for it to settle, then retry.");
                    break;
                }
                if (image != admission.Image) throw new IOException("The unresolved speech reservation belongs to another image. Preserve it for inspection; no service was restarted.");
                if (info.Fingerprint != admission.Fingerprint) throw new IOException("The unresolved speech model identity changed. Preserve the reservation for inspection; generation is blocked.");
                var state = await AdmissionControlAsync(admission, "settle", deadline.Token);
                if (state == "settled") break;
                if (state != "active") throw new IOException("The speech reservation did not settle. Generation remains blocked.");
                await Task.Delay(limits.Poll, deadline.Token);
            }
            await RequireUnchangedPinAsync(image, deadline.Token);
            await RemoveAdmissionAsync(hash);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Speech cancellation is still settling. The saved reservation blocks both engines; wait for local inference to finish, then check readiness or retry. No service was restarted."); }
    }
    private async Task RemoveAdmissionAsync(string hash)
    {
        if (!await OwnedFileRemoval.DeleteByHashAsync(workspace.Root, PendingSpeechAdmission.FileName, hash))
            throw new IOException("The pending speech reservation changed during settlement. It was preserved; generation remains blocked.");
    }
    public void Dispose() { http.Dispose(); recoveryGate.Dispose(); admissionGate.Dispose(); }
}
