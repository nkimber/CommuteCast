using System.IO;
using CommuteCast.Core;
using CommuteCast.Desktop;

namespace CommuteCast.Desktop.Tests;

[Collection("Desktop")]
public class NarrationToolsTests
{
    [Fact]
    public Task ApplyAndSavePresetKeepDefaultsAndQueuedChoicesIndependent() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var model = new MainViewModel(new() { QueuePaused = true }, workspace);
        try
        {
            model.SelectedPreset = model.Presets.Single(p => p.Name == "Relaxed storytelling");
            model.ApplyPresetCommand.Execute(null);
            Assert.Equal(.9, model.Speed); Assert.False(model.UsingNarrationDefaults);
            model.PresetName = "My story"; await DesktopHost.Execute(model.SavePresetCommand);
            var reopened = await workspace.LoadSettingsAsync();
            Assert.Equal(1, reopened.Speed); Assert.Equal(.9, reopened.NarrationPresets!.Single(p => p.Name == "My story").Options.Speed);
            model.UseNarrationDefaultsCommand.Execute(null); Assert.True(model.UsingNarrationDefaults);
        }
        finally { await model.DisposeAsync(); }
    });
    [Fact]
    public Task ImportCancellationAndInvalidFileRetainDraftAndSuccessfulImportNeverSubmits() => DesktopHost.Run(async () =>
    {
        var workspace = DesktopHost.Workspace(); var model = new MainViewModel(new() { QueuePaused = true }, workspace);
        try
        {
            model.Source = "Existing draft"; model.DraftTitle = "Existing title";
            var path = Path.Combine(workspace.Root, "new.md"); var text = "# New title\r\nCafé 😀"; await File.WriteAllTextAsync(path, text);
            Assert.False(await model.ImportTextAsync(path, () => false)); Assert.Equal("Existing draft", model.Source);
            await File.WriteAllTextAsync(path, "invalid\0binary");
            await Assert.ThrowsAsync<ArgumentException>(() => model.ImportTextAsync(path, () => true)); Assert.Equal("Existing title", model.DraftTitle);
            await File.WriteAllTextAsync(path, text); Assert.True(await model.ImportTextAsync(path, () => true));
            Assert.Equal(text, model.Source); Assert.Equal("New title", model.DraftTitle); Assert.Empty(model.Jobs); Assert.True(model.IsCompose);
            await Task.Delay(700); Assert.Contains("spoken words", model.EstimateSummary);
        }
        finally { await model.DisposeAsync(); }
    });
}
