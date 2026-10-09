using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CommuteCast.Tests;

public class AudioSequenceTests
{
    [Theory]
    [InlineData("letters")]
    [InlineData("digits")]
    [InlineData("symbols")]
    public async Task LongUnbrokenTokensReceiveCharacterAllowanceAndRemainBounded(string kind)
    {
        var text = new string(kind == "letters" ? 'x' : kind == "digits" ? '7' : '#', 250);
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "long-token.wav");
        TestWorkspace.WriteWave(path, 60);
        var audio = new AudioPipeline(new()); await audio.ValidateChunkAsync(path, text, default);
        TestWorkspace.WriteWave(path, 122);
        var error = await Assert.ThrowsAsync<IOException>(() => audio.ValidateChunkAsync(path, text, default));
        Assert.Contains("250 non-whitespace characters (longest token 250)", error.Message);
        Assert.Contains("expected 0.080–121.500 seconds", error.Message);
    }

    [Fact]
    public async Task CharacterAllowanceAddsToWordBudgetAndHasAnAbsoluteCeiling()
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "combined.wav");
        var audio = new AudioPipeline(new());
        var text = string.Join(" ", Enumerable.Repeat("short", 8)) + " " + new string('x', 100);
        TestWorkspace.WriteWave(path, 66.5); await audio.ValidateChunkAsync(path, text, default);
        TestWorkspace.WriteWave(path, 67);
        var error = await Assert.ThrowsAsync<IOException>(() => audio.ValidateChunkAsync(path, text, default));
        Assert.Contains("expected 0.080–66.500 seconds", error.Message);
        TestWorkspace.WriteWave(path, 301);
        error = await Assert.ThrowsAsync<IOException>(() => audio.ValidateChunkAsync(path, new string('x', 900), default));
        Assert.Contains("expected 0.080–300.000 seconds", error.Message);
    }

    [Fact]
    public void UnexpectedLongStringsAreSplitWithoutLossAndKeepUnicodeIntact()
    {
        var text = new string('x', 449) + "😀" + new string('7', 900);
        var chunks = Chunker.Split(text, 450);
        Chunker.ValidateManifest(chunks, text);
        Assert.Equal(text, string.Concat(chunks.Select(chunk => chunk.Text)));
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 1, 450));
        Assert.Contains(chunks, chunk => chunk.HardSplit);
    }

    private const string UrlHeavyChunk = "(https://www.buffalobills.com/news/important-dates-in-bills-history-jan-20-1991-bills-beat-raiders-51-3-in-18477088), NFL championship summaries (https://operations.nfl.com/media/3823/2019-nfl-record-and-fact-book.pdf), the Comeback (https://www.buffalobills.com/news/20-years-later-the-comeback-game-9267435).";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UrlComponentsAllowReportedDurationWithoutRequiringEveryComponentToBeSpoken(bool uppercase)
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "urls.wav");
        var text = uppercase ? UrlHeavyChunk.ToUpperInvariant() : UrlHeavyChunk;
        Assert.Equal(8, UrlHeavyChunk.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length);
        TestWorkspace.WriteWave(path, 38.534);
        var audio = new AudioPipeline(new());
        Assert.Equal(WaveAudio.Inspect(path), await audio.ValidateChunkAsync(path, text, default));
        // Providers may pronounce or abbreviate URLs differently; the URL estimate is upper-only.
        TestWorkspace.WriteWave(path, 1);
        await audio.ValidateChunkAsync(path, text, default);
    }

    [Theory]
    [InlineData("One two three four five six seven eight", 38.534)]
    [InlineData(UrlHeavyChunk, 200)]
    [InlineData("See HTTPS://example.com/news/long-technical-address", 40)]
    public async Task ExcessiveDurationStillFailsForPlainTextAndUrls(string text, double duration)
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "excess.wav");
        TestWorkspace.WriteWave(path, duration);
        await Assert.ThrowsAsync<IOException>(() => new AudioPipeline(new()).ValidateChunkAsync(path, text, default));
    }

    [Fact]
    public async Task UrlDurationAllowanceStillRejectsSilence()
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "silence.wav");
        Directory.CreateDirectory(test.Workspace.Root);
        using (var file = File.Create(path))
        {
            WaveAudio.WriteHeader(file, 24000 * 39); file.Write(new byte[24000 * 39 * 2]);
        }
        var error = await Assert.ThrowsAsync<IOException>(() => new AudioPipeline(new()).ValidateChunkAsync(path, UrlHeavyChunk, default));
        Assert.Contains("silence", error.Message);
    }

    [Theory]
    [InlineData(1, 0.079, "0.080", "30.000")]
    [InlineData(13, 0.90, "0.910", "32.500")]
    [InlineData(15, 39.671, "1.050", "37.500")]
    public async Task ImplausibleDurationReportsMeasuredAndExpectedValues(int words, double duration, string minimum, string maximum)
    {
        using var test = new TestWorkspace();
        var path = Path.Combine(test.Workspace.Root, "duration.wav"); TestWorkspace.WriteWave(path, duration);
        var text = string.Join(" ", Enumerable.Repeat("privateword", words));
        var error = await Assert.ThrowsAsync<IOException>(() => new AudioPipeline(new()).ValidateChunkAsync(path, text, default));
        Assert.Contains(FormattableString.Invariant($"measured {WaveAudio.Inspect(path).Duration:F3} seconds for {words} whitespace-delimited words"), error.Message);
        Assert.Contains($"expected {minimum}–{maximum} seconds", error.Message);
        Assert.DoesNotContain("privateword", error.Message);
        Assert.DoesNotContain(path, error.Message);
    }

    [Theory]
    [InlineData(1, 0.08)]
    [InlineData(12, 0.08)]
    [InlineData(13, 0.911)]
    [InlineData(1, 30)]
    [InlineData(13, 32.5)]
    [InlineData(16, 39.671)]
    public async Task PlausibleDurationStillPassesIncludingBoundaries(int words, double duration)
    {
        using var test = new TestWorkspace();
        var path = Path.Combine(test.Workspace.Root, "duration.wav"); TestWorkspace.WriteWave(path, duration);
        var info = await new AudioPipeline(new()).ValidateChunkAsync(path, string.Join(" ", Enumerable.Repeat("word", words)), default);
        Assert.Equal(WaveAudio.Inspect(path).Samples, info.Samples);
    }

    [Fact] public async Task NormalizationPersistsExclusiveCreationThenCanonicalHashBeforeReturning()
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "input.wav"); TestWorkspace.WriteWave(input);
        var output = Path.Combine(directory, "normalized.partial.wav"); var saves = 0;
        await new AudioPipeline(new()).NormalizeAsync(job, input, output, async () =>
        {
            await store.SaveAsync(job); var receipt = Assert.Single(Assert.Single(await store.LoadAsync()).PrivateArtifacts);
            Assert.Throws<IOException>(() => File.WriteAllText(output, "unknown replacement"));
            if (++saves == 1) { Assert.NotNull(receipt.CreationIdentity); Assert.Equal("", receipt.Hash); }
            else { Assert.Null(receipt.CreationIdentity); Assert.Equal(64, receipt.Hash.Length); }
        }, default);
        Assert.Equal(2, saves); Assert.Equal(24000, WaveAudio.Inspect(output).Samples);
        Assert.Equal(await Workspace.HashFileAsync(output), PrivateJobFiles.Inventory(Assert.Single(await store.LoadAsync()))["normalized.partial.wav"]);
        var original = await File.ReadAllBytesAsync(input);
        await Assert.ThrowsAsync<IOException>(() => new AudioPipeline(new()).NormalizeAsync(input, output, default));
        Assert.Equal(original, await File.ReadAllBytesAsync(input)); Assert.Equal(24000, WaveAudio.Inspect(output).Samples);
    }

    [Fact] public async Task EncoderPersistsCreationUnderExclusiveHandleAndRetainsDurableFinalReceipt()
    {
        using var test = new TestWorkspace(); var job = await ToneJobAsync(test); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); var observed = false;
        await new AudioPipeline(new()).AssembleAsync(job, directory, default, async () =>
        {
            await store.SaveAsync(job);
            if (!job.PrivateArtifacts.Any(r => r.RelativePath == "encoded.partial.mp3" && r.CreationIdentity is not null)) return;
            var saved = Assert.Single(await store.LoadAsync()); Assert.NotNull(saved.PrivateArtifacts.Single(r => r.RelativePath == "encoded.partial.mp3").CreationIdentity);
            Assert.Throws<IOException>(() => File.WriteAllText(Path.Combine(directory, "encoded.partial.mp3"), "replacement")); observed = true;
        });
        Assert.True(observed); var reopened = Assert.Single(await store.LoadAsync());
        Assert.DoesNotContain(reopened.PrivateArtifacts, r => r.CreationIdentity is not null || r.PromotionIdentity is not null);
        Assert.Equal(await Workspace.HashFileAsync(test.Workspace.FinalPath(job)), PrivateJobFiles.Inventory(reopened)["complete.mp3"]);
    }

    [Fact] public async Task FailedNormalizerDeletesItsHeldPartialAndAllowsDurableRetry()
    {
        using var test = new TestWorkspace(); var job = new Job(); var store = new SqliteJobStore(test.Workspace);
        var directory = test.Workspace.JobDirectory(job.Id); Directory.CreateDirectory(directory);
        var input = Path.Combine(directory, "input.wav"); TestWorkspace.WriteWave(input);
        var output = Path.Combine(directory, "normalized.partial.wav"); var unknown = Path.Combine(directory, "unknown.txt"); await File.WriteAllTextAsync(unknown, "preserve");
        await Assert.ThrowsAsync<IOException>(() => new AudioPipeline(new() { Ffmpeg = "missing-commutecast-tool.exe" }).NormalizeAsync(job, input, output, () => store.SaveAsync(job), default));
        Assert.False(File.Exists(output)); var retry = Assert.Single(await store.LoadAsync());
        Assert.NotNull(Assert.Single(retry.PrivateArtifacts).CreationIdentity);
        await new AudioPipeline(new()).NormalizeAsync(retry, input, output, () => store.SaveAsync(retry), default);
        Assert.Equal(24000, WaveAudio.Inspect(output).Samples); Assert.Equal("preserve", await File.ReadAllTextAsync(unknown));
    }

    [Fact] public async Task OrdinalTonesSurviveOneMp3EncodeInOrderWithMetadataAndFrameCount()
    {
        using var test = new TestWorkspace(); var job = await ToneJobAsync(test); var audio = new AudioPipeline(new());
        await audio.AssembleAsync(job, test.Workspace.JobDirectory(job.Id), default);
        var info = await audio.ValidateFinalAsync(job, test.Workspace.FinalPath(job), default);
        Assert.InRange(info.Duration, 3.3, 3.5);
        var decoded = Path.Combine(test.Workspace.Root, "decoded.wav");
        var result = await ProcessRunner.RunAsync("ffmpeg", ["-v", "error", "-y", "-i", test.Workspace.FinalPath(job), "-ar", "24000", "-ac", "1", "-c:a", "pcm_s16le", decoded], TimeSpan.FromSeconds(10)); Assert.Equal(0, result.ExitCode);
        var decodedInfo = WaveAudio.Inspect(decoded); Assert.Equal(79200, decodedInfo.Samples);
        for (var ordinal = 0; ordinal < 3; ordinal++)
        {
            var powers = new[] { 220, 440, 660 }.Select(f => TonePower(decoded, ordinal * 1.15 + .25, f)).ToArray();
            Assert.True(powers[ordinal] > powers.Where((_, i) => i != ordinal).Max() * 100, "Each segment must contain its expected ordinal tone.");
        }
        var metadata = await ProcessRunner.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format_tags=title,artist,date,comment,commutecast_created_utc,commutecast_job_id", "-of", "json", test.Workspace.FinalPath(job)], TimeSpan.FromSeconds(10)); Assert.Equal(0, metadata.ExitCode);
        using var document = JsonDocument.Parse(metadata.Output); var tags = document.RootElement.GetProperty("format").GetProperty("tags");
        Assert.Equal(job.Title, tags.GetProperty("title").GetString()); Assert.Equal("CommuteCast", tags.GetProperty("artist").GetString());
        Assert.Equal(job.CreatedUtc.ToString("yyyy"), tags.GetProperty("date").GetString());
        Assert.Equal(job.CreatedUtc.ToString("O"), tags.GetProperty("commutecast_created_utc").GetString()); Assert.Equal(job.Id, tags.GetProperty("commutecast_job_id").GetString());
        var comment = tags.GetProperty("comment").GetString();
        Assert.Contains("Speech model: Kokoro\r\n", comment);
        Assert.Contains("Voice: af_heart\r\nPace: 1x", comment);
        Assert.Contains("Provider image: not recorded (legacy job)", comment);
        Assert.Contains("Pronunciation profile: legacy literal substitutions", comment);
        Assert.Contains("Custom pronunciation rules: 0", comment);
        Assert.Contains("Segments: 3; inserted join pauses: 2 at 150 ms; expected audio duration: 3.300 seconds", comment);
        Assert.Contains("Job ID: " + job.Id, comment);
        Assert.Equal(comment, ReadStandardComment(test.Workspace.FinalPath(job)));
    }

    [Theory]
    [InlineData("kokoro", "af_heart", true)]
    [InlineData("kokoro", "bf_emma", true)]
    [InlineData("piper", "en_US-lessac-medium", true)]
    [InlineData("piper", "en_US-lessac-medium", false)]
    public async Task ExportedCommentsRetainFrozenSpeechAndPreparationSettings(string engine, string voice, bool useProfile)
    {
        using var test = new TestWorkspace(); var job = await ToneJobAsync(test);
        job.Destination = test.Destination;
        var profile = useProfile ? new PronunciationProfile(Numbers: NumberReading.ScientificWords,
            Acronyms: AcronymReading.SpellUppercaseWords, Dates: DateReading.DayMonthYear) : null;
        const string dictionary = "PRIVATE=confidential pronunciation\nAPI=A P I";
        job.Settings = new(engine, voice, 1.125, true, dictionary, engine + ":contract-v1:" + new string('a', 64), profile,
            "sha256:" + new string('b', 64));
        job.Source = "Private source: API 24 on 03/04/2026.";
        // Keep the tone manifest while capturing the preparation's frozen dictionary review.
        var prepared = TextPreparation.Prepare(job.Source, true, dictionary, profile);
        job.Prepared = job.Prepared with { Version = prepared.Version, ProfileReview = prepared.ProfileReview };
        job.Receipts = job.Receipts.Select(r => r with { Fingerprint = job.Fingerprint }).ToList();
        var currentSettings = new AppSettings { Engine = "other-engine", Voice = "other-voice", Speed = .7 };
        var audio = new AudioPipeline(currentSettings);
        var previousCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
            await audio.AssembleAsync(job, test.Workspace.JobDirectory(job.Id), default);
        }
        finally { CultureInfo.CurrentCulture = previousCulture; }
        var info = await audio.ValidateFinalAsync(job, test.Workspace.FinalPath(job), default);
        job.DurationSeconds = info.Duration;
        job.FinalHash = await Workspace.HashFileAsync(test.Workspace.FinalPath(job));
        await new ExportPublisher(test.Workspace, new SqliteJobStore(test.Workspace)).PublishAsync(job, default);
        var path = Path.Combine(job.Destination, job.ExportName);
        var result = await ProcessRunner.RunAsync("ffprobe", ["-v", "error", "-show_entries", "format_tags=comment", "-of", "json", path], TimeSpan.FromSeconds(10));
        Assert.Equal(0, result.ExitCode);
        using var document = JsonDocument.Parse(result.Output);
        var comment = document.RootElement.GetProperty("format").GetProperty("tags").GetProperty("comment").GetString()!;
        Assert.Equal(comment, ReadStandardComment(path));
        Assert.Equal(comment, ReadWindowsComment(path));
        Assert.Contains("Speech model: " + (engine == "kokoro" ? "Kokoro" : "Piper (en_US-lessac-medium)"), comment);
        Assert.Contains("Speech engine: " + engine, comment);
        Assert.Contains("Voice: " + voice + "\r\nPace: 1.125x", comment);
        Assert.Contains("Provider/model fingerprint: " + job.Settings.ProviderFingerprint, comment);
        Assert.Contains("Provider image: " + job.Settings.ProviderImageId, comment);
        Assert.Contains("Code blocks excluded: yes", comment);
        Assert.Contains("Speech language: " + (engine == "piper" ? "en-US" : voice.StartsWith('b') ? "en-gb" : "en-us"), comment);
        if (engine == "piper") Assert.Contains("Piper length scale: " + (1 / job.Settings.Speed).ToString(CultureInfo.InvariantCulture), comment);
        Assert.Contains("Custom pronunciation rules: 2", comment);
        Assert.Contains("Pronunciation dictionary revision: " + (prepared.ProfileReview?.DictionaryRevision ?? Job.Hash(dictionary)), comment);
        if (useProfile)
        {
            Assert.Contains("Pronunciation profile: pronunciation-v2; language: en", comment);
            Assert.Contains("Number reading: ScientificWords; acronym reading: SpellUppercaseWords; date reading: DayMonthYear", comment);
            Assert.Contains("Dictionary format: literal-dictionary-v1", comment);
        }
        else Assert.Contains("Pronunciation profile: legacy literal substitutions", comment);
        Assert.Contains("Audio: MP3; libmp3lame; 128 kbps; 24000 Hz; mono; one encode from 16-bit PCM", comment);
        Assert.Contains("Preparation: " + prepared.Version + "; chunking: " + job.ChunkingVersion + "; audio contract: " + job.AudioContractVersion, comment);
        Assert.Contains("Queued UTC: " + job.CreatedUtc.ToString("O"), comment);
        Assert.DoesNotContain("other-engine", comment); Assert.DoesNotContain("other-voice", comment);
        Assert.DoesNotContain(job.Source, comment); Assert.DoesNotContain("confidential pronunciation", comment);
        Assert.DoesNotContain(job.Destination, comment);
        Assert.Equal(job.FinalHash, await Workspace.HashFileAsync(path));
    }

    private static string ReadWindowsComment(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("MP3 Comments acceptance requires Windows.");
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        dynamic? folder = null; dynamic? item = null;
        try
        {
            folder = shell.NameSpace(Path.GetDirectoryName(path)!);
            item = folder.ParseName(Path.GetFileName(path));
            return (string)item.ExtendedProperty("System.Comment");
        }
        finally
        {
            if (item is not null) Marshal.FinalReleaseComObject(item);
            if (folder is not null) Marshal.FinalReleaseComObject(folder);
            Marshal.FinalReleaseComObject(shell);
        }
    }

    private static string ReadStandardComment(string path)
    {
        using var file = File.OpenRead(path); var header = new byte[10]; file.ReadExactly(header);
        Assert.Equal("ID3", Encoding.ASCII.GetString(header, 0, 3)); Assert.Equal(3, header[3]);
        var size = (header[6] << 21) | (header[7] << 14) | (header[8] << 7) | header[9];
        var tag = new byte[size]; file.ReadExactly(tag); var comments = new List<string>();
        for (var offset = 0; offset + 10 <= tag.Length && tag[offset] != 0;)
        {
            var length = BinaryPrimitives.ReadInt32BigEndian(tag.AsSpan(offset + 4));
            if (Encoding.ASCII.GetString(tag, offset, 4) == "COMM")
            {
                var body = tag.AsSpan(offset + 10, length);
                Assert.Equal(1, body[0]); Assert.Equal("eng", Encoding.ASCII.GetString(body.Slice(1, 3)));
                Assert.Equal(new byte[] { 0xff, 0xfe, 0, 0, 0xff, 0xfe }, body.Slice(4, 6).ToArray());
                comments.Add(Encoding.Unicode.GetString(body[10..]).TrimEnd('\0'));
            }
            offset += 10 + length;
        }
        return Assert.Single(comments);
    }
    [Theory] [InlineData("missing")] [InlineData("duplicate")] [InlineData("reordered")] [InlineData("ordinal")] [InlineData("offset")] [InlineData("receipt")] [InlineData("sample-count")] [InlineData("corruption")]
    public async Task InvalidSequenceCannotProduceACompletedMp3(string defect)
    {
        using var test = new TestWorkspace(); var job = await ToneJobAsync(test);
        switch (defect)
        {
            case "missing": job.Chunks.RemoveAt(1); break;
            case "duplicate": job.Chunks.Insert(1, job.Chunks[0]); break;
            case "reordered": (job.Chunks[0], job.Chunks[1]) = (job.Chunks[1], job.Chunks[0]); break;
            case "ordinal": job.Chunks[0] = job.Chunks[0] with { Index = 1 }; job.Chunks[1] = job.Chunks[1] with { Index = 0 }; break;
            case "offset": job.Chunks[1] = job.Chunks[1] with { Start = 0 }; break;
            case "receipt": job.Receipts[1] = job.Receipts[1] with { Index = 0 }; break;
            case "sample-count": job.Receipts[1] = job.Receipts[1] with { Duration = 1.01 }; break;
            case "corruption": TestWorkspace.WriteWave(test.Workspace.ChunkPath(job, 1), 1); break;
        }
        await Assert.ThrowsAsync<IOException>(() => new AudioPipeline(new()).AssembleAsync(job, test.Workspace.JobDirectory(job.Id), default));
        Assert.False(File.Exists(test.Workspace.FinalPath(job))); Assert.Empty(Directory.GetFiles(test.Destination, "*.mp3"));
    }
    [Theory] [InlineData("declared-size")] [InlineData("empty-data")] [InlineData("incomplete-frame")] [InlineData("duplicate-data")] [InlineData("duplicate-format")] [InlineData("trailing-header")] [InlineData("byte-rate")]
    public void MalformedWavStructureIsRejectedBeforeNormalization(string defect)
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "malformed.wav"); TestWorkspace.WriteWave(path);
        var bytes = File.ReadAllBytes(path);
        switch (defect)
        {
            case "declared-size": BitConverter.GetBytes(1u).CopyTo(bytes, 4); break;
            case "empty-data": bytes = bytes[..44]; BitConverter.GetBytes(36u).CopyTo(bytes, 4); BitConverter.GetBytes(0u).CopyTo(bytes, 40); break;
            case "incomplete-frame": BitConverter.GetBytes((ushort)2).CopyTo(bytes, 22); BitConverter.GetBytes(96000).CopyTo(bytes, 28); BitConverter.GetBytes((ushort)4).CopyTo(bytes, 32); bytes = bytes[..^2]; BitConverter.GetBytes((uint)(bytes.Length - 8)).CopyTo(bytes, 4); BitConverter.GetBytes((uint)(bytes.Length - 44)).CopyTo(bytes, 40); break;
            case "duplicate-data": bytes = bytes.Concat(bytes[36..]).ToArray(); BitConverter.GetBytes((uint)(bytes.Length - 8)).CopyTo(bytes, 4); break;
            case "duplicate-format": bytes = bytes.Concat(bytes[12..36]).ToArray(); BitConverter.GetBytes((uint)(bytes.Length - 8)).CopyTo(bytes, 4); break;
            case "trailing-header": bytes = bytes.Concat(new byte[] { 1, 2 }).ToArray(); BitConverter.GetBytes((uint)(bytes.Length - 8)).CopyTo(bytes, 4); break;
            case "byte-rate": BitConverter.GetBytes(7).CopyTo(bytes, 28); break;
        }
        File.WriteAllBytes(path, bytes); Assert.Throws<IOException>(() => WaveAudio.DataRegion(path, false));
    }
    [Fact] public async Task FailedAssemblyRemovesItsOwnPartialAndCanRetryFromRetainedChunks()
    {
        using var test = new TestWorkspace(); var job = await ToneJobAsync(test);
        var directory = test.Workspace.JobDirectory(job.Id);
        var chunk = test.Workspace.ChunkPath(job, 1); var original = await File.ReadAllBytesAsync(chunk);
        var unknown = Path.Combine(directory, "notes.txt"); await File.WriteAllTextAsync(unknown, "Keep this");
        TestWorkspace.WriteWave(chunk, 1);
        await Assert.ThrowsAsync<IOException>(() => new AudioPipeline(new()).AssembleAsync(job, directory, default));
        Assert.False(File.Exists(Path.Combine(directory, "assembled.wav")));
        Assert.DoesNotContain(job.PrivateArtifacts, r => r.RelativePath == "assembled.wav");
        Assert.Equal("Keep this", await File.ReadAllTextAsync(unknown));
        await File.WriteAllBytesAsync(chunk, original);
        var store = new SqliteJobStore(test.Workspace);
        await new AudioPipeline(new()).AssembleAsync(job, directory, default, () => store.SaveAsync(job));
        var reopened = Assert.Single(await store.LoadAsync());
        Assert.Equal(await Workspace.HashFileAsync(Path.Combine(directory, "assembled.wav")),
            PrivateJobFiles.Inventory(reopened)["assembled.wav"]);
        Assert.InRange((await new AudioPipeline(new()).ValidateFinalAsync(reopened, test.Workspace.FinalPath(job), default)).Duration, 3.3, 3.5);
    }

    [Fact] public void ChunkAndAudioVersionsInvalidateCacheFingerprint()
    {
        var job = new Job { Prepared = TextPreparation.Prepare("Faithful source.") }; var original = job.Fingerprint;
        job.AudioContractVersion = "changed-audio"; Assert.NotEqual(original, job.Fingerprint); job.AudioContractVersion = AudioPipeline.ContractVersion;
        job.ChunkingVersion = "changed-chunking"; Assert.NotEqual(original, job.Fingerprint);
    }
    private static async Task<Job> ToneJobAsync(TestWorkspace test)
    {
        var texts = new[] { "First marker.\n\n", "Second marker.\n\n", "Third marker." };
        var job = new Job { Title = "Ordinal tones — API & Unicode 😀", CreatedUtc = new DateTimeOffset(2026, 10, 5, 20, 10, 0, TimeSpan.Zero), Prepared = new(string.Concat(texts), []), Settings = new("kokoro", "af_heart", 1, false, "", "fixture") };
        Directory.CreateDirectory(test.Workspace.JobDirectory(job.Id)); var offset = 0;
        for (var i = 0; i < texts.Length; i++)
        {
            job.Chunks.Add(new(i, offset, texts[i].Length, texts[i], false)); offset += texts[i].Length;
            var path = test.Workspace.ChunkPath(job, i); using (var file = File.Create(path))
            {
                WaveAudio.WriteHeader(file, 24000); using var writer = new BinaryWriter(file);
                for (var frame = 0; frame < 24000; frame++) writer.Write((short)(Math.Sin(frame * 2 * Math.PI * 220 * (i + 1) / 24000) * 8000));
            }
            job.Receipts.Add(new(i, await Workspace.HashFileAsync(path), job.Fingerprint, 1));
        }
        return job;
    }
    private static double TonePower(string path, double seconds, int frequency)
    {
        var region = WaveAudio.DataRegion(path); using var file = File.OpenRead(path); file.Position = region.Offset + (long)(seconds * 24000) * 2; using var reader = new BinaryReader(file);
        double real = 0, imaginary = 0;
        for (var i = 0; i < 4800; i++) { var sample = reader.ReadInt16() / 32768.0; real += sample * Math.Cos(i * 2 * Math.PI * frequency / 24000); imaginary += sample * Math.Sin(i * 2 * Math.PI * frequency / 24000); }
        return real * real + imaginary * imaginary;
    }
}
