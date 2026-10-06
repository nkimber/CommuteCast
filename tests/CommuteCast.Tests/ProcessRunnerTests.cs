using CommuteCast.Infrastructure;
using System.Diagnostics;

namespace CommuteCast.Tests;

public class ProcessRunnerTests
{
    private static string Shell => Environment.GetEnvironmentVariable("PSHOME") is { Length: > 0 } psHome && File.Exists(Path.Combine(psHome, "pwsh.exe")) ? Path.Combine(psHome, "pwsh.exe") : "pwsh";
    [Fact] public async Task TimeoutBoundsProcessAndOutputDraining()
    {
        var watch = Stopwatch.StartNew();
        await Assert.ThrowsAsync<TimeoutException>(() => ProcessRunner.RunAsync(Shell, ["-NoProfile", "-NonInteractive", "-Command", "[Console]::WriteLine('started'); Start-Sleep -Seconds 30"], TimeSpan.FromSeconds(1)));
        Assert.InRange(watch.Elapsed.TotalSeconds, .8, 8);
    }
    [Fact] public async Task CallerCancellationIsPreserved()
    {
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1)); var watch = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProcessRunner.RunAsync(Shell, ["-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30"], TimeSpan.FromSeconds(30), cancel.Token));
        Assert.InRange(watch.Elapsed.TotalSeconds, .8, 8);
    }
    [Fact] public async Task CapturedOutputIsCappedWhileBothPipesAreDrained()
    {
        var result = await ProcessRunner.RunAsync(Shell, ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write(('a' * 400000)); [Console]::Error.Write(('b' * 400000))"], TimeSpan.FromSeconds(15));
        Assert.Equal(0, result.ExitCode); Assert.Equal(256 * 1024, result.Output.Length); Assert.Equal(256 * 1024, result.Error.Length);
    }
}
