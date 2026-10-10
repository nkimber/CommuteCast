using System.Text.Json;
using CommuteCast.Core;

namespace CommuteCast.Infrastructure;

public record Draft(string Title, string Source, NarrationPromptDraft? PromptDraft = null);
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
            if (saved?.Length is not (2 or 3) || saved.Any(value => value is null)) throw new JsonException();
            NarrationPromptDraft? prompt = null;
            if (saved.Length == 3)
            {
                prompt = JsonSerializer.Deserialize<NarrationPromptDraft>(saved[2]) ?? throw new JsonException();
                prompt.ValidateStorage();
            }
            return new(saved[0], saved[1], prompt);
        }
        catch (Exception error) when (error is JsonException or ArgumentException) { throw new IOException("The saved draft is unreadable. Its original file was preserved. New edits will first preserve it in local recovery storage.", error); }
    }
    public async Task<string?> SaveAsync(Draft draft, CancellationToken ct = default)
    {
        draft.PromptDraft?.ValidateStorage();
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
            var values = draft.PromptDraft is null ? new[] { draft.Title, draft.Source }
                : new[] { draft.Title, draft.Source, JsonSerializer.Serialize(draft.PromptDraft) };
            await Workspace.AtomicWriteAsync(PathForDraft, JsonSerializer.Serialize(values));
            return preserved;
        }
        finally { gate.Release(); }
    }
}
