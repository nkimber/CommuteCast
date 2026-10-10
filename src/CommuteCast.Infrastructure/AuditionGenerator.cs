using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public sealed record AuditionAudio(string RelativePath, string Hash, NarrationSettings Settings, PreparedText Prepared, string? OwnershipId = null);

/// <summary>Short private PCM preview, sharing the queue's inference gate without queue or export records.</summary>
public sealed class AuditionGenerator(Workspace workspace, ISpeechProvider provider, SemaphoreSlim inferenceGate,
    Func<string, CancellationToken, Task<ProviderInfo>>? readiness = null)
{
    private readonly AuditionOwnershipStore ownership = new(workspace);
    public async Task<AuditionAudio> GenerateAsync(AuditionRequest request, CancellationToken ct)
    {
        var prepared = await Task.Run(() => request.Prepare(ct), ct).ConfigureAwait(false);
        await inferenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await (provider is IRenderUnitSpeechProvider units && SpeechProviders.IsHosted(request.Settings.Engine) ? units.ReadyForSettingsAsync(request.Settings, ct) : readiness is null ? provider.ReadyAsync(request.Settings.Engine, ct) : readiness(request.Settings.Engine, ct)).ConfigureAwait(false);
            if (info.Engine != request.Settings.Engine || info.State != "ready" || info.Active != 0 || !info.Voices.Contains(request.Settings.Voice))
                throw new IOException("The selected audition engine or voice is unavailable. Check speech readiness, then select an installed voice.");
            var snapshot = request.Settings with { ProviderFingerprint = info.Fingerprint, ProviderImageId = info.ImageId };
            snapshot.ValidateProviderImage(); ct.ThrowIfCancellationRequested();
            var relative = "auditions/audition-" + Guid.NewGuid().ToString("N") + ".wav";
            var path = OwnedFileRemoval.Resolve(workspace.Root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("The private audition output is occupied. Existing content was preserved.");
            if (provider is IDurableAuditionSpeechProvider durable)
                return await GenerateDurableAsync(durable, snapshot, prepared, relative, path, ct).ConfigureAwait(false);
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
    private async Task<AuditionAudio> GenerateDurableAsync(IDurableAuditionSpeechProvider durable, NarrationSettings settings, PreparedText prepared, string relative, string path, CancellationToken ct)
    {
        var id = Path.GetFileNameWithoutExtension(path)["audition-".Length..];
        var journal = new AuditionWriteJournal(id, DateTimeOffset.UtcNow, []);
        await ownership.SaveAsync(journal, ct).ConfigureAwait(false);
        try
        {
            await durable.SynthesizeAuditionAsync(journal, settings, prepared.Script, path, () => ownership.SaveAsync(journal, ct), ct).ConfigureAwait(false);
            var receipt = journal.Artifacts.Single();
            if (receipt.RelativePath != Path.GetFileName(path) || receipt.CreationIdentity is not null || receipt.PromotionIdentity is null) throw new IOException("The preview has no completed original-file receipt.");
            await using (var held = ExportStagingFile.OpenIfPresent(Path.GetDirectoryName(path)!, receipt.RelativePath) ?? throw new IOException("The completed preview is missing."))
            {
                if (!held.Matches(receipt.PromotionIdentity) || await held.HashAsync(CancellationToken.None).ConfigureAwait(false) != receipt.Hash) throw new IOException("The completed preview was replaced or changed. It was preserved.");
                WaveAudio.DataRegion(held.Stream, false);
            }
            ct.ThrowIfCancellationRequested();
            return new(relative, receipt.Hash, settings, prepared, id);
        }
        catch
        {
            try
            {
                var saved = (await ownership.LoadAsync().ConfigureAwait(false)).SingleOrDefault(r => r.Id == id);
                if (saved is not null) await RemoveJournalAsync(saved, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException) { /* durable intent remains for explicit recovery */ }
            throw;
        }
    }
    private async Task<bool> RemoveJournalAsync(AuditionWriteJournal journal, CancellationToken ct)
    {
        AuditionOwnershipStore.Validate(journal); var directory = Path.Combine(workspace.Root, "auditions"); Workspace.RejectReparsePoints(directory);
        var names = journal.Artifacts.Select(r => r.RelativePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(directory) && Directory.EnumerateFileSystemEntries(directory, "audition-" + journal.Id + ".wav*").Any(path => !names.Contains(Path.GetFileName(path))))
            throw new IOException("An unrecorded preview output requires inspection. Files and ownership intent were preserved.");
        var removed = false;
        foreach (var receipt in journal.Artifacts)
        {
            ct.ThrowIfCancellationRequested();
            await using var held = ExportStagingFile.OpenIfPresent(directory, receipt.RelativePath);
            if (held is null) continue;
            var identity = receipt.CreationIdentity ?? receipt.PromotionIdentity!;
            if (!held.Matches(identity) || receipt.CreationIdentity is null && await held.HashAsync(ct).ConfigureAwait(false) != receipt.Hash)
                throw new IOException("An owned preview was replaced or changed. Files and its receipt were preserved.");
            held.Delete(); removed = true;
        }
        await ownership.RemoveAsync(journal, ct).ConfigureAwait(false); return removed;
    }
    public async Task<int> RecoverAsync(CancellationToken ct = default)
    {
        await inferenceGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var records = await ownership.LoadAsync(ct).ConfigureAwait(false); var count = 0;
            foreach (var journal in records) { await RemoveJournalAsync(journal, ct).ConfigureAwait(false); count++; }
            return count;
        }
        finally { inferenceGate.Release(); }
    }
    public async Task<bool> RemoveAsync(AuditionAudio audio)
    {
        if (audio.OwnershipId is null) return await OwnedFileRemoval.DeleteByHashAsync(workspace.Root, audio.RelativePath, audio.Hash).ConfigureAwait(false);
        var journal = (await ownership.LoadAsync().ConfigureAwait(false)).SingleOrDefault(r => r.Id == audio.OwnershipId);
        if (journal is null) return false;
        var receipt = journal.Artifacts.SingleOrDefault(r => r.RelativePath == "audition-" + journal.Id + ".wav");
        if (audio.RelativePath != "auditions/audition-" + journal.Id + ".wav" || receipt?.Hash != audio.Hash) throw new IOException("The preview cleanup request does not match its durable receipt. Files were preserved.");
        return await RemoveJournalAsync(journal, CancellationToken.None).ConfigureAwait(false);
    }
}
