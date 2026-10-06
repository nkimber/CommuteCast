using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Text.Json;

namespace CommuteCast.Tests;

public class DiagnosticsTests
{
    [Fact] public async Task DiagnosticBundleContainsTransitionsTimingsAndFailureButNoContentOrPaths()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace);
        var job = new Job { Title = "Sensitive title", Source = "Private source material", Prepared = TextPreparation.Prepare("Private script text"), Destination = test.Destination, Error = "Sensitive exception " + test.Parent, Settings = new("kokoro", "af_heart", 1, false, "Secret dictionary", "Sensitive fingerprint") };
        await store.SaveAsync(job);
        job.Stage = JobStage.WaitingForService; await store.SaveAsync(job);
        job.Stage = JobStage.Failed; job.FailedStage = JobStage.WaitingForService; job.FailureCategory = FailureCategory.Timeout; job.Attempts = 1; await store.SaveAsync(job);
        var json = await new DiagnosticExporter(test.Workspace, store).BuildAsync([job], "FFmpeg test version");
        foreach (var secret in new[] { job.Title, job.Source, job.Prepared.Script, job.Settings.Pronunciation, job.Settings.ProviderFingerprint, job.Error, test.Parent, test.Destination }) Assert.DoesNotContain(secret, json);
        using var document = JsonDocument.Parse(json); var root = document.RootElement;
        Assert.Equal(3, root.GetProperty("events").GetArrayLength());
        Assert.Equal("Timeout", root.GetProperty("jobs")[0].GetProperty("failureCategory").GetString());
        Assert.Equal("WaitingForService", root.GetProperty("jobs")[0].GetProperty("failedStage").GetString());
        Assert.Equal(1, root.GetProperty("jobs")[0].GetProperty("Attempts").GetInt32());
        Assert.True(root.GetProperty("events")[1].GetProperty("ElapsedSincePreviousMs").GetDouble() >= 0);
    }
    [Fact] public async Task DeleteRemovesAssociatedDiagnosticHistory()
    {
        using var test = new TestWorkspace(); var store = new SqliteJobStore(test.Workspace); var job = new Job();
        await store.SaveAsync(job); Assert.Single(await store.ReadDiagnosticEventsAsync());
        await store.RemoveAsync(job.Id); Assert.Empty(await store.ReadDiagnosticEventsAsync());
    }
}
