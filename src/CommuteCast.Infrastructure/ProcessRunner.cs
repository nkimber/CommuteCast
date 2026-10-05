using System.Diagnostics;

namespace CommuteCast.Infrastructure;

public record ProcessResult(int ExitCode, string Output, string Error);
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {Path.GetFileName(executable)}.");
        var output = ReadBoundedAsync(process.StandardOutput);
        var error = ReadBoundedAsync(process.StandardError);
        try { await process.WaitForExitAsync(deadline.Token); }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(executable)} exceeded its {timeout.TotalSeconds:0}-second limit.");
        }
        return new(process.ExitCode, await output, await error);
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader)
    {
        var text = new System.Text.StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer)) > 0)
            if (text.Length < 256 * 1024) text.Append(buffer, 0, Math.Min(count, 256 * 1024 - text.Length));
        return text.ToString();
    }
}
