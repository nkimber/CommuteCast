using System.Windows.Controls;
using System.Windows.Documents;
using CommuteCast.Core;
using CommuteCast.Desktop;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public class PlaybackTests
{
    internal sealed class Output : IPlaybackOutput
    {
        public event Action? Opened; public event Action? Ended; public event Action? Failed;
        public double Duration { get; set; } = 100;
        public double Position { get; set; }
        public bool Playing { get; private set; }
        public void Open(string path) { Position = 0; Opened?.Invoke(); }
        public void Play() => Playing = true;
        public void Pause() => Playing = false;
        public void Close() => Playing = false;
        public void Complete() => Ended?.Invoke();
        public void Fail() => Failed?.Invoke();
    }
    [Fact]
    public Task PauseSeekSkipAndEndMaintainOnePlaybackSession() => DesktopHost.Run(() =>
    {
        var output = new Output(); using var playback = new PlaybackController(output);
        Assert.False(playback.CanControl);
        playback.Open("fixture.wav", "Captured title"); Assert.True(playback.IsPlaying); Assert.Equal("Captured title", playback.Title);
        playback.PositionSeconds = 50; playback.PauseCommand.Execute(null);
        Assert.False(output.Playing); Assert.Equal(50, playback.PositionSeconds);
        playback.BackCommand.Execute(null); Assert.Equal(35, playback.PositionSeconds);
        playback.ForwardCommand.Execute(null); Assert.Equal(65, playback.PositionSeconds);
        playback.PositionSeconds = 1000; Assert.Equal(100, playback.PositionSeconds);
        playback.PauseCommand.Execute(null); Assert.True(output.Playing);
        output.Complete(); Assert.False(playback.CanControl); Assert.False(playback.IsPlaying);
        playback.Open("second.wav", "Second"); output.Fail(); Assert.False(playback.IsPlaying);
        return Task.CompletedTask;
    });
    [Fact]
    public Task ReviewHighlightingIsSelectableAndPreservesThePreparedWords() => DesktopHost.Run(() =>
    {
        var prepared = TextPreparation.Prepare("API reads 24.", false, "API=A P I", new());
        var box = PreparationWindow.HighlightedText(ReviewHighlights.Spoken(prepared), "Spoken review");
        box.SelectAll();
        Assert.StartsWith(prepared.Script, box.Selection.Text); // WPF appends the document's paragraph separator
        Assert.True(box.IsReadOnly);
        Assert.Contains(box.Document.Blocks.OfType<Paragraph>().Single().Inlines.OfType<Run>(), r => r.Background is not null);
        return Task.CompletedTask;
    });
    [Fact]
    public Task MainWindowTimelineBindsToCurrentPlaybackRatherThanSelectedNarration() => DesktopHost.Run(async () =>
    {
        var output = new Output(); var model = new MainViewModel(new() { QueuePaused = true }, DesktopHost.Workspace(), playbackOutput: output);
        try
        {
            var window = new MainWindow(model); DesktopHost.Layout(window);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            model.Playback.Open("fixture.wav", "Playing item"); model.Playback.PositionSeconds = 40;
            model.RefreshJobs([new() { Title = "A different item", Stage = JobStage.Cancelled }]);
            Assert.Equal("Playing item", model.Playback.Title); Assert.Equal(40, model.Playback.PositionSeconds);
        }
        finally { await model.DisposeAsync(); }
    });
}
