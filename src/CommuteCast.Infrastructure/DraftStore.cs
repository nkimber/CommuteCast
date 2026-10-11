using System.Text.Json;
using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public record PodcastDraft(bool Enabled, PodcastEpisode Episode);
public record VersionDraft(NarrationVersion Version, NarrationSettings Settings);
public record Draft(string Title, string Source, NarrationPromptDraft? PromptDraft = null, PodcastDraft? Podcast = null, VersionDraft? Version = null);
public sealed class DraftStore(Workspace workspace)
{
    private readonly SemaphoreSlim gate = new(1);
    private string PathForDraft => Path.Combine(workspace.Root, "draft.json");
    public async Task<Draft> LoadAsync(CancellationToken ct = default)
    {
        SqliteSchema.RejectLink(PathForDraft);
        if (!File.Exists(PathForDraft)) return new("", "");
        if (new FileInfo(PathForDraft).Length > 4 * 1048576) throw new IOException("The saved draft is too large to load safely. Its original file was preserved.");
        try
        {
            var saved = JsonSerializer.Deserialize<string[]>(await File.ReadAllTextAsync(PathForDraft, ct));
            if (saved?.Length is not (2 or 3 or 4 or 5) || saved.Any(value => value is null)) throw new JsonException();
            NarrationPromptDraft? prompt = null;
            if (saved.Length == 3 && saved[2] == "null") throw new JsonException();
            if (saved.Length >= 3 && saved[2] != "null")
            {
                prompt = JsonSerializer.Deserialize<NarrationPromptDraft>(saved[2]) ?? throw new JsonException();
                prompt.ValidateStorage();
            }
            var podcast = saved.Length >= 4 && saved[3] != "null" ? JsonSerializer.Deserialize<PodcastDraft>(saved[3]) ?? throw new JsonException() : null;
            if (saved.Length == 4 && podcast is null) throw new JsonException();
            if (podcast is not null && podcast.Episode is null) throw new JsonException();
            podcast?.Episode.ValidateStorage();
            var version = saved.Length == 5 ? JsonSerializer.Deserialize<VersionDraft>(saved[4]) ?? throw new JsonException() : null;
            ValidateVersion(version);
            return new(saved[0], saved[1], prompt, podcast, version);
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { throw new IOException("The saved draft is unreadable. Its original file was preserved. New edits will first preserve it in local recovery storage.", error); }
    }
    public async Task<string?> SaveAsync(Draft draft, CancellationToken ct = default)
    {
        draft.PromptDraft?.ValidateStorage();
        draft.Podcast?.Episode.ValidateStorage();
        ValidateVersion(draft.Version);
        await gate.WaitAsync(ct);
        try
        {
            string? preserved = null;
            try { await LoadAsync(ct); }
            catch (IOException) when (File.Exists(PathForDraft))
            {
                SqliteSchema.RejectLink(PathForDraft);
                var recovery = Path.Combine(workspace.Root, "recovery", "drafts"); Workspace.RejectReparsePoints(recovery); Directory.CreateDirectory(recovery);
                preserved = Path.Combine(recovery, Guid.NewGuid().ToString("N") + ".json");
                await using (var input = new FileStream(PathForDraft, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true))
                await using (var output = new FileStream(preserved, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
                { await input.CopyToAsync(output, ct); output.Flush(true); }
            }
            ct.ThrowIfCancellationRequested();
            var values = draft.Version is not null ? new[] { draft.Title, draft.Source, JsonSerializer.Serialize(draft.PromptDraft), JsonSerializer.Serialize(draft.Podcast), JsonSerializer.Serialize(draft.Version) }
                : draft.Podcast is not null ? new[] { draft.Title, draft.Source, JsonSerializer.Serialize(draft.PromptDraft), JsonSerializer.Serialize(draft.Podcast) }
                : draft.PromptDraft is null ? new[] { draft.Title, draft.Source }
                : new[] { draft.Title, draft.Source, JsonSerializer.Serialize(draft.PromptDraft) };
            await Workspace.AtomicWriteAsync(PathForDraft, JsonSerializer.Serialize(values));
            return preserved;
        }
        finally { gate.Release(); }
    }
    private static void ValidateVersion(VersionDraft? draft)
    {
        if (draft is not null && (draft.Version is null || draft.Settings is null || draft.Version.Number < 2 ||
            string.IsNullOrWhiteSpace(draft.Version.RootJobId) || string.IsNullOrWhiteSpace(draft.Version.ParentJobId) ||
            draft.Version.BaseTitle is null || string.IsNullOrWhiteSpace(draft.Version.ParentSourceHash)))
            throw new ArgumentException("The saved narration version is incomplete.");
        if (draft is not null && (!SpeechProviders.IsKnown(draft.Settings.Engine) || string.IsNullOrWhiteSpace(draft.Settings.Voice) ||
            draft.Settings.Pronunciation is null || !double.IsFinite(draft.Settings.Speed) || draft.Settings.Speed is < .7 or > 1.4))
            throw new ArgumentException("The saved narration version settings are invalid.");
    }
}
