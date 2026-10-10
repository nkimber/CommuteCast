using CommuteCast.Core;

namespace CommuteCast.Tests;

public class ReviewPlaybackTests
{
    [Fact]
    public void ReviewRunsKeepExactSourceAndScriptWhileMarkingExclusionsAndChanges()
    {
        var source = "API reads 24.\n```\nsecret code\n```\nCafé 😀";
        var prepared = TextPreparation.Prepare(source, true, "API=A P I", new(Numbers: NumberReading.NumberWords));
        var original = ReviewHighlights.Original(prepared, source); var spoken = ReviewHighlights.Spoken(prepared);
        Assert.Equal(source, string.Concat(original.Select(r => r.Text)));
        Assert.Equal(prepared.Script, string.Concat(spoken.Select(r => r.Text)));
        Assert.Contains(original, r => r.Kind == ReviewHighlightKind.Excluded && r.Text.Contains("secret code"));
        Assert.Contains(original, r => r.Kind == ReviewHighlightKind.Changed && r.Text.Contains("API"));
        Assert.DoesNotContain(spoken, r => r.Text.Contains("secret code"));
    }
    [Fact]
    public void OverlappingPronunciationChangesNeverDuplicateTextAndLegacyScriptStillDisplays()
    {
        const string source = "API source";
        var prepared = new PreparedText("A P I source", [new(0, source.Length, "spoken", source, "A P I source")], ProfileReview: new(new(), "fixture",
            [new(0, 3, "API", "API", "A P I", "first"), new(0, 3, "API", "A", "Aye", "cascade")]));
        Assert.Equal(source, string.Concat(ReviewHighlights.Original(prepared, source).Select(r => r.Text)));
        Assert.Equal("old script", Assert.Single(ReviewHighlights.Spoken(new("old script", []))).Text);
    }
    [Fact]
    public void ApprovedAuditionSpeaksPreparedTextWithoutDictionaryCascadeOrAnotherProfilePass()
    {
        var settings = new NarrationSettings("kokoro", "af_heart", 1.1, true, "A=Aye", "old", new(Numbers: NumberReading.LiteralDigits));
        var request = AuditionRequest.ApprovedExcerpt("A P I twenty four 😀", settings);
        Assert.Equal("A P I twenty four 😀", request.Prepare().Script);
        Assert.Equal(1.1, request.Settings.Speed); Assert.Equal("", request.Settings.ProviderFingerprint);
        Assert.Throws<ArgumentException>(() => AuditionRequest.ApprovedExcerpt(new string('x', 901), settings));
        Assert.Throws<ArgumentException>(() => AuditionRequest.ApprovedExcerpt("\ud83d", settings));
        Assert.Throws<OperationCanceledException>(() => request.Prepare(new CancellationToken(true)));
    }
    [Theory]
    [InlineData(-20, 100, 0)] [InlineData(130, 100, 100)] [InlineData(50, 100, 50)]
    [InlineData(double.NaN, 100, 0)] [InlineData(5, 0, 0)]
    public void SeekingIsBounded(double position, double duration, double expected) => Assert.Equal(expected, PlaybackTime.Clamp(position, duration));
}
