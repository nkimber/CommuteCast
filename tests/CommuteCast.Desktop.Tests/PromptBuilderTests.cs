using System.Windows.Controls;
using System.Windows.Threading;
using CommuteCast.Core;
using CommuteCast.Desktop;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public class PromptBuilderTests
{
    [Fact]
    public Task GenerateEditRebuildAndReturnKeepNarrationIndependent() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var model = Model(workspace);
        try
        {
            model.Source = "Existing narration"; model.DraftTitle = "Existing title";
            model.NavigateCommand.Execute("prompt");
            Assert.True(model.IsPromptBuilder); Assert.False(model.CanCopyPrompt);
            Assert.Same(model.BuildPromptCommand, model.PrimaryActionCommand);
            model.PromptTopic = "How railways changed cities"; model.PromptGoal = "Understand daily life";
            model.PromptStyle = NarrativeStyle.Story; model.PromptMinutes = "20";
            await DesktopHost.Execute(model.BuildPromptCommand);
            Assert.True(model.CanCopyPrompt); Assert.Contains("2800 to 3200", model.PromptText);
            model.PromptText += "\nKeep my additional request.";
            Assert.Contains("additional request", model.PromptForCopy());
            var edited = model.PromptText;
            model.PromptTopic = "Railways and suburbs";
            Assert.True(model.PromptIsStale); Assert.False(model.CanCopyPrompt); Assert.Equal(edited, model.PromptText);
            Assert.Throws<ArgumentException>(() => model.PromptForCopy());
            model.PromptMinutes = "bad";
            await DesktopHost.Execute(model.BuildPromptCommand);
            Assert.Equal(edited, model.PromptText); Assert.True(model.PromptIsStale);
            model.PromptMinutes = "20"; await DesktopHost.Execute(model.BuildPromptCommand);
            Assert.Contains("Railways and suburbs", model.PromptForCopy()); Assert.DoesNotContain("additional request", model.PromptText);
            Assert.False(model.HasAttention);
            model.NavigateCommand.Execute("compose");
            Assert.True(model.IsCompose); Assert.Same(model.QueueCommand, model.PrimaryActionCommand);
            Assert.Equal("Existing narration", model.Source); Assert.Equal("Existing title", model.DraftTitle); Assert.Empty(model.Jobs);
        }
        finally { await model.DisposeAsync(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EditedPromptAndBriefSurviveClosingAndReopening(bool changeBrief) => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var model = Model(workspace);
        model.Source = "Existing narration"; model.DraftTitle = "Keep this title";
        model.PromptTopic = "Café history 😀";
        await DesktopHost.Execute(model.BuildPromptCommand); model.PromptText += "\nMy custom wording.";
        if (changeBrief) model.PromptAudience = "A local historian";
        var expected = model.CaptureDraft(); await model.DisposeAsync();
        var saved = await new DraftStore(workspace).LoadAsync(); Assert.Equal(expected, saved);
        var reopened = Model(workspace);
        try
        {
            reopened.RestorePromptDraft(saved.PromptDraft);
            Assert.Equal("Café history 😀", reopened.PromptTopic); Assert.Equal(saved.PromptDraft!.Brief.Audience, reopened.PromptAudience);
            Assert.Contains("My custom wording", reopened.PromptText); Assert.Equal(changeBrief, reopened.PromptIsStale); Assert.Equal(!changeBrief, reopened.CanCopyPrompt);
            Assert.Equal(NarrationPrompt.TemplateVersion, reopened.CaptureDraft().PromptDraft!.TemplateVersion);
        }
        finally { reopened.Source = saved.Source; reopened.DraftTitle = saved.Title; await reopened.DisposeAsync(); }
    });

    [Fact]
    public Task PreviouslySavedPromptKeepsEditsButRequiresCurrentNarrationOnlyInstructions() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var brief = new NarrationBrief { Topic = "Café history" };
        const string oldPrompt = "Return NARRATION and SOURCE NOTES.\nMy manual edit.";
        var oldState = System.Text.Json.JsonSerializer.Serialize(new { Brief = brief, Prompt = oldPrompt, GeneratedFor = brief });
        await System.IO.File.WriteAllTextAsync(System.IO.Path.Combine(workspace.Root, "draft.json"),
            System.Text.Json.JsonSerializer.Serialize(new[] { "Existing title", "Existing narration", oldState }));
        var saved = await new DraftStore(workspace).LoadAsync(); Assert.Equal(0, saved.PromptDraft!.TemplateVersion);
        var model = Model(workspace);
        try
        {
            model.Source = saved.Source; model.DraftTitle = saved.Title; model.RestorePromptDraft(saved.PromptDraft);
            Assert.Equal(oldPrompt, model.PromptText); Assert.True(model.PromptIsStale); Assert.False(model.CanCopyPrompt);
            Assert.Contains("narration-only instructions", model.PromptStatus);
            Assert.Throws<ArgumentException>(() => model.PromptForCopy());
            await DesktopHost.Execute(model.BuildPromptCommand);
            Assert.True(model.CanCopyPrompt); Assert.EndsWith(NarrationPrompt.OutputInstructions, model.PromptForCopy());
            Assert.DoesNotContain("SOURCE NOTES", model.PromptText); Assert.DoesNotContain("My manual edit", model.PromptText);
            Assert.Equal("Existing narration", model.Source); Assert.Equal("Existing title", model.DraftTitle); Assert.Empty(model.Jobs);
        }
        finally { await model.DisposeAsync(); }
        Assert.Equal(NarrationPrompt.TemplateVersion, (await new DraftStore(workspace).LoadAsync()).PromptDraft!.TemplateVersion);
    });

    [Fact]
    public void CodexGuideWritingPromptsAlsoRequestOnlyNarration()
    {
        foreach (var prompt in new[] { CodexGuideContent.GeneratePrompt, CodexGuideContent.BillsExample })
        {
            Assert.EndsWith(NarrationPrompt.OutputInstructions, prompt);
            Assert.Contains("privately check the word count", prompt);
            Assert.DoesNotContain("after the narration", prompt);
            Assert.DoesNotContain("separately afterward", prompt);
        }
    }

    [Fact]
    public Task NativeBindingsGenerateAndReflectStaleCopyState() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var model = Model(workspace);
        try
        {
            var window = new MainWindow(model); model.NavigateCommand.Execute("prompt");
            await Dispatcher.Yield(DispatcherPriority.Background); DesktopHost.Layout(window);
            var input = (TextBox)window.FindName("PromptTopicInput"); input.SetCurrentValue(TextBox.TextProperty, "The history of maps");
            input.GetBindingExpression(TextBox.TextProperty)!.UpdateSource();
            Assert.Equal("The history of maps", model.PromptTopic);
            var build = (Button)window.FindName("BuildPromptButton"); Assert.Same(model.BuildPromptCommand, build.Command);
            await DesktopHost.Execute(build.Command); await Dispatcher.Yield(DispatcherPriority.Background); DesktopHost.Layout(window);
            var copy = (Button)window.FindName("CopyPromptButton"); Assert.True(copy.IsEnabled); Assert.Same(model.CopyPromptCommand, copy.Command);
            Assert.Equal(model.PromptText, ((TextBox)window.FindName("GeneratedPromptInput")).Text);
            model.PromptGoal = "Understand mapmaking"; await Dispatcher.Yield(DispatcherPriority.Background); DesktopHost.Layout(window); Assert.False(copy.IsEnabled);
            var back = (Button)window.FindName("PromptToNarrationButton"); back.Command.Execute(back.CommandParameter);
            Assert.True(model.IsCompose); Assert.Empty(model.Jobs);
        }
        finally { await model.DisposeAsync(); }
    });

    private static MainViewModel Model(Workspace workspace) => new(new() { QueuePaused = true }, workspace,
        playbackOutput: new PlaybackTests.Output(), powerEvents: new LifecycleTests.Events(), notifications: new LifecycleTests.Notifications());
}
