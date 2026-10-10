using System.Globalization;
using CommuteCast.Core;
using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class NarrationPromptTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 10);

    [Fact]
    public void CompleteBriefProducesPortableSpokenWritingAndEvidenceContract()
    {
        var brief = new NarrationBrief { Topic = "Railways and cities 😀", Goal = "Understand how transport shaped everyday life", Audience = "An interested historian", Minutes = "25", Tone = "Calm and curious", Include = "London; a commuter's day; economic tradeoffs", Avoid = "Do not assume all effects were positive", SourceMaterial = "A café beside the station\nhttps://example.org/history" };
        var prompt = NarrationPrompt.Build(brief, AsOf);
        foreach (var value in new[] { brief.Topic, brief.Goal, brief.Audience, brief.Tone, brief.Include, brief.Avoid, brief.SourceMaterial }) Assert.Contains(value, prompt);
        Assert.Contains("3500 to 4000 spoken words", prompt);
        Assert.Contains("October 10, 2026", prompt);
        Assert.Contains("do not claim verification", prompt);
        Assert.Contains("Never invent quotations", prompt);
        Assert.Contains("listening alone", prompt);
        Assert.Contains("REVISE BEFORE DELIVERING", prompt);
        Assert.EndsWith(NarrationPrompt.OutputInstructions, prompt);
        Assert.Contains("Your entire response will be converted directly to text-to-speech", prompt);
        Assert.Contains("Do not report those checks", prompt);
        Assert.DoesNotContain("NARRATION and SOURCE NOTES", prompt);
        Assert.DoesNotContain("say so in the notes", prompt);
        Assert.DoesNotContain("$commute-narrative", prompt);
    }

    [Theory]
    [InlineData(PromptEvidenceMode.Research)]
    [InlineData(PromptEvidenceMode.SuppliedOnly)]
    public void BothEvidenceModesKeepResearchAndLengthWorkOutOfTheOutput(PromptEvidenceMode evidence)
    {
        var prompt = NarrationPrompt.Build(new() { Topic = "A topic", Evidence = evidence, SourceMaterial = "Supplied facts" }, AsOf);
        Assert.EndsWith(NarrationPrompt.OutputInstructions, prompt);
        Assert.Contains("Privately check the word count", prompt);
        Assert.Contains("do not include formal document references or append a source list", prompt);
        Assert.DoesNotContain("in the source notes", prompt);
        Assert.DoesNotContain("separate source notes", prompt);
        Assert.DoesNotContain("Inside SOURCE NOTES", prompt);
        Assert.DoesNotContain("offer a continuation", prompt);
    }

    [Theory]
    [InlineData(NarrativeStyle.Explainer, "explain how and why it works")]
    [InlineData(NarrativeStyle.Story, "documented scenes only")]
    [InlineData(NarrativeStyle.PracticalGuide, "when the advice does not apply")]
    [InlineData(NarrativeStyle.BalancedComparison, "Do not manufacture equal weight")]
    public void StyleChangesNarrativeDirection(NarrativeStyle style, string direction) =>
        Assert.Contains(direction, NarrationPrompt.Build(new() { Topic = "A topic", Style = style }, AsOf));

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("2")] [InlineData("61")] [InlineData("30.5")] [InlineData("thirty")] [InlineData("9999999999")]
    public void InvalidDurationRefusesToGenerateInsteadOfUsingAnOldOrClampedValue(string minutes) =>
        Assert.Throws<ArgumentException>(() => NarrationPrompt.Build(new() { Topic = "A topic", Minutes = minutes }, AsOf));

    [Theory]
    [InlineData("3", "420 to 480")]
    [InlineData(" 60 ", "8400 to 9600")]
    public void DurationBoundsProduceCorrectWordTargets(string minutes, string target) =>
        Assert.Contains(target, NarrationPrompt.Build(new() { Topic = "A topic", Minutes = minutes }, AsOf));

    [Fact]
    public void SourceOnlyModeRequiresMaterialAndForbidsOutsideResearch()
    {
        var brief = new NarrationBrief { Topic = "My notes", Evidence = PromptEvidenceMode.SuppliedOnly };
        Assert.Throws<ArgumentException>(() => NarrationPrompt.Build(brief, AsOf));
        var prompt = NarrationPrompt.Build(brief with { SourceMaterial = "A supplied note" }, AsOf);
        Assert.Contains("Do not browse or add outside factual claims", prompt);
        Assert.Contains("If links cannot be opened", prompt);
        Assert.DoesNotContain("check their status as of", prompt);
    }

    [Fact]
    public void MissingTopicOversizedInputAndUnknownOptionsAreRefused()
    {
        Assert.Throws<ArgumentException>(() => NarrationPrompt.Build(new(), AsOf));
        Assert.Throws<ArgumentException>(() => NarrationPrompt.Build(new() { Topic = "A topic", SourceMaterial = new('x', 50001) }, AsOf));
        Assert.Throws<ArgumentException>(() => NarrationPrompt.Build(new() { Topic = "A topic", Style = (NarrativeStyle)99 }, AsOf));
        Assert.Throws<ArgumentException>(() => NarrationPrompt.Build(new() { Topic = "A topic", Evidence = (PromptEvidenceMode)99 }, AsOf));
    }

    [Fact]
    public void WordTargetsAndResearchDateRemainUnambiguousAcrossCultures()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-EG");
            var prompt = NarrationPrompt.Build(new() { Topic = "A topic" }, AsOf);
            Assert.Contains("4200 to 4800 spoken words", prompt); Assert.Contains("October 10, 2026", prompt);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task PromptAndEditedTextRoundTripAlongsideNarrationAndLegacyDraftsStillLoad()
    {
        using var test = new TestWorkspace(); var store = new DraftStore(test.Workspace);
        var legacy = new Draft("Existing title", "Unchanged narration 😀");
        await store.SaveAsync(legacy); Assert.Equal(legacy, await store.LoadAsync());
        var brief = new NarrationBrief { Topic = "Café history", SourceMaterial = "My reference notes" };
        var saved = legacy with { PromptDraft = new(brief, NarrationPrompt.Build(brief, AsOf) + "\nMy manual edit", brief, NarrationPrompt.TemplateVersion) };
        await store.SaveAsync(saved); Assert.Equal(saved, await new DraftStore(test.Workspace).LoadAsync());
        var incomplete = saved with { PromptDraft = saved.PromptDraft with { Brief = brief with { Minutes = "" } } };
        await store.SaveAsync(incomplete); Assert.Equal(incomplete, await store.LoadAsync());
    }

    [Theory]
    [InlineData("[\"Title\",\"Source\",\"not JSON\"]")]
    [InlineData("[\"Title\",\"Source\",\"null\"]")]
    [InlineData("[\"Title\",\"Source\",\"{}\"]")]
    [InlineData("[\"Title\",\"Source\",null]")]
    public async Task MalformedPromptDraftIsPreservedBeforeReplacement(string original)
    {
        using var test = new TestWorkspace(); var path = Path.Combine(test.Workspace.Root, "draft.json");
        await File.WriteAllTextAsync(path, original); var store = new DraftStore(test.Workspace);
        await Assert.ThrowsAsync<IOException>(() => store.LoadAsync()); Assert.Equal(original, await File.ReadAllTextAsync(path));
        var preserved = await store.SaveAsync(new("Recovered", "Kept narration"));
        Assert.Equal(original, await File.ReadAllTextAsync(preserved!));
        Assert.Equal("Kept narration", (await store.LoadAsync()).Source);
    }
}
