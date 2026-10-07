using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

internal static class PreviewRemovalFixture
{
    internal static async Task RunAsync(string command, Workspace workspace)
    {
        var ledger = new AuditionOwnershipStore(workspace);
        if (command == "seed-preview-removal")
        {
            if ((await ledger.LoadAsync()).Count != 0) throw new IOException("Use a fixture without existing preview ownership.");
            var journal = new AuditionWriteJournal(Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, []);
            var directory = Path.Combine(workspace.Root, "auditions"); Directory.CreateDirectory(directory);
            var name = "audition-" + journal.Id + ".wav";
            await using var held = ExportStagingFile.Create(directory, name);
            journal.Artifacts.Add(new(name, "", CreationIdentity: held.Identity)); await ledger.SaveAsync(journal);
            WaveAudio.WriteHeader(held.Stream, 24000); await held.Stream.WriteAsync(new byte[48000]); await held.Stream.FlushAsync();
            var hash = await held.HashAsync(default);
            journal.Artifacts.Clear(); journal.Artifacts.Add(new(name, hash, PromotionIdentity: held.Identity)); await ledger.SaveAsync(journal);
            Console.WriteLine(JsonSerializer.Serialize(new { journal.Id, path = Path.Combine(directory, name), hash }));
            return;
        }
        using var gate = new SemaphoreSlim(1);
        var count = await new AuditionGenerator(workspace, new NeverProvider(), gate).RecoverAsync();
        if (count != 1 || (await ledger.LoadAsync()).Count != 0) throw new IOException("The recorded fixture preview was not reconciled.");
        Console.WriteLine(JsonSerializer.Serialize(new { reconciled = count }));
    }
    private sealed class NeverProvider : ISpeechProvider
    {
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => throw new IOException("Preview reconciliation must not contact speech.");
        public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct) => throw new IOException("Preview reconciliation must not synthesize.");
    }
}
