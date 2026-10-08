using CommuteCast.Infrastructure;
using Serilog.Context;
using Serilog.Events;
using System.Text.Json;

namespace CommuteCast.Tests;

public class AppLoggingTests
{
    [Fact]
    public void JsonLogsCorrelateEventsAndRetainSafeExceptionDetails()
    {
        using var test = new TestWorkspace();
        var directory = Path.Combine(test.Parent, "logs");
        using (var logger = AppLogging.CreateLogger(directory, "test"))
        using (LogContext.PushProperty("JobId", "job-123"))
        {
            logger.Debug("Filtered debug event");
            logger.Information("Stage {Stage}", "Synthesizing");
            try { throw new IOException("PRIVATE SCRIPT AND PATH", new ArgumentException("PRIVATE TITLE")); }
            catch (Exception error) { AppLogging.WriteFailure(logger, "SyntheticFailure", error); }
        }
        var lines = File.ReadAllLines(Directory.GetFiles(directory, "*.jsonl").Single());
        Assert.Equal(2, lines.Length);
        foreach (var line in lines)
        {
            using var json = JsonDocument.Parse(line);
            var props = json.RootElement.GetProperty("Properties");
            Assert.Equal("job-123", props.GetProperty("JobId").GetString());
            Assert.Equal("test", props.GetProperty("Component").GetString());
            Assert.Equal(32, props.GetProperty("SessionId").GetString()!.Length);
            Assert.DoesNotContain("PRIVATE", line);
            Assert.DoesNotContain(test.Parent.Replace("\\", "\\\\"), line);
        }
        using var failure = JsonDocument.Parse(lines[1]);
        var details = failure.RootElement.GetProperty("Properties").GetProperty("Failure");
        Assert.Equal(typeof(IOException).FullName, details.GetProperty("Type").GetString());
        Assert.Equal(typeof(ArgumentException).FullName, details.GetProperty("Inner").GetProperty("Type").GetString());
        Assert.NotEmpty(details.GetProperty("Frames").EnumerateArray());
    }

    [Fact]
    public void SizeRotationBoundsRetainedFilesAndDebugCanBeEnabled()
    {
        using var test = new TestWorkspace();
        var directory = Path.Combine(test.Parent, "logs");
        using (var logger = AppLogging.CreateLogger(directory, "test", LogEventLevel.Debug, 700, 3))
            for (var i = 0; i < 50; i++) logger.Debug("Rotation fixture {Index}", i);
        var files = Directory.GetFiles(directory, "*.jsonl");
        Assert.InRange(files.Length, 2, 3);
        Assert.Contains(files.SelectMany(File.ReadAllLines), line => line.Contains("Debug") && line.Contains("49"));
    }
}
