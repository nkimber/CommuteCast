namespace CommuteCast.Core;

public static class JobRecovery
{
    public static string Instructions(Job job)
    {
        if (job.Stage != JobStage.Failed && !(job.Stage == JobStage.Deleting && job.Error.Length > 0)) return "";
        var error = job.Error;
        if (error.Contains("recovery is exhausted", StringComparison.OrdinalIgnoreCase))
            return $"An earlier failed readiness attempt used the automatic recovery allowance for {job.Settings.Engine}. It stays used across restarts and provisioning. This message does not identify the original service failure.\n\n" +
                $"1. Choose Repair speech & resume here to reset the {job.Settings.Engine} recovery allowance, start its verified container if stopped, check the captured model and resume this existing job. If repair succeeds, only choose Resume queue if paused. Alternatively, Check saved speech service performs the readiness check without resuming; if that check succeeds, go to step 4.\n" +
                "2. If readiness fails, open setup checks, then choose Check setup in Settings to identify the failed prerequisite or service check.\n" +
                "3. If Docker is unavailable, open Docker Desktop and wait for its engine to finish starting. Check setup also reports an incorrect local Linux context or missing service. Resolve the reported check, then return here and check the saved speech service again.\n" +
                "4. When readiness succeeds, choose Retry / resume on this saved narration. If the queue is paused, choose Resume queue. Your saved source and validated chunks are retained; you do not need to submit another copy.";
        if (error.Contains("not provisioned", StringComparison.OrdinalIgnoreCase))
            return "1. Complete the one-time speech setup with scripts/Provision-Speech.ps1 -Build from the application package or repository folder.\n2. Choose Check saved speech service.\n3. When ready, use Retry / resume on this saved narration, then Resume queue if paused.";
        if (error.Contains("image changed", StringComparison.OrdinalIgnoreCase) || error.Contains("model or image changed", StringComparison.OrdinalIgnoreCase) || error.Contains("image identity", StringComparison.OrdinalIgnoreCase))
            return "Restore the original compatible speech service and image captured by this narration, then check its speech service and retry. If you deliberately changed the model, use Use as a new draft to create a separate narration with the new settings. Existing validated chunks cannot be mixed with another model.";
        if (job.Stage == JobStage.Deleting)
            return "Close any player or application holding the managed audio file and check folder permissions. Retry deletion on the existing item. If the error reports changed or unrecognized files, retain them for inspection rather than deleting them manually.";
        return job.FailureCategory switch
        {
            FailureCategory.ServiceConnection or FailureCategory.ServiceContract or FailureCategory.Prerequisite =>
                "1. Open setup checks and choose Check setup in Settings. Resolve the reported missing tool, Docker or service check.\n2. Choose Check saved speech service here.\n3. When ready, choose Retry / resume on this saved item. If readiness still fails, copy its error and setup results for diagnosis.",
            FailureCategory.Timeout when job.FailedStage is JobStage.WaitingForService or JobStage.Synthesizing =>
                "Wait for any current speech request to finish. Open setup checks and run Check setup, then check this narration's speech service. When readiness succeeds, use Retry / resume. A pending speech request must settle before another can start.",
            FailureCategory.Export => "Check that the output folder still exists and that you can write to it. Close applications holding the output file. Use Retry / resume, or Retry export to another folder to choose a writable destination; validated finished audio is retained. Preserve any changed or unrecognized files for inspection.",
            FailureCategory.Storage or FailureCategory.AccessDenied => "Check free disk space and permissions for the private workspace and output folder. Close applications holding the affected files. After resolving the displayed error, use Retry / resume; choose Resume queue if dispatch is paused. Preserve any changed or unrecognized files for inspection.",
            FailureCategory.AudioValidation => "Check the encoder using Settings → Check encoder, then check this narration's speech service. Resolve any reported tool or service failure and retry the saved item. If audio validation fails again, copy these details for diagnosis.",
            _ => "Open setup checks and run Check setup. Resolve the specific error shown above, then retry this saved item. If the cause remains unclear, copy these details and the setup results for diagnosis."
        };
    }
}
