using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public sealed class SpeechProviderService(Workspace workspace, AppSettings settings, LocalSpeechProvider? localProvider = null, ISpeechSecrets? secrets = null, HttpClient? http = null)
    : IDurableSpeechProvider, IDurableAuditionSpeechProvider, IJobSpeechStatusProvider, IRenderUnitSpeechProvider, IDisposable
{
    private readonly LocalSpeechProvider local = localProvider ?? new(workspace);
    private readonly HostedSpeechProvider hosted = new(secrets ?? new WindowsSpeechSecrets(), http);
    public IReadOnlyDictionary<string, string> VoiceNames => hosted.VoiceNames;
    public SpeechConfiguration Configuration(string engine) => settings.SpeechDefaults.TryGetValue(engine, out var config) ? config : new(settings.HostedConnections.TryGetValue(engine, out var connection) ? connection.Model : SpeechProviders.Get(engine).DefaultModel);
    public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => ReadyAsync(engine, ct, false);
    public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct, bool explicitRetry) => SpeechProviders.IsHosted(engine) ? hosted.ReadyAsync(engine, Configuration(engine), ct) : local.ReadyAsync(engine, ct, explicitRetry);
    public Task<ProviderInfo> ProbeAsync(string engine, CancellationToken ct = default) => SpeechProviders.IsHosted(engine) ? hosted.ReadyAsync(engine, Configuration(engine), ct) : local.ProbeAsync(engine, ct);
    public Task<ProviderInfo> CaptureForSubmissionAsync(string engine, ProviderInfo? cached, CancellationToken ct = default) => SpeechProviders.IsHosted(engine) ? ReadyAsync(engine, ct) : local.CaptureForSubmissionAsync(engine, cached, ct);
    public Task ResetRecoveryBudgetAsync(string engine, CancellationToken ct = default) => SpeechProviders.IsHosted(engine) ? Task.CompletedTask : local.ResetRecoveryBudgetAsync(engine, ct);
    public Task<ProviderInfo> ReadyForSettingsAsync(NarrationSettings snapshot, CancellationToken ct) => SpeechProviders.IsHosted(snapshot.Engine) ? hosted.ReadyAsync(snapshot.Engine, snapshot.Speech ?? throw new ArgumentException("Missing saved speech model."), ct) : local.ReadyAsync(snapshot.Engine, ct);
    public Task<ProviderInfo> ReadyForJobAsync(Job job, CancellationToken ct) => SpeechProviders.IsHosted(job.Settings.Engine) ? ReadyForSettingsAsync(job.Settings, ct) : local.ReadyForJobAsync(job, ct);
    public Task SynthesizeAsync(NarrationSettings snapshot, string text, string output, CancellationToken ct) => SynthesizeAsync(new Job(), snapshot, text, output, () => Task.CompletedTask, ct);
    public Task SynthesizeAsync(Job job, NarrationSettings snapshot, string text, string output, Func<Task> checkpoint, CancellationToken ct) => SpeechProviders.IsHosted(snapshot.Engine)
        ? WriteHostedAsync(job, snapshot, text, output, checkpoint, ct) : local.SynthesizeAsync(job, snapshot, text, output, checkpoint, ct);
    private Task WriteHostedAsync(Job job, NarrationSettings snapshot, string text, string output, Func<Task> checkpoint, CancellationToken ct, IReadOnlyList<PodcastTurn>? turns = null)
    {
        GuardOutput(output);
        return PrivateJobFiles.WriteRecordedAsync(job, Path.GetDirectoryName(Path.GetFullPath(output))!, Path.GetFileName(output), stream => hosted.WriteAsync(job, snapshot, text, stream, checkpoint, ct, turns), checkpoint, ct);
    }
    private void GuardOutput(string output)
    {
        if (!Workspace.IsWithin(workspace.Root, Path.GetFullPath(output)) || !output.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) throw new IOException("Speech output must be a WAV in CommuteCast's private workspace.");
        Workspace.RejectReparsePoints(Path.GetDirectoryName(Path.GetFullPath(output))!);
    }
    public Task SynthesizeUnitAsync(Job job, TextChunk unit, string output, Func<Task> checkpoint, CancellationToken ct)
    {
        if (job.Episode is null) return SynthesizeAsync(job, job.Settings, unit.Text, output, checkpoint, ct);
        PodcastScript.ValidateManifest(job);
        var turns = unit.Turns!; var speaker = job.Episode.Speakers.Single(s => s.Name == turns[0].Speaker);
        var speech = job.Settings.Speech is { } config ? config with { Delivery = speaker.Delivery, Emotion = speaker.Emotion } : null;
        var snapshot = job.Settings with { Voice = speaker.Voice, Speed = speaker.Speed, Speech = speech };
        if (SpeechProviders.IsHosted(snapshot.Engine))
        {
            foreach (var name in turns.Select(t => t.Speaker).Distinct())
            {
                var member = job.Episode.Speakers.Single(s => s.Name == name);
                HostedSpeechProvider.ValidateSettings(snapshot with { Voice = member.Voice, Speed = member.Speed, Speech = speech! with { Delivery = member.Delivery, Emotion = member.Emotion } });
            }
            if (turns.Count > 1 && (!job.Episode.Dialogue || !speech!.Dialogue || SpeechProviders.Capabilities(snapshot.Engine, speech.Model).DialogueSpeakers < job.Episode.Speakers.Count || job.Episode.Speakers.Any(s => s.Speed != 1)))
                throw new IOException("This saved dialogue block is incompatible with the provider capabilities.");
            return WriteHostedAsync(job, snapshot, unit.Text, output, checkpoint, ct, turns);
        }
        if (turns.Count != 1) throw new IOException("Local providers require individual speaker turns.");
        return local.SynthesizeAsync(job, snapshot, unit.Text, output, checkpoint, ct);
    }
    public async Task SynthesizeAuditionAsync(AuditionWriteJournal journal, NarrationSettings snapshot, string text, string output, Func<Task> checkpoint, CancellationToken ct)
    {
        if (!SpeechProviders.IsHosted(snapshot.Engine)) { await local.SynthesizeAuditionAsync(journal, snapshot, text, output, checkpoint, ct); return; }
        GuardOutput(output);
        var directory = Path.GetDirectoryName(Path.GetFullPath(output))!; var name = Path.GetFileName(output);
        var job = new Job { PrivateArtifacts = journal.Artifacts, SpeechAttempts = [] };
        async Task SaveRequest() { await new HostedAuditionHistory(workspace).SaveAsync(job.SpeechAttempts!); await checkpoint(); }
        await PrivateJobFiles.PrepareOutputAsync(job, directory, name, ct);
        await using var held = ExportStagingFile.Create(directory, name);
        job.PrivateArtifacts.Add(new(name, "", CreationIdentity: held.Identity));
        await checkpoint();
        try
        {
            await hosted.WriteAsync(job, snapshot, text, held.Stream, SaveRequest, ct); held.Stream.Flush(true);
            var receipt = new PrivateArtifactReceipt(name, await held.HashAsync(ct), PromotionIdentity: held.Identity);
            job.PrivateArtifacts.Clear(); job.PrivateArtifacts.Add(receipt); await checkpoint();
        }
        catch { held.Delete(); throw; }
    }
    public void Dispose() { local.Dispose(); hosted.Dispose(); }
}
