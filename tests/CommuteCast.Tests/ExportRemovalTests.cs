using CommuteCast.Core;
using CommuteCast.Infrastructure;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CommuteCast.Tests;

public class ExportRemovalTests
{
    [Fact]
    public async Task AnotherProcessCannotWriteReplaceOrDeleteTheValidatedExport()
    {
        using var test = new TestWorkspace(); var (job, path, store) = await ExportAsync(test);
        var other = Path.Combine(test.Destination, "unrelated.mp3"); await File.WriteAllTextAsync(other, "Unrelated audio");
        var validations = 0;
        var observer = new Observer(async (point, _) =>
        {
            if (point != ExportCheckpoint.RemovalValidated) return;
            validations++;
            using var result = await CompeteAsync(path, other);
            foreach (var operation in new[] { "write", "replace", "delete" }) Assert.Equal("refused", result.RootElement.GetProperty(operation).GetString());
            await using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            Assert.Equal(job.ExportHash, Convert.ToHexString(await SHA256.HashDataAsync(reader)));
            Assert.Equal("Unrelated audio", await File.ReadAllTextAsync(other));
        });

        await new ExportPublisher(test.Workspace, store, observer).RemoveManagedExportAsync(job, default);
        Assert.Equal(1, validations); Assert.False(File.Exists(path));
        Assert.Equal("Unrelated audio", await File.ReadAllTextAsync(other));
        Assert.Equal(job.FinalHash, await Workspace.HashFileAsync(test.Workspace.FinalPath(job)));
        Assert.True(Assert.Single(await store.LoadAsync()).ExportCommitted);

        // Establish that the same child's operations succeed when no validation handle is held.
        var control = Path.Combine(test.Destination, "control-target.mp3"); var replacement = Path.Combine(test.Destination, "control-replacement.mp3");
        await File.WriteAllTextAsync(control, "Control audio"); await File.WriteAllTextAsync(replacement, "Replacement audio");
        using var admitted = await CompeteAsync(control, replacement);
        foreach (var operation in new[] { "write", "replace", "delete" }) Assert.Equal("succeeded", admitted.RootElement.GetProperty(operation).GetString());
        Assert.False(File.Exists(control)); Assert.False(File.Exists(replacement));
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FailureOrCancellationAfterValidationPreservesExportAndStaging(bool cancel)
    {
        using var test = new TestWorkspace(); var (job, path, store) = await ExportAsync(test);
        var partial = Path.Combine(test.Destination, $".commutecast-{job.Id}.partial");
        await File.WriteAllTextAsync(partial, "Owned incomplete staging"); job.ExportStagingOwned = true;
        using var cancellation = new CancellationTokenSource();
        var observer = new Observer((point, _) =>
        {
            if (point == ExportCheckpoint.RemovalValidated)
            { if (cancel) cancellation.Cancel(); else throw new IOException("Injected removal interruption"); }
            return Task.CompletedTask;
        });
        var publisher = new ExportPublisher(test.Workspace, store, observer);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publisher.RemoveManagedExportAsync(job, cancellation.Token));
        else await Assert.ThrowsAsync<IOException>(() => publisher.RemoveManagedExportAsync(job, default));
        Assert.Equal(job.ExportHash, await Workspace.HashFileAsync(path));
        Assert.Equal("Owned incomplete staging", await File.ReadAllTextAsync(partial)); Assert.True(job.ExportStagingOwned);
        await new ExportPublisher(test.Workspace, store).RemoveManagedExportAsync(job, default);
        Assert.False(File.Exists(path)); Assert.False(File.Exists(partial)); Assert.False(job.ExportStagingOwned);
    }

    [Fact]
    public async Task SameLengthChangedExportIsPreservedWithoutTouchingOtherFiles()
    {
        using var test = new TestWorkspace(); var (job, path, store) = await ExportAsync(test);
        var changed = Enumerable.Repeat((byte)0x7F, checked((int)new FileInfo(path).Length)).ToArray();
        await File.WriteAllBytesAsync(path, changed);
        var other = Path.Combine(test.Destination, "unrelated.mp3"); await File.WriteAllTextAsync(other, "Keep audio");
        await Assert.ThrowsAsync<IOException>(() => new ExportPublisher(test.Workspace, store).RemoveManagedExportAsync(job, default));
        Assert.Equal(changed, await File.ReadAllBytesAsync(path)); Assert.Equal("Keep audio", await File.ReadAllTextAsync(other));
        Assert.True(Assert.Single(await store.LoadAsync()).ExportCommitted);
    }

    [Theory] [InlineData(FileShare.Read)] [InlineData(FileShare.ReadWrite | FileShare.Delete)]
    public async Task ExistingWriterRefusesRemovalAndAllowsRetryAfterRelease(FileShare sharing)
    {
        using var test = new TestWorkspace(); var (job, path, store) = await ExportAsync(test);
        var publisher = new ExportPublisher(test.Workspace, store);
        await using (var writer = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, sharing))
        {
            await Assert.ThrowsAsync<IOException>(() => publisher.RemoveManagedExportAsync(job, default));
            Assert.True(File.Exists(path));
        }
        Assert.Equal(job.ExportHash, await Workspace.HashFileAsync(path));
        await publisher.RemoveManagedExportAsync(job, default); Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task MissingExportIsIdempotentAndDoesNotRemoveAnUnrecordedFile()
    {
        using var test = new TestWorkspace(); var (job, path, store) = await ExportAsync(test);
        File.Delete(path); var other = Path.Combine(test.Destination, "unrelated.mp3"); await File.WriteAllTextAsync(other, "Keep audio");
        var publisher = new ExportPublisher(test.Workspace, store);
        await publisher.RemoveManagedExportAsync(job, default); await publisher.RemoveManagedExportAsync(job, default);
        Assert.Equal("Keep audio", await File.ReadAllTextAsync(other)); Assert.True(File.Exists(test.Workspace.FinalPath(job)));
    }

    [Fact]
    public async Task MissingRecordedHashCannotAuthorizeRemovingAnExistingExport()
    {
        using var test = new TestWorkspace(); var (job, path, store) = await ExportAsync(test); var hash = job.ExportHash;
        job.ExportHash = "";
        await Assert.ThrowsAsync<IOException>(() => new ExportPublisher(test.Workspace, store).RemoveManagedExportAsync(job, default));
        Assert.Equal(hash, await Workspace.HashFileAsync(path));
    }

    private static async Task<(Job Job, string Path, SqliteJobStore Store)> ExportAsync(TestWorkspace test)
    {
        var job = new Job { Title = "Removal fixture", Destination = test.Destination };
        Directory.CreateDirectory(test.Workspace.JobDirectory(job.Id));
        await File.WriteAllBytesAsync(test.Workspace.FinalPath(job), Enumerable.Range(0, 180000).Select(i => (byte)(i % 251)).ToArray());
        job.FinalHash = await Workspace.HashFileAsync(test.Workspace.FinalPath(job)); var store = new SqliteJobStore(test.Workspace);
        await new ExportPublisher(test.Workspace, store).PublishAsync(job, default);
        return (job, Path.Combine(test.Destination, job.ExportName), store);
    }

    private static async Task<JsonDocument> CompeteAsync(string path, string other)
    {
        static string Literal(string value) => "'" + value.Replace("'", "''") + "'";
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $result = [ordered]@{}
            try { [IO.File]::WriteAllText({{Literal(path)}}, 'Competing changed content'); $result.write = 'succeeded' } catch { $result.write = 'refused' }
            try { Move-Item -LiteralPath {{Literal(other)}} -Destination {{Literal(path)}} -Force; $result.replace = 'succeeded' } catch { $result.replace = 'refused' }
            try { Remove-Item -LiteralPath {{Literal(path)}}; $result.delete = 'succeeded' } catch { $result.delete = 'refused' }
            $result | ConvertTo-Json -Compress
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) }) start.ArgumentList.Add(argument);
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(); var error = child.StandardError.ReadToEndAsync();
        try
        {
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0, child.ExitCode); Assert.Equal("", await error); return JsonDocument.Parse(await output);
        }
        finally { if (!child.HasExited) { child.Kill(); await child.WaitForExitAsync(); } }
    }

    private sealed class Observer(Func<ExportCheckpoint, CancellationToken, Task> reached) : IExportObserver
    { public Task ReachedAsync(ExportCheckpoint checkpoint, CancellationToken ct) => reached(checkpoint, ct); }
}
