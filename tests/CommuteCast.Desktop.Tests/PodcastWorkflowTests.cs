using System.IO;
using System.Diagnostics;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommuteCast.Core;
using CommuteCast.Desktop;
using CommuteCast.Infrastructure;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public class PodcastWorkflowTests
{
    private sealed class BindingErrors : TraceListener
    {
        public readonly System.Text.StringBuilder Text = new();
        public override void Write(string? message) => Text.Append(message);
        public override void WriteLine(string? message) => Text.AppendLine(message);
    }
    private static MainViewModel Model(Workspace workspace, AppSettings? settings = null) => new(settings ?? new() { QueuePaused = true }, workspace,
        playbackOutput: new PlaybackTests.Output(), powerEvents: new LifecycleTests.Events(), notifications: new LifecycleTests.Notifications());
    [Fact]
    public Task PodcastPromptAndValidatedDialogueBecomeStaleAfterCastChanges() => DesktopHost.Run(async () =>
    {
        var model = Model(DesktopHost.Workspace());
        try
        {
            model.PodcastMode = true; model.PromptTopic = "Railways and cities"; model.Cast[0].Voice = "af_heart"; model.Cast[1].Voice = "am_adam";
            await DesktopHost.Execute(model.BuildPromptCommand); Assert.True(model.CanCopyPrompt); Assert.Contains("Speaker: spoken words", model.PromptForCopy());
            model.Source = "Alex: Let us explore railways.\nCasey: They changed cities in several ways.";
            await DesktopHost.Execute(model.ValidatePodcastCommand); Assert.Contains("Valid dialogue", model.PodcastValidation); Assert.Contains("Speaker balance", model.PodcastValidation);
            model.Cast[1].Name = "Taylor"; Assert.True(model.PromptIsStale); Assert.Contains("changed", model.PodcastValidation);
            await DesktopHost.Execute(model.ValidatePodcastCommand); Assert.True(model.HasAttention); Assert.Contains("Line 2", model.AttentionMessage); Assert.Empty(model.Jobs);
            model.Source = "Alex: Let us explore railways.\nTaylor: They changed cities in several ways.";
            await DesktopHost.Execute(model.ValidatePodcastCommand); Assert.False(model.HasAttention); Assert.Contains("Valid dialogue", model.PodcastValidation);
        }
        finally { await model.DisposeAsync(); }
    });
    [Fact]
    public Task LibrariesAndProviderVoiceBindingsRemainIndependentOfEpisodeSnapshots() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var model = Model(workspace);
        try
        {
            model.PodcastMode = true; var row = model.Cast[0]; row.Personality = model.SpeakerLibrary[0]; row.Voice = "af_heart"; row.Expertise = "Urban history";
            await DesktopHost.Execute(model.SavePersonalityCommand, row); var frozen = model.CaptureEpisode(); row.Expertise = "Changed expertise"; await DesktopHost.Execute(model.SavePersonalityCommand, row);
            Assert.Equal("Urban history", frozen.Speakers[0].Expertise); Assert.Equal("Changed expertise", model.SpeakerLibrary.Single(p => p.Id == row.Personality!.Id).Expertise);
            Assert.Contains((await workspace.LoadSettingsAsync()).SpeakerVoiceBindings, b => b.Voice == "af_heart");
            model.PodcastFormat = model.PodcastFormats.Single(f => f.Id == "panel"); await DesktopHost.Execute(model.ApplyFormatCommand); await DesktopHost.Execute(model.AddSpeakerCommand); await DesktopHost.Execute(model.AddSpeakerCommand);
            Assert.Equal(5, model.Cast.Count); Assert.Single(model.Cast, s => s.Role == "Host");
            var draft = model.CaptureDraft(); await new DraftStore(workspace).SaveAsync(draft); var saved = await new DraftStore(workspace).LoadAsync(); Assert.Equal(draft.Podcast!.Episode.Identity, saved.Podcast!.Episode.Identity);
        }
        finally { await model.DisposeAsync(); }
    });
    [Fact]
    public Task NativePodcastAndHostedControlsRenderAndBindWithoutDocker() => DesktopHost.Run(async () =>
    {
        var settings = new AppSettings { QueuePaused = true }; settings.Providers["openai"] = new("openai", new SpeechConfiguration(SpeechProviders.Get("openai").DefaultModel).Identity("openai"), ["coral", "onyx"], "ready", 0);
        var model = Model(DesktopHost.Workspace(), settings);
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        var priorLevel = PresentationTraceSources.DataBindingSource.Switch.Level; PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            model.PodcastMode = true; model.Engine = "openai"; model.PromptTopic = "The history of cities"; model.Cast[0].Voice = "coral"; model.Cast[1].Voice = "onyx";
            model.Source = "Alex: How did railways reshape a city?\nCasey: Let us begin with the distances people could travel.";
            await DesktopHost.Execute(model.ValidatePodcastCommand); model.NavigateCommand.Execute("compose");
            var window = new MainWindow(model); await Dispatcher.Yield(DispatcherPriority.Background); DesktopHost.Layout(window);
            var evidence = Path.GetFullPath(Path.Combine("TestResults", "podcast-compose.png")); Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
            Render(window, evidence);
            DesktopHost.Layout(window, 1060, 700); Render(window, Path.Combine(Path.GetDirectoryName(evidence)!, "podcast-compose-minimum.png"));
            Assert.True(((TextBox)window.FindName("SourceInput")).ActualHeight >= 160);
            var root = (System.Windows.FrameworkElement)window.Content;
            Assert.DoesNotContain(Descendants(root).OfType<TextBlock>(), t => t.Text.Contains("â") || t.Text.Contains("Â"));
            var cast = new PodcastCastWindow { DataContext = model }; DesktopHost.Layout(cast, 960, 760); await Dispatcher.Yield(DispatcherPriority.Background); Render(cast, Path.Combine(Path.GetDirectoryName(evidence)!, "podcast-cast.png"));
            model.NavigateCommand.Execute("settings"); await Dispatcher.Yield(DispatcherPriority.Background); DesktopHost.Layout(window); Render(window, Path.Combine(Path.GetDirectoryName(evidence)!, "hosted-settings.png"));
            Assert.IsType<PasswordBox>(window.FindName("HostedKeyInput")); Assert.True(model.IsHosted); Assert.True(model.SupportsDelivery); Assert.False(model.SupportsEmotion);
            model.SpeechModel = "tts-1"; Assert.False(model.SupportsDelivery); Assert.Contains("tts-1", model.SpeechModels);
            Assert.Contains("Refresh voices", model.VoiceLibrarySummary);
            Assert.Equal("", errors.Text.ToString());
        }
        finally { PresentationTraceSources.DataBindingSource.Listeners.Remove(errors); PresentationTraceSources.DataBindingSource.Switch.Level = priorLevel; await model.DisposeAsync(); }
    });
    private static void Render(System.Windows.Window window, string path)
    {
        var content = (System.Windows.FrameworkElement)window.Content; window.Content = null;
        content.DataContext = window.DataContext; content.Resources = window.Resources;
        if (content is Panel panel) panel.Background = window.Background;
        content.Measure(new(window.Width, window.Height)); content.Arrange(new(0, 0, window.Width, window.Height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual(); using (var drawing = background.RenderOpen()) drawing.DrawRectangle(window.Background, null, new(0, 0, window.Width, window.Height)); bitmap.Render(background); bitmap.Render(content); window.Content = content;
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
    private static IEnumerable<System.Windows.DependencyObject> Descendants(System.Windows.DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i); yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
