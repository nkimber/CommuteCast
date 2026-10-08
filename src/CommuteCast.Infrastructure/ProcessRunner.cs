using System.Diagnostics;

namespace CommuteCast.Infrastructure;

public record ProcessResult(int ExitCode, string Output, string Error);
public static partial class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        var timer = Stopwatch.StartNew();
        var tool = Path.GetFileName(executable);
        Serilog.Log.Debug("Tool {Tool} started with timeout {TimeoutSeconds} seconds", tool, timeout.TotalSeconds);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8, CreateNoWindow = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {Path.GetFileName(executable)}.");
        var output = ReadBoundedAsync(process.StandardOutput, deadline.Token);
        var error = ReadBoundedAsync(process.StandardError, deadline.Token);
        try
        {
            await process.WaitForExitAsync(deadline.Token);
            await Task.WhenAll(output, error).WaitAsync(deadline.Token);
            Serilog.Log.Write(process.ExitCode == 0 ? Serilog.Events.LogEventLevel.Debug : Serilog.Events.LogEventLevel.Warning, "Tool {Tool} exited with {ExitCode} in {ElapsedMs} ms", tool, process.ExitCode, timer.ElapsedMilliseconds);
            return new(process.ExitCode, await output, await error);
        }
        catch (OperationCanceledException)
        {
            Serilog.Log.Warning("Tool {Tool} interrupted after {ElapsedMs} ms; caller cancelled {CallerCancelled}", tool, timer.ElapsedMilliseconds, ct.IsCancellationRequested);
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (TimeoutException) { }
            try { await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(2)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { }
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"{Path.GetFileName(executable)} exceeded its {timeout.TotalSeconds:0}-second limit.");
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new System.Text.StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
            if (text.Length < 256 * 1024) text.Append(buffer, 0, Math.Min(count, 256 * 1024 - text.Length));
        return text.ToString();
    }
}
