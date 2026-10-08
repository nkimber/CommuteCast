namespace CommuteCast.Core;

// Describe saved progress without implying chunk completion means delivery is complete.
public sealed record JobProgress(string Status, string Guidance, bool IsWorking, bool NeedsAttention)
{
    public static JobProgress Describe(Job job, bool paused = false)
    {
        if (job.CancellationRequested && !job.ExportCommitted && job.Stage is not (JobStage.Cancelled or JobStage.Failed or JobStage.Deleting))
            return new("Cancelling · waiting for active work to stop", "Your saved narration remains in the library.", true, false);
        return job.Stage switch
        {
            JobStage.Queued => new(paused ? "Queue paused · narration saved" : "Queued · narration saved",
                paused ? "Resume the queue in Your library. You do not need to submit this narration again." : "Waiting its turn. You do not need to submit this narration again.", false, false),
            JobStage.Preparing => new("Preparing spoken text", "Your narration is saved. Preparing its audio chunks.", true, false),
            JobStage.WaitingForService => new("Waiting for local speech service", "Checking or starting local speech. Audio generation has not resumed yet.", true, false),
            JobStage.Synthesizing => new($"Narrating · {job.CompletedChunks} of {job.Chunks.Count} chunks validated", "Generating the next chunk. Progress advances after each chunk is validated.", true, false),
            JobStage.Assembling => new("Assembling the MP3", "Combining the validated chunks into one audio file.", true, false),
            JobStage.Validating => new("Checking the finished audio", "The MP3 is being checked before export.", true, false),
            JobStage.Generated => new("Generated · ready to export", "Audio is saved on this laptop and awaits export.", false, false),
            JobStage.Exporting => new("Exporting the finished MP3", "Writing the validated MP3 to the selected output folder.", true, false),
            JobStage.Exported => new("Exported locally · upload unknown", "The MP3 was exported. Check OneDrive upload before listening on your phone.", false, false),
            JobStage.Failed => new("Stopped · action required", $"{job.CompletedChunks} of {job.Chunks.Count} validated segments are retained. Open the details, resolve the error, then use Retry / resume on this existing item.", false, true),
            JobStage.Cancelled => new("Cancelled · saved for resume", "Use Retry / resume in Your library to continue this existing narration.", false, false),
            JobStage.Deleting => job.Error.Length > 0
                ? new("Removal needs attention", "Inspect the removal error in Your library. The saved removal request is retained.", false, true)
                : new("Deleting narration", "Removal is in progress.", true, false),
            _ => throw new ArgumentOutOfRangeException(nameof(job.Stage))
        };
    }
}
