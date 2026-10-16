using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using CommuteCast.Core;
using CommuteCast.Desktop;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public class VoiceComparisonTests
{
    [Fact] public Task CandidatesFreezeOnePreparedPassageAndKeepCompleteLocalRecipes() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var model = new MainViewModel(new() { QueuePaused = true, Pronunciation = "API=application interface" }, workspace);
        try
        {
            model.ComparisonPassage = "API explained."; model.AddComparison();
            Assert.Equal("application interface explained.", model.ComparisonPassage);
            var first = model.ComparisonCandidates.Single();
            model.BlendVoice = "af_bella"; model.BlendWeight = .35; model.Speed = .95; model.ParagraphPauseMs = 700;
            model.AddComparison();
            Assert.Equal("", first.Recipe.LocalVoice!.BlendVoice);
            Assert.Equal(.35, model.SelectedComparison!.Recipe.LocalVoice!.BlendWeight);
            model.ComparisonPassage = "Replacement is refused."; Assert.Equal("application interface explained.", model.ComparisonPassage);
            model.ComparisonPresetName = "My blend"; await DesktopHost.Execute(model.SaveComparisonCommand);
            var saved = (await workspace.LoadSettingsAsync()).NarrationPresets!.Single(p => p.Name == "My blend");
            Assert.Equal("af_bella", saved.Options.LocalVoice!.BlendVoice); Assert.Equal(700, saved.Options.LocalVoice.ParagraphPauseMs); Assert.Equal(.95, saved.Options.Speed);
            Assert.False(model.UsingNarrationDefaults);
            model.Pronunciation = "API=another pronunciation";
            Assert.Throws<ArgumentException>(model.AddComparison);
            await model.ClearComparisonAsync(); Assert.False(model.ComparisonLocked); Assert.Empty(model.ComparisonCandidates);
        }
        finally { await model.DisposeAsync(); }
    });

    [Fact] public Task NaturalDeliveryResetKeepsVoiceScriptAndSavedDefaults() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var model = new MainViewModel(new() { QueuePaused = true }, workspace);
        try
        {
            model.Source = "Keep this script."; model.Voice = "af_bella"; model.BlendVoice = "af_heart"; model.Speed = 1.3; model.NaturalPhrasing = false;
            await DesktopHost.Execute(model.ResetNaturalDeliveryCommand);
            Assert.Equal("af_bella", model.Voice); Assert.Equal("Keep this script.", model.Source);
            Assert.True(model.NaturalPhrasing); Assert.Equal("", model.BlendVoice); Assert.Equal(1, model.Speed);
            Assert.Equal(220, model.SentencePauseMs); Assert.Equal(500, model.ParagraphPauseMs);
            Assert.False(model.UsingNarrationDefaults);
        }
        finally { await model.DisposeAsync(); }
    });

    [Fact] public Task FullDraftPreparationKeepsExcludedCodeOutAndRejectsOversizeWithoutReplacement() => DesktopHost.Run(async () =>
    {
        var model = new MainViewModel(new() { QueuePaused = true, ExcludeCode = true }, DesktopHost.Workspace());
        try
        {
            model.Source = "Read this.\n```\nsecret code\n```\nThe end.";
            await DesktopHost.Execute(model.ComparisonDraftCommand); Assert.DoesNotContain("secret", model.ComparisonPassage);
            var previous = model.ComparisonPassage; model.Source = new string('a', 3700);
            await DesktopHost.Execute(model.ComparisonDraftCommand); Assert.Equal(previous, model.ComparisonPassage);
            Assert.Contains("shorter passage", model.StatusMessage);
        }
        finally { await model.DisposeAsync(); }
    });

    [Theory] [InlineData(760, 600)] [InlineData(1000, 800)]
    public Task ComparisonWindowRendersAtSupportedSizesWithoutBindingErrors(int width, int height) => DesktopHost.Run(async () =>
    {
        var model = new MainViewModel(new() { QueuePaused = true }, DesktopHost.Workspace());
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        var before = PresentationTraceSources.DataBindingSource.Switch.Level; PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            model.ComparisonPassage = "A question? Here is the answer.\n\nAnother idea."; model.AddComparison();
            var window = new VoiceComparisonWindow { DataContext = model }; DesktopHost.Layout(window, width, height);
            var content = (FrameworkElement)window.Content; window.Content = null; content.DataContext = model; content.Resources = window.Resources;
            if (content is Panel panel) panel.Background = window.Background;
            content.Measure(new(width, height)); content.Arrange(new(0, 0, width, height)); content.UpdateLayout();
            Assert.Contains(Children(content).OfType<Button>(), button => button.Content?.ToString() == "Render selected sample");
            Assert.Contains(Children(content).OfType<TextBox>(), box => box.IsReadOnly && box.Text == model.ComparisonPassage);
            Assert.Equal("", errors.Text.ToString());
            var image = new System.Windows.Media.Imaging.RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
            var folder = Path.GetFullPath("artifacts/desktop-comparison"); Directory.CreateDirectory(folder);
            using var output = File.Create(Path.Combine(folder, $"comparison-{width}.png")); encoder.Save(output);
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(errors); PresentationTraceSources.DataBindingSource.Switch.Level = before; await model.DisposeAsync(); }
    });
    private static IEnumerable<DependencyObject> Children(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (var nested in Children(child)) yield return nested; }
    }
    private sealed class BindingErrors : TraceListener
    {
        public StringBuilder Text { get; } = new();
        public override void Write(string? message) => Text.Append(message);
        public override void WriteLine(string? message) => Text.AppendLine(message);
    }
}
