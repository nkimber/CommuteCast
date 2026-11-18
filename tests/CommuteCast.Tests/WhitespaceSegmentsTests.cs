using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class WhitespaceSegmentsTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LeadingBlankLinesOrExcludedCodeStayWithTheFirstSpokenSegment(bool natural, bool excludeCode)
    {
        var source = (excludeCode ? "~~~python\nprint(42)\n~~~\n" : "\n\n") + "Retain this complete sentence.";
        var prepared = TextPreparation.Prepare(source, excludeCode, profile: natural ? new() : null);
        var chunks = natural ? NaturalChunker.Split(prepared.Script) : Chunker.Split(prepared.Script, 450);

        Assert.True(Assert.Single(chunks).HasSpeech);
        Assert.Equal(source, string.Concat(prepared.Spans.Select(span => span.Original)));
        Assert.Equal(prepared.Script, string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.DoesNotContain("print", prepared.Script);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LongBlankRegionsRetainCoverageAndOnlySendSpeechToTheProvider(bool natural)
    {
        using var test = new TestWorkspace();
        var store = new SqliteJobStore(test.Workspace);
        var provider = new RecordingProvider();
        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store));
        await queue.InitializeAsync();
        var blank = new string('\n', 3000);
        var job = CreateJob(test, blank + "First paragraph." + blank + "Second paragraph." + blank, natural);
        await queue.AddAsync(job);

        var saved = await SettleAsync(queue);
        Assert.True(saved.Stage == JobStage.Exported, saved.Error);
        Assert.Equal(new[] { "First paragraph.", "Second paragraph." }, provider.Texts.Select(text => text.Trim()));
        Assert.Contains(saved.Chunks, chunk => !chunk.HasSpeech);
        Assert.Equal(saved.Chunks.Where(chunk => chunk.HasSpeech).Select(chunk => chunk.Index), saved.Receipts.Select(receipt => receipt.Index));
        Assert.Equal(saved.Source, string.Concat(saved.Prepared.Spans.Select(span => span.Original)));
        Assert.Equal(saved.Prepared.Script, string.Concat(saved.Chunks.Select(chunk => chunk.Text)));
        Assert.Equal(2, saved.CompletedChunks);
        Assert.Equal(2, saved.SpeechChunkCount);
        Assert.InRange(saved.DurationSeconds, natural ? 2.44 : 2.1, natural ? 2.6 : 2.25);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SavedBlankSegmentsCanBeRetriedWithoutReplacingValidatedSpeech(bool previousCompleteAudio)
    {
        using var test = new TestWorkspace();
        var store = new SqliteJobStore(test.Workspace);
        var provider = new RecordingProvider();
        string[] spans = ["\n\n", "First paragraph.\n", "\n", "Second paragraph.\n", "\n"];
        var job = CreateJob(test, string.Concat(spans), natural: true);
        var offset = 0;
        foreach (var text in spans)
        {
            job.Chunks.Add(new(job.Chunks.Count, offset, text.Length, text, false));
            offset += text.Length;
        }
        job.Stage = JobStage.Failed;
        var firstSpeechPath = test.Workspace.ChunkPath(job, 1);
        TestWorkspace.WriteWave(firstSpeechPath);
        var first = new ChunkReceipt(1, await Workspace.HashFileAsync(firstSpeechPath), job.Fingerprint, 1);
        job.Receipts.Add(first);
        if (previousCompleteAudio)
        {
            foreach (var index in new[] { 0, 2, 3, 4 })
            {
                var path = test.Workspace.ChunkPath(job, index);
                TestWorkspace.WriteWave(path);
                job.Receipts.Add(new(index, await Workspace.HashFileAsync(path), job.Fingerprint, 1));
            }
            // The old plan considered the response for the blank region part of the finished audio.
            var final = test.Workspace.FinalPath(job);
            await File.WriteAllTextAsync(final, "Earlier output containing audio for a blank region");
            job.FinalHash = await Workspace.HashFileAsync(final);
        }
        job.CompletedChunks = job.Receipts.Count;
        await store.SaveAsync(job);

        await using var queue = new QueueCoordinator(test.Workspace, store, provider, new(new()), new(test.Workspace, store));
        await queue.InitializeAsync();
        await queue.RetryAsync(job.Id);
        var saved = await SettleAsync(queue);

        Assert.True(saved.Stage == JobStage.Exported, saved.Error);
        if (previousCompleteAudio) Assert.Empty(provider.Texts);
        else Assert.Equal("Second paragraph.", Assert.Single(provider.Texts).Trim());
        Assert.Equal(new[] { 1, 3 }, saved.Receipts.Select(receipt => receipt.Index));
        Assert.Equal(first, saved.Receipts[0]);
        Assert.Equal(first.Hash, await Workspace.HashFileAsync(firstSpeechPath));
        Assert.Equal(job.Fingerprint, saved.Fingerprint);
        Assert.Equal(job.Chunks, saved.Chunks);
        Assert.NotEqual(job.FinalHash, saved.FinalHash);
        Assert.Single(Directory.GetFiles(test.Destination, "*.mp3"));
    }

    private static Job CreateJob(TestWorkspace test, string source, bool natural)
    {
        var job = new Job
        {
            Title = "Whitespace fixture", Source = source, Prepared = TextPreparation.Prepare(source), Destination = test.Destination,
            Settings = new("kokoro", "af_heart", 1, false, "", "fixture", LocalVoice: natural ? new() : null)
        };
        NaturalChunker.ConfigureNew(job);
        return job;
    }

    private static async Task<Job> SettleAsync(QueueCoordinator queue)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            var job = Assert.Single(queue.Snapshot());
            if (job.Stage is JobStage.Exported or JobStage.Failed) return job;
            await Task.Delay(50, deadline.Token);
        }
    }

    private sealed class RecordingProvider : ISpeechProvider
    {
        public List<string> Texts { get; } = [];

        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) =>
            Task.FromResult(new ProviderInfo(engine, "fixture", ["af_heart"], "ready", 0));

        public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct)
        {
            Assert.False(string.IsNullOrWhiteSpace(text));
            Texts.Add(text);
            TestWorkspace.WriteWave(output);
            return Task.CompletedTask;
        }
    }
}
