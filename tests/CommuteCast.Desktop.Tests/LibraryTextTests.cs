using CommuteCast.Core;
using CommuteCast.Desktop;
using CommuteCast.Infrastructure;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public class LibraryTextTests
{
    [Fact] public Task CopiedWritingPromptStaysAssociatedDespiteLaterBuilderChanges() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var copied = new List<string>();
        var model = new MainViewModel(new() { QueuePaused = true }, workspace, playbackOutput: new PlaybackTests.Output(),
            powerEvents: new LifecycleTests.Events(), notifications: new LifecycleTests.Notifications(), copyText: copied.Add);
        try
        {
            model.PromptTopic = "Railways"; await DesktopHost.Execute(model.BuildPromptCommand);
            Assert.Equal("", model.GenerationPrompt);
            await DesktopHost.Execute(model.CopyPromptCommand);
            var original = Assert.Single(copied); Assert.Equal(original, model.GenerationPrompt);
            model.PromptTopic = "Cities"; await DesktopHost.Execute(model.BuildPromptCommand);
            Assert.NotEqual(original, model.PromptText); Assert.Equal(original, model.GenerationPrompt);
            model.Source = "The generated railway script.";
            var draft = model.CaptureDraft(); await new DraftStore(workspace).SaveAsync(draft);
            Assert.Equal(original, (await new DraftStore(workspace).LoadAsync()).GenerationPrompt);
            var job = new Job { Title = "Railways", Source = model.Source, GenerationPrompt = draft.GenerationPrompt };
            model.RefreshJobs([job]); model.SelectedJob = model.Jobs.Single();
            Assert.True(model.SelectedJob.HasGenerationPrompt);
            await DesktopHost.Execute(model.CopyLibraryPromptCommand, model.SelectedJob); Assert.Equal(original, copied[^1]);
            model.CreateVersionDraft(job); Assert.Equal(original, model.GenerationPrompt);
            job.GenerationPrompt = null; model.RefreshJobs([job]);
            Assert.False(model.SelectedJob!.HasGenerationPrompt);
            Assert.Throws<ArgumentException>(() => MainViewModel.SavedPromptForCopy(job));
            model.CreateVersionDraft(job); Assert.Equal("", model.GenerationPrompt);
        }
        finally { await model.DisposeAsync(); }
    });
    [Fact] public Task ScriptAndPromptReadersCopyExactTextAndPromptEditorIsEditable() => DesktopHost.Run(() =>
    {
        var original = "Exact text 😀\r\nSecond line\nFinal line."; string? copied = null;
        var reader = new SavedTextWindow("Original script", original, copyText: value => copied = value);
        var grid = (Grid)reader.Content; var editor = Assert.Single(grid.Children.OfType<TextBox>());
        Assert.True(editor.IsReadOnly);
        var buttons = grid.Children.OfType<StackPanel>().Single().Children.OfType<Button>();
        buttons.Single(button => Equals(button.Content, "Copy text")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.Equal(original, copied);
        Render(reader, "saved-script.png");
        var prompt = new SavedTextWindow("Generation prompt", original); Render(prompt, "saved-prompt.png");
        var edit = new SavedTextWindow("Attach generation prompt", original, editable: true);
        Assert.False(((Grid)edit.Content).Children.OfType<TextBox>().Single().IsReadOnly);
        Assert.Contains(((Grid)edit.Content).Children.OfType<StackPanel>().Single().Children.OfType<Button>(), button => Equals(button.Content, "Save prompt"));
        return Task.CompletedTask;
    });
    private static void Render(Window window, string name)
    {
        var content = (FrameworkElement)window.Content; window.Content = null; content.Resources = window.Resources;
        if (content is Panel panel) panel.Background = window.Background;
        content.Measure(new(700, 450)); content.Arrange(new(0, 0, 700, 450)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(700, 450, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content); window.Content = content;
        var path = System.IO.Path.GetFullPath(System.IO.Path.Combine("TestResults", name)); System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = System.IO.File.Create(path); encoder.Save(file);
    }
}
