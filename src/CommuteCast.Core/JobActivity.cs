namespace CommuteCast.Core;

public sealed record JobRunTiming(long ProcessingMilliseconds, int? SegmentNumber, long SegmentMilliseconds);
public sealed record JobActivitySnapshot(bool Running, bool Waiting, string Notice, JobRunTiming Timing);

// Monotonic live clocks; only checkpointed totals belong in durable job history.
public sealed class JobActivity(TimeProvider? clock = null)
{
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    private readonly object gate = new();
    private bool running, waiting;
    private string notice = "";
    private long started, segmentStarted, processingMilliseconds, segmentMilliseconds;
    private int? segmentNumber;

    public void Start(JobRunTiming? saved)
    {
        lock (gate)
        {
            processingMilliseconds = Math.Clamp(saved?.ProcessingMilliseconds ?? 0, 0, TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond / 2);
            started = time.GetTimestamp(); running = true; waiting = false;
            segmentNumber = null; segmentMilliseconds = 0; notice = "Preparing saved narration";
        }
    }
    public void Segment(int number)
    {
        lock (gate) { segmentNumber = number; segmentStarted = time.GetTimestamp(); segmentMilliseconds = 0; }
    }
    public void EndSegment()
    {
        lock (gate) { segmentNumber = null; segmentMilliseconds = 0; }
    }
    public void Update(string message, bool isWaiting = false)
    {
        lock (gate) { notice = message; waiting = isWaiting; }
    }
    public void Stop()
    {
        lock (gate)
        {
            if (!running) return;
            var snapshot = ReadCore(); processingMilliseconds = snapshot.Timing.ProcessingMilliseconds;
            segmentMilliseconds = snapshot.Timing.SegmentMilliseconds; running = false; waiting = false;
        }
    }
    public JobActivitySnapshot Read() { lock (gate) return ReadCore(); }
    private JobActivitySnapshot ReadCore() => new(running, waiting, notice, new(
        processingMilliseconds + (running ? (long)time.GetElapsedTime(started).TotalMilliseconds : 0), segmentNumber,
        running && segmentNumber is not null ? (long)time.GetElapsedTime(segmentStarted).TotalMilliseconds : segmentMilliseconds));
}
