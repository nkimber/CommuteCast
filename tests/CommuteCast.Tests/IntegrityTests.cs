using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

namespace CommuteCast.Tests;

public class IntegrityTests
{
    [Theory]
    [InlineData("# A heading\r\n\r\nHello, world.\nLast line")]
    [InlineData("| Item | Value |\n| --- | --- |\n| Cost | $12.50 |\n")]
    [InlineData("```csharp\nvar answer = 42;\n```\nNo missing paragraphs.")]
    [InlineData("Unicode café and 👋🏽. A URL [source](https://example.org).")]
    [InlineData("One line without a trailing newline")]
    public void PreparationAccountsForEverySourceSpan(string source)
    {
        var prepared = TextPreparation.Prepare(source);
        Assert.Equal(source, string.Concat(prepared.Spans.Select(s => s.Original)));
        Assert.Equal(prepared.Script, string.Concat(prepared.Spans.Select(s => s.Narration)));
        var position = 0;
        foreach (var span in prepared.Spans) { Assert.Equal(position, span.Start); position += span.Length; }
        Assert.Equal(source.Length, position);
    }
    [Fact] public void CodeRequiresExplicitExclusion()
    {
        const string text = "Keep this.\n```\nsecret_code = 42;\n```\nAnd this.";
        Assert.Contains("secret underscore code", TextPreparation.Prepare(text).Script);
        var excluded = TextPreparation.Prepare(text, true);
        Assert.DoesNotContain("secret_code", excluded.Script);
        Assert.Contains(excluded.Spans, s => s.Kind == "explicit exclusion" && s.Original.Contains("secret_code"));
        Assert.Contains("And this.", excluded.Script);
    }
    [Fact] public void AmbiguousMarkupAndSubstantiveIdentifiersAreNotErased()
    {
        var prepared = TextPreparation.Prepare("Keep foo__bar and unmatched **marker. **Bold** text.\n````csharp\na != b;\n```\nvalue++;\n````\nDone.");
        Assert.Contains("foo__bar", prepared.Script);
        Assert.Contains("**marker", prepared.Script);
        Assert.Contains("value plus plus", prepared.Script);
        Assert.Contains("exclamation equals", prepared.Script);
        Assert.Contains("Done.", prepared.Script);
    }
    [Fact] public void EmptyTableCellsAreExplicit()
    {
        var prepared = TextPreparation.Prepare("| name |  | value |\n");
        Assert.Contains("Column 2: empty cell", prepared.Script);
        Assert.Contains("Column 3: value", prepared.Script);
    }
    [Fact] public void DictionaryUsesLiteralWholeTermsAndPreservesNumbers()
    {
        var text = TextPreparation.Prepare("API APIs C++ cost $12.50 with 99.9% availability.", pronunciation: "API=A P I\nC++=C plus plus");
        Assert.Contains("A P I APIs C plus plus", text.Script);
        Assert.Contains("$12.50", text.Script);
        Assert.Contains("99.9%", text.Script);
    }
    [Theory]
    [InlineData("broken")]
    [InlineData("API=")]
    [InlineData("API=one\nAPI=two")]
    public void InvalidDictionaryIsRejected(string rules) => Assert.Throws<ArgumentException>(() => TextPreparation.ParseDictionary(rules));
    [Fact] public void OversizedInputIsRejectedWithoutTruncation() => Assert.Throws<ArgumentException>(() => TextPreparation.Prepare(new string('a', TextPreparation.MaximumCharacters + 1)));
    [Theory]
    [InlineData("# Hello commute\nMore text", "Hello commute")]
    [InlineData("A useful first sentence. And another.", "A useful first sentence.")]
    public void TitleIsDeterministic(string text, string expected) => Assert.Equal(expected, TextPreparation.SuggestTitle(text));
    [Theory]
    [InlineData(2_100)]
    [InlineData(249_900)]
    public void SuggestedTitlePrefersHeadingLateInSupportedInput(int introductionLength)
    {
        var source = "Opening sentence.\n" + new string('x', introductionLength) + "\n# The full document heading\nFinal paragraph.";
        Assert.True(source.Length <= TextPreparation.MaximumCharacters);
        Assert.Equal("The full document heading", TextPreparation.SuggestTitle(source));
    }
    [Theory]
    [InlineData(false, 99)]
    [InlineData(true, 99)]
    [InlineData(false, 98)]
    [InlineData(true, 98)]
    public void SuggestedTitleKeepsSupplementaryUnicodeWhole(bool heading, int prefixLength)
    {
        var prefix = new string('x', prefixLength);
        var source = (heading ? "# " : "") + prefix + "😀 trailing text";
        var title = TextPreparation.SuggestTitle(source);
        Assert.Equal(prefixLength == 98 ? prefix + "😀" : prefix, title);
        Assert.InRange(title.Length, 1, 100);
        var strictUtf8 = new System.Text.UTF8Encoding(false, true);
        Assert.Equal(title, strictUtf8.GetString(strictUtf8.GetBytes(title)));
    }
    [Theory]
    [InlineData(16)] [InlineData(57)] [InlineData(450)] [InlineData(900)]
    public void ChunkerNeverLosesDuplicatesOrReorders(int maximum)
    {
        var random = new Random(407);
        var script = string.Concat(Enumerable.Range(0, 600).Select(i => $"Paragraph {i}: {new string('x', random.Next(1, 50))}.\n\n")) + new string('z', 1200) + "😀final";
        var chunks = Chunker.Split(script, maximum);
        Assert.Equal(script, string.Concat(chunks.Select(c => c.Text)));
        var next = 0;
        foreach (var chunk in chunks) { Assert.Equal(next, chunk.Start); Assert.InRange(chunk.Length, 1, maximum); next += chunk.Length; }
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(c => c.Index));
        Assert.Contains(chunks, c => c.HardSplit);
    }
    [Fact] public void ChunkerDoesNotSplitSurrogatePairs()
    {
        var chunks = Chunker.Split(new string('x', 15) + "😀" + new string('y', 40), 16);
        Assert.All(chunks, c => { Assert.False(char.IsHighSurrogate(c.Text[^1])); Assert.False(char.IsLowSurrogate(c.Text[0])); });
    }
    [Fact] public void SettingsFingerprintIsImmutableAcrossSerialization()
    {
        var job = new Job { Prepared = TextPreparation.Prepare("A preserved thought."), Settings = new("kokoro", "af_heart", 1, false, "", "model-hash") };
        var restored = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
        Assert.Equal(job.Fingerprint, restored.Fingerprint);
        restored.Settings = restored.Settings with { Speed = 1.1 };
        Assert.NotEqual(job.Fingerprint, restored.Fingerprint);
    }
    [Theory]
    [InlineData("../../escape")]
    [InlineData("C:\\Windows")]
    [InlineData("")]
    public void WorkspaceRejectsUnsafeJobIdentity(string id)
    {
        using var test = new TestWorkspace();
        Assert.Throws<ArgumentException>(() => test.Workspace.JobDirectory(id));
    }
    [Fact] public void ExportNameIsSafeAndCollisionResistant()
    {
        var a = new Job { Title = "CON ../?:*\\a | unsafe\u0001" };
        var b = new Job { Title = a.Title, CreatedUtc = a.CreatedUtc };
        var name = ExportPublisher.Filename(a);
        Assert.Equal(Path.GetFileName(name), name);
        Assert.DoesNotContain(name, c => "<>:\"/\\|?*".Contains(c) || c < 32);
        Assert.NotEqual(name, ExportPublisher.Filename(b));
    }
    [Theory]
    [InlineData("en-US")]
    [InlineData("th-TH")]
    [InlineData("ar-SA")]
    public void ExportIdentityUsesUtcGregorianTimestampAcrossCultures(string culture)
    {
        var prior = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new(culture);
            var job = new Job { Id = new string('a', 32), Title = "Same title", CreatedUtc = new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4)) };
            var expected = "20261101-053000-Same title-" + job.Id + ".mp3";
            Assert.Equal(expected, ExportPublisher.Filename(job));
            var reopened = JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(job))!;
            Assert.Equal(expected, ExportPublisher.Filename(reopened));
            reopened.Stage = JobStage.Failed;
            Assert.Equal(job.CreatedUtc, reopened.CreatedUtc);
            Assert.Equal(expected, ExportPublisher.Filename(reopened));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = prior; }
    }
    [Fact]
    public void RepeatedDstLocalTimeKeepsDistinctUtcSubmissionIdentity()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var first = new Job { Title = "Repeated hour", CreatedUtc = new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero) };
        var second = new Job { Title = first.Title, CreatedUtc = first.CreatedUtc.AddHours(1) };
        var firstLocal = TimeZoneInfo.ConvertTime(first.CreatedUtc, zone);
        var secondLocal = TimeZoneInfo.ConvertTime(second.CreatedUtc, zone);
        Assert.Equal(firstLocal.DateTime, secondLocal.DateTime);
        Assert.NotEqual(firstLocal.Offset, secondLocal.Offset);
        Assert.StartsWith("20261101-053000-", ExportPublisher.Filename(first));
        Assert.StartsWith("20261101-063000-", ExportPublisher.Filename(second));
        Assert.NotEqual(ExportPublisher.Filename(first), ExportPublisher.Filename(second));
    }
    [Fact] public void WaveValidatorRejectsTruncatedAndSilentChunks()
    {
        using var test = new TestWorkspace();
        var path = Path.Combine(test.Workspace.Root, "test.wav");
        TestWorkspace.WriteWave(path);
        Assert.Equal(1, WaveAudio.Inspect(path).Duration);
        using (var file = File.OpenWrite(path)) file.SetLength(50);
        Assert.Throws<IOException>(() => WaveAudio.Inspect(path));
    }
}

public sealed class TestWorkspace : IDisposable
{
    public string Parent { get; } = Path.Combine(Path.GetTempPath(), "CommuteCast-tests", Guid.NewGuid().ToString("N"));
    public Workspace Workspace { get; }
    public string Destination { get; }
    public TestWorkspace()
    {
        Workspace = new(Path.Combine(Parent, "private"));
        Destination = Path.Combine(Parent, "export"); Directory.CreateDirectory(Destination);
    }
    public static void WriteWave(string path, double duration = 1)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path); var samples = (long)(24000 * duration);
        WaveAudio.WriteHeader(file, samples);
        using var writer = new BinaryWriter(file);
        for (long i = 0; i < samples; i++) writer.Write((short)(Math.Sin(i * 2 * Math.PI * 220 / 24000) * 8000));
    }
    public void Dispose()
    {
        if (Directory.Exists(Parent) && Workspace.IsWithin(Path.Combine(Path.GetTempPath(), "CommuteCast-tests"), Parent)) Directory.Delete(Parent, true);
    }
}
