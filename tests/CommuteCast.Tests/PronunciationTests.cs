using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

namespace CommuteCast.Tests;

public class PronunciationTests
{
    private static PreparedText Prepare(string source, NumberReading numbers = NumberReading.AsWritten, AcronymReading acronyms = AcronymReading.AsWritten,
        DateReading dates = DateReading.NoCalendarInterpretation, string dictionary = "", bool exclude = false) =>
        TextPreparation.Prepare(source, exclude, dictionary, new(Numbers: numbers, Acronyms: acronyms, Dates: dates));

    [Theory]
    [InlineData("24", "twenty four")]
    [InlineData("-42", "minus forty two")]
    [InlineData("+0", "plus zero")]
    [InlineData("−12.50", "minus twelve point five zero")]
    [InlineData("1,234,567", "one million two hundred thirty four thousand five hundred sixty seven")]
    [InlineData("007", "zero zero seven")]
    [InlineData("0.0010", "zero point zero zero one zero")]
    [InlineData("2.10.0", "two point one zero point zero")]
    [InlineData("1/2", "one slash two")]
    [InlineData("12:30", "one two colon three zero")]
    [InlineData("12,34", "one two comma three four")]
    [InlineData("999999999999999999", "nine hundred ninety nine quadrillion nine hundred ninety nine trillion nine hundred ninety nine billion nine hundred ninety nine million nine hundred ninety nine thousand nine hundred ninety nine")]
    public void NumberValuesPreserveDigitsAndConservativeComplexForms(string text, string expected) => Assert.Equal(expected, Prepare(text, NumberReading.NumberWords).Script);

    [Theory]
    [InlineData("1.25e-3", "one point two five times ten to the power of minus three")]
    [InlineData("-2E+4", "minus two times ten to the power of plus four")]
    [InlineData("0e0", "zero times ten to the power of zero")]
    public void ScientificInterpretationIsExplicit(string text, string expected)
    {
        Assert.Equal(expected, Prepare(text, NumberReading.ScientificWords).Script);
        Assert.DoesNotContain("times ten", Prepare(text, NumberReading.NumberWords).Script);
        Assert.Equal(text, Prepare(text).Script);
    }

    [Fact] public void DigitsAndAcronymsNeverGuessMeanings()
    {
        const string source = "API APIs OpenAI XMLHttpRequest SQL SHA256 V2.10 κόσμος ΑΒ 12.50 +24";
        var result = Prepare(source, NumberReading.LiteralDigits, AcronymReading.SpellUppercaseWords);
        Assert.Equal("A P I APIs OpenAI XMLHttpRequest S Q L S H A two five six V two point one zero κόσμος ΑΒ one two point five zero plus two four", result.Script);
        Assert.DoesNotContain("application programming", result.Script);
    }

    [Theory]
    [InlineData(DateReading.MonthDayYear, "03/04/2026", "March fourth, two thousand twenty six")]
    [InlineData(DateReading.DayMonthYear, "03/04/2026", "third of April, two thousand twenty six")]
    [InlineData(DateReading.IsoYearMonthDay, "2026-10-06", "October sixth, two thousand twenty six")]
    [InlineData(DateReading.IsoYearMonthDay, "2024-02-29", "February twenty-ninth, two thousand twenty four")]
    public void ChosenCalendarOrderProducesExactPreview(DateReading mode, string source, string expected)
    {
        var prepared = Prepare(source, NumberReading.NumberWords, dates: mode);
        Assert.Equal(expected, prepared.Script); Assert.False(Assert.Single(prepared.ProfileReview!.Changes).Warning);
        Assert.Equal(source, Prepare(source).Script);
    }
    [Theory]
    [InlineData("2026-02-29")]
    [InlineData("2026-13-06")]
    [InlineData("0000-01-01")]
    public void InvalidCalendarDatesStayLiteralWithAnAffectedSourceWarning(string source)
    {
        var result = Prepare(source, NumberReading.NumberWords, dates: DateReading.IsoYearMonthDay);
        Assert.Equal(source, result.Script); var change = Assert.Single(result.ProfileReview!.Changes);
        Assert.True(change.Warning); Assert.Equal(source, change.Original); Assert.Equal(0, change.SourceStart); Assert.Equal(source.Length, change.SourceLength);
    }
    [Fact] public void DictionaryOverridesEveryStyleAndCascadesAreReviewable()
    {
        const string source = "API 24 2026-10-06 SQL";
        var result = Prepare(source, NumberReading.NumberWords, AcronymReading.SpellUppercaseWords, DateReading.IsoYearMonthDay, "API=API\n24=24\n2026-10-06=DATE\nSQL=DB\nDB=database");
        Assert.Equal("API 24 DATE database", result.Script);
        Assert.Equal(5, result.ProfileReview!.Changes.Count);
        Assert.Equal(new[] { "SQL", "SQL" }, result.ProfileReview.Changes.TakeLast(2).Select(c => c.Original));
        Assert.Equal(new[] { "SQL", "DB" }, result.ProfileReview.Changes.TakeLast(2).Select(c => c.Before));
    }
    [Fact] public void RawUtf16OffsetsSurviveMarkupCodeTablesAndEmoji()
    {
        const string source = "# 😀 API\r\n| **SQL** | 24 |\r\n[API](https://example.org/42) and `X=12;`\r\n```\nABC=7;\n```\n";
        var prepared = Prepare(source, NumberReading.NumberWords, AcronymReading.SpellUppercaseWords);
        Assert.Contains("Column 1: S Q L; Column 2: twenty four", prepared.Script);
        Assert.Contains("X equals twelve", prepared.Script); Assert.Contains("A B C equals seven", prepared.Script);
        var position = 0;
        foreach (var span in prepared.Spans) { Assert.Equal(position, span.Start); Assert.Equal(source.Substring(span.Start, span.Length), span.Original); position += span.Length; }
        Assert.Equal(source.Length, position); Assert.Equal(prepared.Script, string.Concat(prepared.Spans.Select(s => s.Narration)));
        foreach (var change in prepared.ProfileReview!.Changes)
        {
            Assert.Equal(source.Substring(change.SourceStart, change.SourceLength), change.Original);
            Assert.Contains(change.Original, new[] { "API", "SQL", "24", "42", "12", "ABC", "7" });
        }
        var api = prepared.ProfileReview.Changes.First(); Assert.Equal(source.IndexOf("API", StringComparison.Ordinal), api.SourceStart);
        var chunks = Chunker.Split(prepared.Script, 450); Chunker.ValidateManifest(chunks, prepared.Script);
    }
    [Theory]
    [InlineData("# A heading\r\n\r\nHello, world.\nLast line")]
    [InlineData("| Item |  | Value |\n| --- | --- | --- |\n| Cost | x | $12.50 |\n")]
    [InlineData("````csharp\nvar answer = 42;\n```\nvalue++;\n````\nEnd.")]
    [InlineData("Unicode café 👋🏽. [source](https://example.org). **Bold** foo__bar `a != b;`.")]
    public void DefaultProfileRetainsExistingStructuralSpokenOutput(string source)
    {
        foreach (var exclude in new[] { false, true })
            Assert.Equal(TextPreparation.Prepare(source, exclude, "API=A P I").Script, Prepare(source, dictionary: "API=A P I", exclude: exclude).Script);
    }
    [Fact] public void DictionaryRevisionTracksOrderedContentRatherThanTupleSerialization()
    {
        Assert.Equal(TextPreparation.DictionaryRevision("API=A P I\r\nSQL=S Q L"), TextPreparation.DictionaryRevision(" API = A P I \n SQL = S Q L "));
        Assert.NotEqual(TextPreparation.DictionaryRevision("API=A P I"), TextPreparation.DictionaryRevision("API=interface"));
        Assert.NotEqual(TextPreparation.DictionaryRevision("API=A P I\nSQL=S Q L"), TextPreparation.DictionaryRevision("SQL=S Q L\nAPI=A P I"));
    }
    [Fact] public void LegacyFingerprintPreservesExistingReceiptIdentityExactly()
    {
        var job = new Job { Prepared = TextPreparation.Prepare("Legacy API 24."), Settings = new("kokoro", "af_heart", 1, false, "", "old-model") };
        var oldSettings = new { job.Settings.Engine, job.Settings.Voice, job.Settings.Speed, job.Settings.ExcludeCode, job.Settings.Pronunciation, job.Settings.ProviderFingerprint };
        var expected = Job.Hash(JsonSerializer.Serialize(new { Settings = oldSettings, job.Prepared.Version, job.Prepared.Script, job.AudioContractVersion, job.ChunkingVersion }));
        Assert.Equal(expected, job.Fingerprint);
        Assert.Equal(expected, JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!.Fingerprint);
    }
    [Fact] public async Task FrozenProfilesSurviveSqliteWhileFutureSettingsChange()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var app = new AppSettings { PronunciationProfile = new(Numbers: NumberReading.NumberWords) };
        var job = new Job { Source = "API 24", Prepared = TextPreparation.Prepare("API 24", profile: app.PronunciationProfile), Settings = new("piper", "en_US-lessac-medium", 1, false, "", "frozen-model", app.PronunciationProfile), Destination = test.Destination };
        var fingerprint = job.Fingerprint; await store.SaveAsync(job);
        app.PronunciationProfile = app.PronunciationProfile with { Numbers = NumberReading.LiteralDigits, Acronyms = AcronymReading.SpellUppercaseWords };
        var retained = Assert.Single(await store.LoadAsync()); Assert.Equal(fingerprint, retained.Fingerprint); Assert.Equal("API twenty four", retained.Prepared.Script);
        Assert.Equal(NumberReading.NumberWords, retained.Settings.Profile!.Numbers); Assert.Equal(retained.Settings.Profile, retained.Prepared.ProfileReview!.Profile);
        var revised = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(retained))!;
        revised.Settings = revised.Settings with { Profile = app.PronunciationProfile }; Assert.NotEqual(fingerprint, revised.Fingerprint);
        revised.Settings = retained.Settings with { Profile = retained.Settings.Profile with { Dates = DateReading.MonthDayYear } }; Assert.NotEqual(fingerprint, revised.Fingerprint);
        revised.Settings = retained.Settings with { Profile = retained.Settings.Profile with { Version = "future" } }; Assert.NotEqual(fingerprint, revised.Fingerprint);
    }
    [Fact] public void BoundsAndCancellationRejectWithoutTruncatingSource()
    {
        Assert.Throws<ArgumentException>(() => Prepare("API", dictionary: new string('x', 16_385)));
        Assert.Throws<ArgumentException>(() => Prepare("API", dictionary: "API=" + new string('x', 1025)));
        Assert.Throws<ArgumentException>(() => Prepare("API", dictionary: string.Join('\n', Enumerable.Range(0, 257).Select(i => $"TERM{i}=word"))));
        var source = string.Join(' ', Enumerable.Repeat("A", 120_000));
        Assert.Throws<ArgumentException>(() => Prepare(source, dictionary: "A=" + new string('x', 1024)));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => TextPreparation.Prepare("API", profile: new(), ct: cancelled.Token));
        Assert.Equal(239_999, source.Length);
    }
    [Fact] public async Task OfflineBackupRestoreRetainsProfileScriptReviewAndReceiptFingerprint()
    {
        using var source = new TestWorkspace(); using var target = new TestWorkspace();
        var profile = new PronunciationProfile(Numbers: NumberReading.LiteralDigits, Acronyms: AcronymReading.SpellUppercaseWords, Dates: DateReading.DayMonthYear);
        const string raw = "# API 24 on 03/04/2026"; const string dictionary = "API=interface";
        var job = new Job { Source = raw, Prepared = TextPreparation.Prepare(raw, pronunciation: dictionary, profile: profile), Settings = new("kokoro", "af_heart", 1.1, false, dictionary, "frozen-provider", profile), Destination = source.Destination };
        job.Chunks = Chunker.Split(job.Prepared.Script, 450); job.Receipts.Add(new(0, "receipt-hash", job.Fingerprint, 1));
        await new SqliteJobStore(source.Workspace).SaveAsync(job);
        using var sourceLease = WorkspaceLease.Acquire(source.Workspace); var backup = await WorkspaceBackup.CreateAsync(sourceLease);
        using var targetLease = WorkspaceLease.Acquire(target.Workspace); await WorkspaceBackup.RestoreAsync(targetLease, backup);
        var restored = Assert.Single(await new SqliteJobStore(target.Workspace).LoadAsync());
        Assert.Equal(job.Fingerprint, restored.Fingerprint); Assert.Equal(job.Settings, restored.Settings); Assert.Equal(job.Prepared.Script, restored.Prepared.Script);
        Assert.Equal(job.Prepared.ProfileReview!.Changes, restored.Prepared.ProfileReview!.Changes); Assert.Equal(job.Prepared.ProfileReview.DictionaryRevision, restored.Prepared.ProfileReview.DictionaryRevision);
        Assert.Equal(job.Receipts, restored.Receipts);
    }
    [Fact] public void CascadingDictionaryReviewGrowthIsBounded()
    {
        var source = string.Join(' ', Enumerable.Repeat("A", 20_000));
        var dictionary = string.Join('\n', Enumerable.Range(0, 25).Select(i => $"{(char)('A' + i)}={(char)('B' + i)}"));
        var error = Assert.Throws<ArgumentException>(() => Prepare(source, dictionary: dictionary));
        Assert.Contains("review changes", error.Message);
    }
    [Fact] public async Task QueueRefusesUnsupportedAndMismatchedProfilesBeforeAcknowledgement()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace);
        await using var queue = new QueueCoordinator(test.Workspace, store, new NeverProvider(), new(new()), new(test.Workspace, store)) { Paused = true };
        await queue.InitializeAsync(); var job = new Job { Source = "API 24", Prepared = Prepare("API 24"), Settings = new("kokoro", "af_heart", 1, false, "", "model", new()), Destination = test.Destination };
        foreach (var invalid in new[] { new PronunciationProfile(Version: "future"), new(Language: "fr"), new(Numbers: (NumberReading)999), new(Acronyms: AcronymReading.SpellUppercaseWords) })
        {
            job.Settings = job.Settings with { Profile = invalid }; await Assert.ThrowsAsync<ArgumentException>(() => queue.AddAsync(job));
            Assert.Empty(await store.LoadAsync()); Assert.Empty(queue.Snapshot());
        }
        job.Settings = job.Settings with { Profile = new() }; await queue.AddAsync(job); Assert.Single(await store.LoadAsync());
    }
    private sealed class NeverProvider : ISpeechProvider
    {
        public Task<ProviderInfo> ReadyAsync(string engine, CancellationToken ct) => throw new InvalidOperationException("No speech calls expected.");
        public Task SynthesizeAsync(NarrationSettings settings, string text, string output, CancellationToken ct) => throw new InvalidOperationException("No speech calls expected.");
    }
}
