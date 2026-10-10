using CommuteCast.Core;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CommuteCast.Infrastructure;

public sealed record SpeechRepairResult(ProviderInfo Provider, string Detail);

public sealed partial class LocalSpeechProvider
{
    /// <summary>Explicit repair can recover a pin or create a missing container; ordinary readiness never does.</summary>
    public async Task<SpeechRepairResult> RepairAsync(string engine, CancellationToken ct = default)
    {
        DockerContainerPolicy.Name(engine);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct); deadline.CancelAfter(limits.Readiness);
        var admissionEntered = false; var recoveryEntered = false;
        string detail;
        try
        {
            await admissionGate.WaitAsync(deadline.Token); admissionEntered = true;
            await recoveryGate.WaitAsync(deadline.Token); recoveryEntered = true;
            detail = await RepairInstallationAsync(engine, deadline.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Speech repair exceeded its two-minute allowance. Open Docker Desktop, allow models to finish loading, then try Start / repair speech services again."); }
        finally
        {
            if (recoveryEntered) recoveryGate.Release();
            if (admissionEntered) admissionGate.Release();
        }
        // Use the remaining repair allowance for the existing reservation settlement,
        // start-once policy and health checks. Saved jobs still verify their frozen identity.
        try { return new(await ReadyCoreAsync(engine, deadline.Token, true, null, desktopAlreadyChecked: true), detail); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new TimeoutException("Speech repair exceeded its two-minute allowance. The installed configuration may be repaired while the model is still loading. Try Start / repair speech services again."); }
    }

    private async Task<string> RepairInstallationAsync(string engine, CancellationToken ct)
    {
        var pinPath = Path.Combine(workspace.Root, "provider-lock.local.json"); SqliteSchema.RejectLink(pinPath);
        if (File.Exists(pinPath) && new FileInfo(pinPath).Length > 65536)
            throw new SpeechSetupException("The local provider pin exceeds its safe size.", "Ask your administrator to inspect the saved speech configuration before provisioning.");
        var originalHash = File.Exists(pinPath) ? await Workspace.HashFileAsync(pinPath, ct) : null;
        var pinnedImage = originalHash is null ? null : await ReadPinnedImageAsync(ct);
        await VerifyDaemonAsync(true, ct);
        var inspection = await runtime.DockerAsync(["inspect", DockerContainerPolicy.Name(engine)], TimeSpan.FromSeconds(30), ct);
        if (inspection.ExitCode != 0)
        {
            if (pinnedImage is null) throw new SpeechSetupException("No installed speech configuration could be recovered.", "The service and local image pin are missing. Follow the manual setup steps below.");
            // An inspect failure can also mean a daemon/permission problem. Establish
            // absence independently before creating anything, never remove by name.
            var existing = await runtime.DockerAsync(["container", "ls", "--all", "--filter", "name=^/" + DockerContainerPolicy.Name(engine) + "$", "--format", "{{.ID}}"], TimeSpan.FromSeconds(15), ct);
            if (existing.ExitCode != 0 || existing.Output.Trim().Length != 0)
                throw new SpeechSetupException("The speech container could not be inspected.", "Open Docker Desktop and check its engine/permissions. An existing container was preserved.");
            await RequireNoPendingRepairAsync(ct);
            await RequireUnchangedPinAsync(pinnedImage, ct);
            var created = await runtime.DockerAsync(CreateSpeechContainerArguments(engine, pinnedImage), TimeSpan.FromSeconds(30), ct);
            if (created.ExitCode != 0) throw new SpeechSetupException("The missing speech service could not be recreated from its pinned local image.", "The image may have been removed or the container name may be in use. Follow the manual setup steps below.");
            var verified = await runtime.DockerAsync(["inspect", DockerContainerPolicy.Name(engine)], TimeSpan.FromSeconds(30), ct);
            if (verified.ExitCode != 0) throw new SpeechSetupException("The recreated speech service could not be verified.", "Inspect Docker Desktop before retrying repair.");
            DockerContainerPolicy.Evaluate(verified.Output, engine, pinnedImage);
            await RequireUnchangedPinAsync(pinnedImage, ct);
            return "Recreated the missing service from the pinned local image; no download was needed.";
        }
        if (pinnedImage is not null)
        {
            try
            {
                DockerContainerPolicy.Evaluate(inspection.Output, engine, pinnedImage);
                return "Installed configuration verified. Checked readiness and started the service if stopped.";
            }
            catch (SpeechSetupException error) when (error.ImageMismatch) { }
        }
        await RequireNoPendingRepairAsync(ct);
        var installed = new List<(string Engine, string Image, string Id, ProviderInfo Health)>();
        foreach (var installedEngine in new[] { "kokoro", "piper" })
        {
            var result = installedEngine == engine ? inspection : await runtime.DockerAsync(["inspect", DockerContainerPolicy.Name(installedEngine)], TimeSpan.FromSeconds(30), ct);
            if (result.ExitCode != 0) throw new SpeechSetupException("The saved speech image could not be reconciled.", "Both installed CommuteCast services must be present, running and idle on the same image. Follow the manual setup steps below.");
            var (image, id) = InstalledIdentity(result.Output);
            if (!DockerContainerPolicy.Evaluate(result.Output, installedEngine, image))
                throw new SpeechSetupException("The installed speech service is stopped on a different or unrecorded image.", "Start both owned services through your approved provisioning process before reconciling their image.");
            if (installed.Count > 0 && installed[0].Image != image)
                throw new SpeechSetupException("Kokoro and Piper use different installed speech images.", "Provision both services from one compatible image before trying repair again.");
            var health = await HealthAsync(installedEngine, ct); RequireAdmission(health);
            if (health.State != "ready" || health.Active != 0)
                throw new SpeechSetupException("The installed speech model is busy, loading or failed.", "Wait for current speech and model loading to finish, then try Start / repair speech services again. The saved image pin was preserved.");
            installed.Add((installedEngine, image, id, health));
        }
        // Recheck the full container and service process identities before adopting
        // the image. Never adopt a name, mutable tag or unverifiable service.
        foreach (var service in installed)
        {
            var result = await runtime.DockerAsync(["inspect", DockerContainerPolicy.Name(service.Engine)], TimeSpan.FromSeconds(30), ct);
            if (result.ExitCode != 0 || !DockerContainerPolicy.Evaluate(result.Output, service.Engine, service.Image) || InstalledIdentity(result.Output).Id != service.Id)
                throw new SpeechSetupException("An installed speech container changed during repair.", "Wait for provisioning to finish, then try repair again. The saved image pin was preserved.");
            var health = await HealthAsync(service.Engine, ct); RequireAdmission(health);
            if (health.State != "ready" || health.Active != 0 || health.InstanceId != service.Health.InstanceId || health.Fingerprint != service.Health.Fingerprint)
                throw new SpeechSetupException("The speech service process changed or became busy during repair.", "Wait for current speech to finish, then try repair again. The saved image pin was preserved.");
        }
        await RequireNoPendingRepairAsync(ct);
        SqliteSchema.RejectLink(pinPath);
        if (originalHash is null ? File.Exists(pinPath) : !File.Exists(pinPath) || await Workspace.HashFileAsync(pinPath, ct) != originalHash)
            throw new SpeechSetupException("The saved speech configuration changed during repair.", "Wait for provisioning to finish, then try repair again.");
        ct.ThrowIfCancellationRequested();
        await Workspace.AtomicWriteAsync(pinPath, JsonSerializer.Serialize(new { ImageId = installed[0].Image, Contract = 1 }));
        return "Recovered the local image pin from both verified, idle installed services. No container was restarted. Older unfinished narrations still require their original image or Use as a new draft.";
    }

    private async Task RequireNoPendingRepairAsync(CancellationToken ct)
    {
        if (await PendingSpeechAdmission.LoadAsync(workspace, ct) is not null)
            throw new SpeechSetupException("A previous speech reservation still needs to be settled.", "The image pin and containers were preserved. Wait for prior speech to finish and check the original saved speech service before changing its installation. Keep speech-admission.json for recovery.");
    }

    private static (string Image, string Id) InstalledIdentity(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() != 1) throw new JsonException();
            var node = document.RootElement[0]; var image = node.GetProperty("Image").GetString() ?? ""; var id = node.GetProperty("Id").GetString() ?? "";
            if (!Regex.IsMatch(image, "^sha256:[a-f0-9]{64}$") || !Regex.IsMatch(id, "^[a-f0-9]{64}$")) throw new JsonException();
            return (image, id);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new SpeechSetupException("Docker returned incomplete installed speech identities.", "Open Docker Desktop and inspect the CommuteCast services before retrying repair."); }
    }

    private static string[] CreateSpeechContainerArguments(string engine, string image) =>
    ["create", "--pull", "never", "--name", DockerContainerPolicy.Name(engine), "--network", "bridge",
        "--label", "com.commutecast.owner=CommuteCast", "--label", "com.commutecast.contract=1", "--label", "com.docker.compose.project=commutecast", "--label", "com.docker.compose.service=" + engine,
        "--env", "COMMUTECAST_ENGINE=" + engine, "--publish", "127.0.0.1:" + (engine == "kokoro" ? "8765" : "8766") + ":8765",
        "--cpus", "2", "--memory", engine == "kokoro" ? "2g" : "1g", "--security-opt", "no-new-privileges:true", "--restart", "no",
        "--log-driver", "json-file", "--log-opt", "max-size=5m", "--log-opt", "max-file=3",
        "--health-cmd", "python -c \"import json,urllib.request; assert json.load(urllib.request.urlopen('http://127.0.0.1:8765/health',timeout=3))['state']=='ready'\"",
        "--health-interval", "30s", "--health-timeout", "5s", "--health-start-period", "120s", "--health-retries", "3",
        image, "uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8765", "--no-access-log"];
}
