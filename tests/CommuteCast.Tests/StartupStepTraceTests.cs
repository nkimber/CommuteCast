using CommuteCast.Infrastructure;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using System.Collections.Concurrent;

namespace CommuteCast.Tests;

public class StartupStepTraceTests
{
    [Fact] public async Task BlockedStepLeavesHeartbeatAndCompletionStopsIt()
    {
        var sink = new Sink(); using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        var step = new StartupStepTrace(StartupPhase.QueueValidation, logger, TimeSpan.FromMilliseconds(20));
        try { await sink.Heartbeat.Task.WaitAsync(TimeSpan.FromSeconds(15)); }
        finally { step.Complete(); step.Dispose(); }
        var count = sink.Events.Count; await Task.Delay(80);
        Assert.Equal(count, sink.Events.Count);
        Assert.Contains(sink.Events, e => e.Level == LogEventLevel.Warning && e.Properties["StartupStep"].ToString() == "QueueValidation" && e.Properties.ContainsKey("ElapsedMs"));
        Assert.Equal("\"Completed\"", sink.Events.Last().Properties["Outcome"].ToString());
    }
    [Fact] public void InterruptedStepRetainsItsIdentityWithoutPrivateData()
    {
        var sink = new Sink(); using var logger = new LoggerConfiguration().WriteTo.Sink(sink).CreateLogger();
        using (new StartupStepTrace(StartupPhase.EditorConstruction, logger)) { }
        Assert.Equal(2, sink.Events.Count);
        Assert.Equal("\"Interrupted\"", sink.Events.Last().Properties["Outcome"].ToString());
        Assert.All(sink.Events, e => Assert.Equal("EditorConstruction", e.Properties["StartupStep"].ToString()));
    }
    private sealed class Sink : ILogEventSink
    {
        public ConcurrentQueue<LogEvent> Events { get; } = new();
        public TaskCompletionSource Heartbeat { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Emit(LogEvent e) { Events.Enqueue(e); if (e.Level == LogEventLevel.Warning) Heartbeat.TrySetResult(); }
    }
}
