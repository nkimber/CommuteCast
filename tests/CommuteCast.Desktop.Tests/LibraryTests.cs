using System.Collections.Specialized;
using System.Windows.Controls;
using CommuteCast.Core;
using CommuteCast.Desktop;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public class LibraryTests
{
    [Fact]
    public Task ProgressAndReorderingKeepRowsAndSelectionWithoutCollectionReset() => DesktopHost.Run(async () =>
    {
        var model = new MainViewModel(new() { QueuePaused = true }, DesktopHost.Workspace());
        try
        {
            var a = new Job { Title = "API briefing", Stage = JobStage.Queued, QueuePosition = 1 };
            var b = new Job { Title = "Evening story", Stage = JobStage.Failed };
            model.RefreshJobs([a, b]); model.SelectedJob = model.Jobs[0];
            var selected = model.SelectedJob; var resets = 0; var changes = 0;
            model.LibraryJobs.CollectionChanged += (_, e) => { changes++; if (e.Action == NotifyCollectionChangedAction.Reset) resets++; };
            model.RefreshJobs([new() { Id = a.Id, Title = a.Title, Stage = JobStage.Synthesizing, CompletedChunks = 2 }, b]);
            Assert.Same(selected, model.SelectedJob); Assert.Equal(2, selected!.Progress); Assert.Equal(0, resets);
            changes = 0;
            model.RefreshJobs([selected.Job, b]);
            Assert.Equal(0, changes);
            model.LibrarySort = LibrarySort.Title;
            Assert.Same(selected, model.SelectedJob); Assert.Equal(0, resets);
        }
        finally { await model.DisposeAsync(); }
    });

    [Fact]
    public Task NativeBindingsFilterAndRevealExactSavedItem() => DesktopHost.Run(async () =>
    {
        var model = new MainViewModel(new() { QueuePaused = true }, DesktopHost.Workspace());
        try
        {
            var a = new Job { Title = "API briefing", Stage = JobStage.Exported, ExportCommitted = true };
            var b = new Job { Title = "Evening story", Stage = JobStage.Failed, CreatedUtc = DateTimeOffset.UtcNow.AddMinutes(1) };
            model.RefreshJobs([a, b]);
            var window = new MainWindow(model); DesktopHost.Layout(window, 1060, 700);
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            var list = (ListBox)window.FindName("NarrationList");
            model.LibrarySearch = "API";
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            Assert.Single(model.LibraryJobs);
            Assert.Single(list.Items); Assert.Equal(a.Id, model.SelectedJob!.Id);
            model.LibraryFilter = LibraryFilter.Stopped;
            Assert.Empty(list.Items); Assert.Null(model.SelectedJob);
            model.ViewAttentionCommand.Execute(null);
            Assert.Equal("", model.LibrarySearch); Assert.Equal(LibraryFilter.All, model.LibraryFilter);
            Assert.Equal(b.Id, model.SelectedJob!.Id); Assert.Equal(2, list.Items.Count);
        }
        finally { await model.DisposeAsync(); }
    });
}
