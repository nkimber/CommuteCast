using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;

namespace CommuteCast.Infrastructure;

// This runtime fence deliberately survives workspace restore: it describes the
// current server, not narration history from a backup. It contains no source text.
internal sealed record PendingSpeechAdmission(int Version, string Engine, string Image, string Fingerprint, string Instance, long Sequence)
{
    internal const string FileName = "speech-admission.json";
    internal static string PathFor(Workspace workspace) => OwnedFileRemoval.Resolve(workspace.Root, FileName);
    internal void Validate()
    {
        if (Version != 1 || Engine is not ("kokoro" or "piper") ||
            !Regex.IsMatch(Image, "^sha256:[a-f0-9]{64}$") ||
            !Regex.IsMatch(Fingerprint, "^" + Engine + ":contract-v1:[a-fA-F0-9]{64}$") ||
            !Regex.IsMatch(Instance, "^[a-f0-9]{32}$") || Sequence is < 1 or > 9007199254740991)
            throw new IOException("The pending speech reservation is incompatible. Preserve it for inspection; generation is blocked.");
    }
    internal static async Task<(PendingSpeechAdmission Admission, string Hash)?> LoadAsync(Workspace workspace, CancellationToken ct)
    {
        var path = PathFor(workspace);
        if (Directory.Exists(path)) throw new IOException("The pending speech reservation is a directory. Preserve it for inspection; generation is blocked.");
        if (!File.Exists(path)) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
        if (file.Length > 16384) throw new IOException("The pending speech reservation is oversized. Preserve it for inspection; generation is blocked.");
        var bytes = new byte[checked((int)file.Length)]; await file.ReadExactlyAsync(bytes, ct);
        PendingSpeechAdmission admission;
        try { admission = JsonSerializer.Deserialize<PendingSpeechAdmission>(bytes) ?? throw new JsonException(); admission.Validate(); }
        catch (Exception error) when (error is JsonException or ArgumentNullException)
        { throw new IOException("The pending speech reservation is unreadable. Preserve it for inspection; generation is blocked.", error); }
        return (admission, Convert.ToHexString(SHA256.HashData(bytes)));
    }
    internal async Task SaveAsync(Workspace workspace, CancellationToken ct)
    {
        Validate(); ct.ThrowIfCancellationRequested();
        var path = PathFor(workspace);
        if (File.Exists(path)) throw new IOException("A previous speech reservation remains unresolved. No new text was sent.");
        var temporary = path + ".pending-" + Guid.NewGuid().ToString("N");
        await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, true))
        {
            await JsonSerializer.SerializeAsync(file, this, cancellationToken: ct);
            await file.FlushAsync(ct); file.Flush(true);
        }
        ct.ThrowIfCancellationRequested();
        File.Move(temporary, path, false);
    }
}
