using System.Collections.Concurrent;
using System.Text.Json;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Web;

public sealed class WebRuntime(IConfiguration configuration, IM365Publisher publisher, IWebSpeechFactory speechFactory, ILogger<WebRuntime> logger) : BackgroundService
{
    public Workspace Workspace { get; private set; } = null!;
    public AppSettings Settings { get; private set; } = null!;
    public WebSettings WebSettings { get; private set; } = WebSettings.Empty;
    public QueueCoordinator Queue { get; private set; } = null!;
    public ISpeechService Speech { get; private set; } = null!;
    public DraftStore Drafts { get; private set; } = null!;
    private WorkspaceLease? lease;
    private readonly object stopSync = new();
    private Task? stopping;
    private readonly SemaphoreSlim gate = new(1), publicationGate = new(1);
    private readonly ConcurrentDictionary<string, PublishingRecord> publications = new();
    private readonly ConcurrentDictionary<string, ProviderInfo> providerCatalog = new();
    private volatile NarrationOptions? narrationDefaults;
    private readonly ISpeechSecrets speechSecrets = new WindowsSpeechSecrets();
    private ExportPublisher localPublisher = null!;
    private string ConfigPath => Path.Combine(Workspace.Root, "web", "settings.json");
    private string PublicationPath(string id) => Path.Combine(Workspace.JobDirectory(id), "m365-publication.json");

    public override async Task StartAsync(CancellationToken ct)
    {
        var root = configuration["CommuteCast:Workspace"];
        Workspace = new(string.IsNullOrWhiteSpace(root) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommuteCast-Web") : root);
        lease = WorkspaceLease.Acquire(Workspace);
        if (configuration.GetValue("CommuteCast:OperationalLogging", true)) AppLogging.Start("web", Path.Combine(Workspace.Root, "logs"));
        try
        {
            Settings = await Workspace.LoadSettingsAsync();
            narrationDefaults = Settings.NarrationDefaults ?? NarrationOptions.Capture(Settings);
            Settings.NarrationDefaults = narrationDefaults;
            foreach (var (engine, info) in Settings.Providers) providerCatalog[engine] = info;
            if (File.Exists(ConfigPath)) WebSettings = JsonSerializer.Deserialize<WebSettings>(await File.ReadAllTextAsync(ConfigPath, ct)) ?? WebSettings.Empty;
            // Server-managed local exports are never in wwwroot and never exposed through HTTP.
            Settings.Destination = Path.GetFullPath(configuration["CommuteCast:Exports"] ?? Workspace.Root.TrimEnd(Path.DirectorySeparatorChar) + "-exports");
            Directory.CreateDirectory(Settings.Destination);
            Workspace.GuardLocalDestination(Settings.Destination);
            var store = new SqliteJobStore(Workspace);
            Speech = speechFactory.Create(Workspace, Settings, speechSecrets);
            localPublisher = new(Workspace, store);
            Queue = new(Workspace, store, Speech, new(Settings), localPublisher)
            {
                Paused = Settings.QueuePaused, CacheQuotaMiB = Settings.CacheQuotaMiB,
                ScratchRetentionDays = Settings.ScratchRetentionDays, PrivateStorageLimitMiB = Settings.PrivateStorageLimitMiB
            };
            Drafts = new(Workspace);
            await Queue.InitializeAsync(ct);
            foreach (var job in Queue.Snapshot())
            {
                if (!File.Exists(PublicationPath(job.Id))) continue;
                var record = JsonSerializer.Deserialize<PublishingRecord>(await File.ReadAllTextAsync(PublicationPath(job.Id), ct));
                if (record is null || record.JobId != job.Id) throw new IOException("A publication record has an invalid job identity. Inspect the workspace before restarting.");
                if (record.Status == "Publishing") record = record with { Status = "Pending", Error = "Recovering the interrupted upload." };
                publications[job.Id] = record;
            }
            await base.StartAsync(ct);
        }
        catch
        {
            if (Queue is not null) await Queue.DisposeAsync(); Speech?.Dispose(); lease.Dispose();
            lock (stopSync) stopping = Task.CompletedTask;
            throw;
        }
    }

    public object State() => new
    {
        paused = Queue.Paused, error = Queue.PersistenceError, publishing = new
        {
            configured = WebSettings.Destination.Configured, WebSettings.AutoPublish, WebSettings.FolderName, WebSettings.FolderUrl,
            destination = WebSettings.Destination, hasSecret = WebSettings.Destination.Configured && WindowsWebSecrets.Get(WindowsWebSecrets.Target(WebSettings.Destination)) is not null
        },
        defaults = narrationDefaults, providers = SpeechProviders.All.Select(p => new
        {
            p.Id, p.Name, p.DefaultVoice, p.DefaultModel, p.Capabilities,
            hasKey = !p.Capabilities.Hosted || speechSecrets.Get(p.Id) is not null,
            verified = providerCatalog.ContainsKey(p.Id),
            voices = SpeechVoiceCatalog.Choices(p.Id, providerCatalog.TryGetValue(p.Id, out var info) ? info.Voices : [], SpeechVoiceCatalog.DefaultVoice(p.Id))
                .Select(v => new { v.Id, displayName = Speech.VoiceNames.TryGetValue(p.Id + ":" + v.Id, out var name) ? name : v.DisplayName, v.Available })
        }),
        jobs = Queue.Snapshot().OrderByDescending(j => j.CreatedUtc).Select(j => new
        {
            j.Id, j.Title, j.CreatedUtc, stage = j.Stage.ToString(), j.Settings.Engine, j.Settings.Voice, j.Settings.Speed,
            j.DurationSeconds, j.CompletedChunks, chunks = j.Chunks.Count, j.Error, j.ExportCommitted,
            progress = JobProgress.Describe(j, Queue.Paused), publication = publications.GetValueOrDefault(j.Id),
            words = j.Source.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length,
            position = j.QueuePosition, timing = j.Activity.Read().Timing
        })
    };

    public object Details(string id)
    {
        var job = Find(id);
        return new { job.Id, job.Title, job.Source, job.Prepared, job.Settings, job.GenerationPrompt, job.Episode, job.Error, stage = job.Stage.ToString(), publication = publications.GetValueOrDefault(id) };
    }
    private Job Find(string id) => Queue.Snapshot().FirstOrDefault(j => j.Id == id) ?? throw new ArgumentException("This narration no longer exists.");

    public async Task<object> LoadDraftAsync()
    {
        var saved = await ReadEditorDraftAsync();
        return new { saved.Draft, saved.Options, revision = Job.Hash(JsonSerializer.Serialize(saved)) };
    }
    private async Task<EditorDraft> ReadEditorDraftAsync()
    {
        var path = Path.Combine(Workspace.Root, "web", "draft.json");
        if (!File.Exists(path)) return new(await Drafts.LoadAsync());
        Workspace.RejectFileReparsePoint(path);
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new IOException("The saved web draft is oversized. Preserve it for inspection.");
        return JsonSerializer.Deserialize<EditorDraft>(await File.ReadAllTextAsync(path)) ?? throw new IOException("The web draft could not be read.");
    }
    public async Task<object> SaveDraftAsync(DraftRequest request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var current = await ReadEditorDraftAsync();
            if (Job.Hash(JsonSerializer.Serialize(current)) != request.Revision) throw new IOException("This draft changed in another browser. Your text is still here; copy it before refreshing to load the newer draft.");
            var draft = request.Draft;
            if (draft is null || draft.Title is null || draft.Title.Length > 300 || draft.Source is null || draft.Source.Length > TextPreparation.MaximumCharacters)
                throw new ArgumentException("Keep the draft within 300 title characters and 250,000 narration characters.");
            draft.PromptDraft?.ValidateStorage(); draft.Podcast?.Episode.ValidateStorage(); DraftStore.ValidateGenerationPrompt(draft.GenerationPrompt);
            request.Options?.Validate();
            var saved = new EditorDraft(draft, request.Options ?? current.Options);
            await Workspace.AtomicWriteAsync(Path.Combine(Workspace.Root, "web", "draft.json"), JsonSerializer.Serialize(saved));
            return new { revision = Job.Hash(JsonSerializer.Serialize(saved)) };
        }
        finally { gate.Release(); }
    }

    public async Task<object> PreviewAsync(ComposeRequest request, CancellationToken ct)
    {
        request.Options.Validate();
        var prepared = await Task.Run(() => Prepare(request).Prepared, ct);
        return new { prepared.Script, changes = prepared.Spans.Where(s => s.Original != s.Narration), characters = prepared.Script.Length };
    }
    private static (PreparedText Prepared, List<TextChunk>? Units) Prepare(ComposeRequest request)
    {
        var o = request.Options;
        if (request.Episode is null) return (TextPreparation.Prepare(request.Source, o.ExcludeCode, o.Pronunciation, o.Profile), null);
        var rendered = PodcastScript.Prepare(request.Source, request.Episode, new(o.Engine, o.Voice, o.Speed, o.ExcludeCode, o.Pronunciation, "", o.Profile, Speech: o.Speech, LocalVoice: o.LocalVoice));
        return (rendered.Prepared, rendered.Units);
    }
    public async Task<string> SubmitAsync(ComposeRequest request, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            request.Options.Validate(); DraftStore.ValidateGenerationPrompt(request.GenerationPrompt);
            if (!Guid.TryParseExact(request.SubmissionId, "N", out _)) throw new ArgumentException("The submission needs a valid request identity. Refresh the page and try again.");
            var existing = Queue.Snapshot().FirstOrDefault(j => j.Id == request.SubmissionId);
            if (existing is not null)
            {
                var captured = existing.Settings;
                var originalOptions = new NarrationOptions(captured.Engine, captured.Voice, captured.Speed, captured.ExcludeCode, captured.Pronunciation, captured.Profile ?? new(), captured.LocalVoice, captured.Speech);
                var requestedOptions = request.Options with { Speech = SpeechProviders.IsHosted(request.Options.Engine) ? request.Options.Speech ?? new(SpeechProviders.Get(request.Options.Engine).DefaultModel) : null };
                if (existing.Source != request.Source || existing.Title != (string.IsNullOrWhiteSpace(request.Title) ? TextPreparation.SuggestTitle(request.Source) : request.Title.Trim()) ||
                    requestedOptions != originalOptions || existing.GenerationPrompt != request.GenerationPrompt || JsonSerializer.Serialize(existing.Episode) != JsonSerializer.Serialize(request.Episode))
                    throw new ArgumentException("This submission was already accepted with different text or voice settings. Open its library item before creating a new narration.");
                return existing.Id;
            }
            var o = request.Options;
            var prepared = Prepare(request);
            if (request.Title.Length > 300) throw new ArgumentException("Keep the narration title within 300 characters.");
            var speech = SpeechProviders.IsHosted(o.Engine) ? o.Speech ?? new(SpeechProviders.Get(o.Engine).DefaultModel) : null;
            if (speech is not null) Settings.SpeechDefaults[o.Engine] = speech;
            await localPublisher.TestDestinationAsync(Settings.Destination, ct);
            Settings.Providers.TryGetValue(o.Engine, out var cached);
            var info = await Speech.CaptureForSubmissionAsync(o.Engine, cached, ct);
            var voices = request.Episode?.Speakers.Select(s => s.Voice).ToArray() ?? [o.Voice];
            if (voices.Any(v => !info.Voices.Contains(v))) throw new ArgumentException("Refresh voices and choose an available voice for every speaker.");
            if (speech is not null && info.Fingerprint != speech.Identity(o.Engine)) throw new IOException("The speech model changed. Refresh voices and submit again.");
            if (request.Episode is null) o.LocalVoice?.RequireAvailable(info, o.Voice);
            else foreach (var member in request.Episode.Speakers) PodcastScript.LocalDelivery(member,
                new(o.Engine, o.Voice, o.Speed, o.ExcludeCode, o.Pronunciation, info.Fingerprint, o.Profile, info.ImageId, speech, o.LocalVoice))?.RequireAvailable(info, member.Voice);
            var job = new Job
            {
                Id = request.SubmissionId!,
                Title = string.IsNullOrWhiteSpace(request.Title) ? TextPreparation.SuggestTitle(request.Source) : request.Title.Trim(),
                Source = request.Source, Prepared = prepared.Prepared, Destination = Settings.Destination,
                Settings = new(o.Engine, o.Voice, o.Speed, o.ExcludeCode, o.Pronunciation, info.Fingerprint, o.Profile, info.ImageId, speech, o.LocalVoice),
                GenerationPrompt = request.GenerationPrompt, Episode = request.Episode
            };
            if (prepared.Units is not null) { job.Chunks = prepared.Units; job.ChunkingVersion = PodcastScript.ChunkVersion; job.AudioContractVersion = PodcastScript.AudioVersion; }
            NaturalChunker.ConfigureNew(job);
            // Record the publication intent before the queue becomes dispatchable.
            await SavePublicationAsync(new(job.Id, WebSettings.AutoPublish ? "Pending" : "Not requested", WebSettings.Destination, WebSettings.FolderUrl));
            await Queue.AddAsync(job, ct);
            Settings.Providers[o.Engine] = info;
            providerCatalog[o.Engine] = info;
            await Workspace.SaveSettingsAsync(Settings);
            return job.Id;
        }
        finally { gate.Release(); }
    }
    public async Task SaveDefaultsAsync(NarrationOptions options, CancellationToken ct)
    {
        options.Validate(); await gate.WaitAsync(ct);
        try
        {
            var persisted = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(Settings))!;
            options.ApplyTo(persisted); persisted.NarrationDefaults = options;
            await Workspace.SaveSettingsAsync(persisted);
            options.ApplyTo(Settings); Settings.NarrationDefaults = options; narrationDefaults = options;
        }
        finally { gate.Release(); }
    }
    public async Task CheckProviderAsync(ProviderRequest request, CancellationToken ct)
    {
        SpeechProviders.Get(request.Engine); await gate.WaitAsync(ct);
        try
        {
            if (SpeechProviders.IsHosted(request.Engine))
            {
                var config = new SpeechConfiguration(request.Model ?? SpeechProviders.Get(request.Engine).DefaultModel); config.Validate(request.Engine);
                Settings.SpeechDefaults[request.Engine] = config;
                if (!string.IsNullOrWhiteSpace(request.ApiKey)) speechSecrets.Set(request.Engine, request.ApiKey);
            }
            var info = await Speech.ReadyAsync(request.Engine, ct, true);
            Settings.Providers[request.Engine] = info;
            providerCatalog[request.Engine] = info;
            await Workspace.SaveSettingsAsync(Settings);
        }
        finally { gate.Release(); }
    }
    public async Task SaveDestinationAsync(DestinationRequest request, CancellationToken ct)
    {
        request.Destination.Validate(); await gate.WaitAsync(ct);
        try
        {
            if (!string.IsNullOrWhiteSpace(request.Secret)) WindowsWebSecrets.Set(WindowsWebSecrets.Target(request.Destination), request.Secret);
            var same = request.Destination == WebSettings.Destination;
            var updated = new WebSettings(request.Destination, request.AutoPublish, same ? WebSettings.FolderName : "", same ? WebSettings.FolderUrl : "");
            await Workspace.AtomicWriteAsync(ConfigPath, JsonSerializer.Serialize(updated)); WebSettings = updated;
        }
        finally { gate.Release(); }
    }
    public async Task RepairProviderAsync(string engine, CancellationToken ct)
    {
        if (SpeechProviders.IsHosted(engine)) throw new ArgumentException("Hosted providers use Save & check connection.");
        await gate.WaitAsync(ct);
        try
        {
            var repaired = await Speech.RepairAsync(engine, ct);
            Settings.Providers[engine] = repaired.Provider;
            providerCatalog[engine] = repaired.Provider;
            await Workspace.SaveSettingsAsync(Settings);
        }
        finally { gate.Release(); }
    }
    public async Task CheckDestinationAsync(bool createLink, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var folder = await publisher.CheckFolderAsync(WebSettings.Destination, createLink, ct);
            var updated = WebSettings with { FolderName = folder.Name, FolderUrl = folder.PublicUrl };
            await Workspace.AtomicWriteAsync(ConfigPath, JsonSerializer.Serialize(updated)); WebSettings = updated;
        }
        finally { gate.Release(); }
    }
    public async Task PauseAsync(bool paused, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try { Settings.QueuePaused = paused; await Workspace.SaveSettingsAsync(Settings); Queue.Paused = paused; }
        finally { gate.Release(); }
    }
    public async Task PublishAsync(string id, bool currentDestination, CancellationToken ct)
    {
        await publicationGate.WaitAsync(ct);
        try
        {
            var job = Find(id);
            if (job.Stage != JobStage.Exported) throw new ArgumentException("Wait for the MP3 to finish generation before publishing.");
            if (publications.TryGetValue(id, out var saved) && saved.Status == "Published") throw new ArgumentException("This narration is already published.");
            var destination = currentDestination || saved is null || !saved.Destination.Configured ? WebSettings.Destination : saved.Destination;
            destination.Validate();
            await SavePublicationAsync(new(id, "Pending", destination, destination == WebSettings.Destination ? WebSettings.FolderUrl : saved?.FolderUrl ?? ""));
        }
        finally { publicationGate.Release(); }
    }
    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        await publicationGate.WaitAsync(ct);
        try { Find(id); await Queue.DeleteAsync(id, true); publications.TryRemove(id, out _); }
        finally { publicationGate.Release(); }
    }
    private async Task SavePublicationAsync(PublishingRecord record)
    {
        await Workspace.AtomicWriteAsync(PublicationPath(record.JobId), JsonSerializer.Serialize(record));
        publications[record.JobId] = record;
    }
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await publicationGate.WaitAsync(stoppingToken);
                try
                {
                    foreach (var job in Queue.Snapshot().Where(j => j.Stage == JobStage.Exported && !j.DeletionRequested))
                    {
                        if (!publications.TryGetValue(job.Id, out var record) || record.Status != "Pending") continue;
                        if (!record.Destination.Configured)
                        {
                            await SavePublicationAsync(record with { Status = "Not configured", Error = "Set up Microsoft 365 in Settings, then publish this narration." }); continue;
                        }
                        await SavePublicationAsync(record with { Status = "Publishing", Error = "" });
                        try
                        {
                            var folder = await publisher.CheckFolderAsync(record.Destination, false, stoppingToken);
                            var receipt = await publisher.UploadAsync(record.Destination, Workspace.FinalPath(job), ExportPublisher.Filename(job), job.FinalHash, stoppingToken);
                            await SavePublicationAsync(record with { Status = "Published", Error = "", Receipt = receipt, FolderUrl = folder.PublicUrl, PublishedUtc = DateTimeOffset.UtcNow });
                        }
                        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                        catch (Exception error)
                        {
                            logger.LogWarning("Publishing failed for narration {JobId}: {ErrorType}", job.Id, error.GetType().Name);
                            await SavePublicationAsync(record with { Status = "Failed", Error = SafeError(error) });
                        }
                    }
                }
                finally { publicationGate.Release(); }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception error) { logger.LogError("Publication checkpoint failed: {ErrorType}", error.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch (OperationCanceledException) { break; }
        }
    }
    public static string SafeError(Exception error) => error is ArgumentException or IOException or TimeoutException ? error.Message : "The operation could not finish. Check server logs and configuration, then retry.";
    public override Task StopAsync(CancellationToken ct)
    {
        lock (stopSync) return stopping ??= StopCoreAsync(ct);
    }
    private async Task StopCoreAsync(CancellationToken ct)
    {
        try { await base.StopAsync(ct); }
        finally
        {
            try { if (Queue is not null) await Queue.DisposeAsync(); }
            finally
            {
                Speech?.Dispose(); lease?.Dispose();
                if (configuration.GetValue("CommuteCast:OperationalLogging", true)) AppLogging.Stop();
            }
        }
    }
}
