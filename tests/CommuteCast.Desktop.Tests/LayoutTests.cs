using System.IO;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommuteCast.Core;
using CommuteCast.Desktop;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public class LayoutTests
{
    private sealed class BindingErrors : TraceListener
    {
        public readonly System.Text.StringBuilder Text = new();
        public override void Write(string? message) => Text.Append(message);
        public override void WriteLine(string? message) => Text.AppendLine(message);
    }
    [Fact]
    public Task SmallAndDefaultLayoutsKeepActionsSeparateWithNoBindingErrorsInBothThemes() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var model = new MainViewModel(new() { QueuePaused = true }, workspace, playbackOutput: new PlaybackTests.Output(), powerEvents: new LifecycleTests.Events(), notifications: new LifecycleTests.Notifications());
        var errors = new BindingErrors(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        var previousLevel = PresentationTraceSources.DataBindingSource.Switch.Level; PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error;
        try
        {
            model.Source = "# A useful commute\n" + string.Join(" ", Enumerable.Repeat("This sample text demonstrates a calm, clear narration.", 10));
            model.RefreshJobs([new() { Title = "A completed narration", Stage = JobStage.Exported, ExportCommitted = true, DurationSeconds = 120 }, new() { Title = "A queued narration", Stage = JobStage.Queued }]);
            var window = new MainWindow(model);
            for (var theme = 0; theme < 2; theme++)
            {
                if (theme == 1) App.ToggleTheme();
                foreach (var size in new[] { (Width: 1060, Height: 700), (Width: 1380, Height: 900) })
                    foreach (var page in new[] { "compose", "library", "settings" })
                    {
                        model.NavigateCommand.Execute(page);
                        await Dispatcher.Yield(DispatcherPriority.Background);
                        var root = (FrameworkElement)window.Content;
                        if (root is Panel panel) panel.Background = (Brush)Application.Current.Resources["Canvas"];
                        root.Measure(new(size.Width, size.Height)); root.Arrange(new(0, 0, size.Width, size.Height)); root.UpdateLayout();
                        if (page == "compose")
                        {
                            var create = (Button)window.FindName("CreateMp3Button"); var review = (Button)window.FindName("ReviewTextButton"); var import = (Button)window.FindName("ImportTextButton");
                            var createBounds = create.TransformToAncestor(root).TransformBounds(new(0, 0, create.ActualWidth, create.ActualHeight));
                            var reviewBounds = review.TransformToAncestor(root).TransformBounds(new(0, 0, review.ActualWidth, review.ActualHeight));
                            var importBounds = import.TransformToAncestor(root).TransformBounds(new(0, 0, import.ActualWidth, import.ActualHeight));
                            Assert.True(create.ActualWidth > 0); Assert.True(createBounds.Bottom <= size.Height);
                            Assert.False(createBounds.IntersectsWith(reviewBounds)); Assert.False(createBounds.IntersectsWith(importBounds));
                            Assert.Same(model.QueueCommand, create.Command);
                        }
                        var bitmap = new RenderTargetBitmap(size.Width, size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(root);
                        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        using var image = File.Create(Path.Combine(workspace.Root, $"{page}-{size.Width}-{(theme == 0 ? "light" : "dark")}.png")); encoder.Save(image);
                    }
            }
            Assert.Equal("", errors.Text.ToString());
            Console.WriteLine("Offscreen layout evidence: " + workspace.Root);
        }
        finally
        {
            App.ToggleTheme(); PresentationTraceSources.DataBindingSource.Listeners.Remove(errors); PresentationTraceSources.DataBindingSource.Switch.Level = previousLevel;
            await model.DisposeAsync();
        }
    });
}
