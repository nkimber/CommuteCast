using CommuteCast.Core;
using CommuteCast.Desktop;
using CommuteCast.Infrastructure;
using System.Windows.Controls;
using System.Windows.Threading;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public class NarrationVersionTests
{
    private static MainViewModel Model(Workspace workspace) => new(new() { QueuePaused = true }, workspace,
        playbackOutput: new PlaybackTests.Output(), powerEvents: new LifecycleTests.Events(), notifications: new LifecycleTests.Notifications());

    [Fact] public Task NewVersionCopiesSettingsPersistsDraftAndKeepsOriginalIndependent() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var model = Model(workspace);
        var original = new Job { Title = "Sunday commute", Source = "Café 😀, number 42.", Stage = JobStage.Exported, ExportCommitted = true,
            Settings = new("openai", "coral", .9, true, "Café=cafe", "original", new(), Speech: new("tts-1")) };
        try
        {
            model.RefreshJobs([original]); model.SelectedJob = model.Jobs.Single();
            await DesktopHost.Execute(model.ReuseCommand);
            Assert.True(model.IsCompose); Assert.True(model.HasVersionDraft); Assert.Single(model.Jobs);
            Assert.Equal(original.Source, model.Source); Assert.Equal("openai", model.Engine);
            Assert.Equal("coral", model.Voice); Assert.Equal(.9, model.Speed); Assert.Equal("tts-1", model.SpeechModel);
            Assert.True(model.ExcludeCode); Assert.Equal("Café=cafe", model.Pronunciation);
            Assert.Contains("Version 2", model.DraftTitle);
            model.SpeechModel = "gpt-4o-mini-tts"; model.Speed = 1.1;
            var captured = model.CaptureDraft(); await new DraftStore(workspace).SaveAsync(captured);
            var saved = await new DraftStore(workspace).LoadAsync();
            Assert.Equal(captured.Version, saved.Version);
            Assert.Equal("gpt-4o-mini-tts", saved.Version!.Settings.Speech!.Model);
            var reopened = Model(workspace);
            try
            {
                reopened.Source = saved.Source; reopened.DraftTitle = saved.Title; reopened.RestoreVersionDraft(saved.Version);
                Assert.True(reopened.HasVersionDraft); Assert.Equal(1.1, reopened.Speed); Assert.Equal(model.SpeechModel, reopened.SpeechModel);
            }
            finally { await reopened.DisposeAsync(); }
            var next = new Job { Title = model.DraftTitle, Source = model.Source, Settings = saved.Version.Settings };
            MainViewModel.AssignSubmittedVersion(next, model.CaptureSubmittedVersion(), model.DraftTitle);
            Assert.NotEqual(original.Id, next.Id); Assert.Equal(original.Id, next.Version!.ParentJobId);
            Assert.Equal(original.Id, next.Version.RootJobId); Assert.Contains("gpt-4o-mini-tts — Version 2", next.Title);
            Assert.Empty(next.Receipts); Assert.Equal(JobStage.Queued, next.Stage);
            Assert.Contains("unchanged", new JobView(next, workspace).VersionSummary);
            var store = new SqliteJobStore(workspace); await store.SaveAsync(original); await store.SaveAsync(next);
            var restored = await store.LoadAsync(); Assert.Equal(next.Version, restored.Single(j => j.Id == next.Id).Version);
            Assert.True(restored.Single(j => j.Id == original.Id).ExportCommitted);
            Assert.NotEqual(ExportPublisher.Filename(original), ExportPublisher.Filename(next));
            next.Source += " A script edit."; Assert.Contains("edited", new JobView(next, workspace).VersionSummary);
            model.RefreshJobs([original, next]); model.SelectedJob = model.Jobs.Single(j => j.Id == next.Id);
            await DesktopHost.Execute(model.OpenOriginalVersionCommand); Assert.Equal(original.Id, model.SelectedJob!.Id);
            model.CreateVersionDraft(next); Assert.Equal(3, model.CaptureSubmittedVersion()!.Number);
            Assert.Equal(next.Id, model.CaptureSubmittedVersion()!.ParentJobId);
            var custom = new Job { Title = "My comparison" };
            MainViewModel.AssignSubmittedVersion(custom, model.CaptureSubmittedVersion(), model.DraftTitle);
            Assert.Equal("My comparison", custom.Title);
            var window = new MainWindow(model); await Dispatcher.Yield(DispatcherPriority.Background); DesktopHost.Layout(window);
            await Dispatcher.Yield(DispatcherPriority.Background); DesktopHost.Layout(window, 1060, 700);
            Render(window);
            var editor = (TextBox)window.FindName("SourceInput");
            Assert.True(editor.ActualHeight >= 160, $"Editor height {editor.ActualHeight}, minimum {editor.MinHeight}, visibility {editor.Visibility}, compose {model.IsCompose}, version {model.HasVersionDraft}");
            var path = System.IO.Path.Combine(workspace.Root, "unrelated.txt"); await System.IO.File.WriteAllTextAsync(path, "New unrelated script");
            await model.ImportTextAsync(path, () => true); Assert.False(model.HasVersionDraft);
        }
        finally { await model.DisposeAsync(); }
        Assert.Equal("tts-1", original.Settings.Speech!.Model); Assert.Equal(.9, original.Settings.Speed);
    });

    [Fact] public Task PodcastVersionRestoresCastAndRequiresReviewAgain() => DesktopHost.Run(async () =>
    {
        var model = Model(DesktopHost.Workspace());
        try
        {
            model.PodcastMode = true; model.Cast[0].Voice = "af_heart"; model.Cast[1].Voice = "am_adam";
            var episode = model.CaptureEpisode();
            var original = new Job { Title = "Panel", Source = "Alex: Hello.\nCasey: Welcome.", Episode = episode };
            model.CreateVersionDraft(original);
            Assert.True(model.PodcastMode); Assert.Equal(episode.Identity, model.CaptureEpisode().Identity);
            Assert.Contains("Validate", model.PodcastValidation);
            var captured = model.CaptureDraft(); Assert.NotNull(captured.Podcast); Assert.NotNull(captured.Version);
            model.Cast[0].Name = "Changed"; Assert.NotEqual(episode.Identity, model.CaptureEpisode().Identity);
            Assert.Equal("Alex", episode.Speakers[0].Name);
        }
        finally { await model.DisposeAsync(); }
    });
    private static void Render(System.Windows.Window window)
    {
        var content = (System.Windows.FrameworkElement)window.Content; window.Content = null;
        content.DataContext = window.DataContext; content.Resources = window.Resources;
        if (content is Panel panel) panel.Background = window.Background;
        content.Measure(new(window.Width, window.Height)); content.Arrange(new(0, 0, window.Width, window.Height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(content); window.Content = content;
        var path = System.IO.Path.GetFullPath("TestResults/narration-version-compose.png");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = System.IO.File.Create(path); encoder.Save(file);
    }
}
