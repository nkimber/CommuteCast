using CommuteCast.Core;
using System.Windows;
using System.Windows.Input;

namespace CommuteCast.Desktop;

public sealed partial class MainViewModel
{
    private readonly Action<string> writeClipboard;
    private string generationPrompt = "";
    public string GenerationPrompt
    {
        get => generationPrompt;
        set { if (Set(ref generationPrompt, value)) ScheduleDraftSave(); }
    }
    public ICommand ViewScriptCommand { get; private set; } = null!;
    public ICommand ViewGenerationPromptCommand { get; private set; } = null!;
    public ICommand CopyLibraryPromptCommand { get; private set; } = null!;
    public ICommand EditGenerationPromptCommand { get; private set; } = null!;
    private Job LibraryTextJob(object? parameter) => parameter is JobView row
        ? Jobs.FirstOrDefault(j => j.Id == row.Id)?.Job ?? throw new ArgumentException("This narration no longer exists.")
        : RequireSelected();
    internal static string SavedPromptForCopy(Job job) => !string.IsNullOrWhiteSpace(job.GenerationPrompt)
        ? job.GenerationPrompt : throw new ArgumentException("No generation prompt was saved. Attach the exact prompt you used to generate this script.");
    private void InitializeLibraryText()
    {
        ViewScriptCommand = Command(parameter =>
        {
            var job = LibraryTextJob(parameter);
            new SavedTextWindow("Original script — " + job.Title, job.Source) { Owner = Application.Current.MainWindow }.ShowDialog();
            return Task.CompletedTask;
        }, false);
        ViewGenerationPromptCommand = Command(parameter =>
        {
            var job = LibraryTextJob(parameter);
            new SavedTextWindow("Generation prompt — " + job.Title, SavedPromptForCopy(job)) { Owner = Application.Current.MainWindow }.ShowDialog();
            return Task.CompletedTask;
        }, false);
        CopyLibraryPromptCommand = Command(parameter =>
        {
            writeClipboard(SavedPromptForCopy(LibraryTextJob(parameter)));
            StatusMessage = "Saved generation prompt copied. Paste it into another LLM to compare scripts.";
            return Task.CompletedTask;
        }, false);
        EditGenerationPromptCommand = Command(async parameter =>
        {
            var job = LibraryTextJob(parameter);
            var dialog = new SavedTextWindow("Attach generation prompt — " + job.Title, job.GenerationPrompt ?? "", editable: true)
                { Owner = Application.Current.MainWindow };
            if (dialog.ShowDialog() != true) return;
            await queue.UpdateGenerationPromptAsync(job.Id, dialog.TextContent, shutdown.Token);
            RefreshJobs(queue.Snapshot());
            StatusMessage = string.IsNullOrWhiteSpace(dialog.TextContent) ? "Saved prompt removed. The script and audio are retained." : "Generation prompt saved with this narration.";
        });
    }
}
