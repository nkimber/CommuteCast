using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public sealed record AuditionAudio(string RelativePath, string Hash, NarrationSettings Settings, PreparedText Prepared);

/// <summary>Short private PCM preview, sharing the queue's inference gate without queue or export records.</summary>
public sealed class AuditionGenerator(Workspace workspace, ISpeechProvider provider, SemaphoreSlim inferenceGate,
    Func<string, CancellationToken, Task<ProviderInfo>>? readiness = null)
{
    public async Task<AuditionAudio> GenerateAsync(AuditionRequest request, CancellationToken ct)
    {
        var prepared = await Task.Run(() => request.Prepare(ct), ct).ConfigureAwait(false);
        await inferenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await (readiness is null ? provider.ReadyAsync(request.Settings.Engine, ct) : readiness(request.Settings.Engine, ct)).ConfigureAwait(false);
            if (info.Engine != request.Settings.Engine || info.State != "ready" || info.Active != 0 || !info.Voices.Contains(request.Settings.Voice))
                throw new IOException("The selected audition engine or voice is unavailable. Check speech readiness, then select an installed voice.");
            var snapshot = request.Settings with { ProviderFingerprint = info.Fingerprint, ProviderImageId = info.ImageId };
            snapshot.ValidateProviderImage(); ct.ThrowIfCancellationRequested();
            var relative = "auditions/audition-" + Guid.NewGuid().ToString("N") + ".wav";
            var path = OwnedFileRemoval.Resolve(workspace.Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("The private audition output is occupied. Existing content was preserved.");
            await provider.SynthesizeAsync(snapshot, prepared.Script, path, ct).ConfigureAwait(false);
            // Capture a completed late response before observing Stop, so its owned bytes can be
            // removed instead of becoming playable through a stale continuation.
            WaveAudio.DataRegion(path, false);
            var result = new AuditionAudio(relative, await Workspace.HashFileAsync(path, CancellationToken.None).ConfigureAwait(false), snapshot, prepared);
            if (ct.IsCancellationRequested)
            {
                await RemoveAsync(result).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            return result;
        }
        finally { inferenceGate.Release(); }
    }
    public Task<bool> RemoveAsync(AuditionAudio audio) => OwnedFileRemoval.DeleteByHashAsync(workspace.Root, audio.RelativePath, audio.Hash);
}
