namespace CommuteCast.Core;

public sealed record JobRunTiming(long ProcessingMilliseconds, int? SegmentNumber, long SegmentMilliseconds)
{
    public long? EstimatedNarrationTotalMilliseconds { get; init; }
    public long? AssemblyMilliseconds { get; init; }
    public int JoinedSegments { get; init; }
    public int AssemblyTotalSegments { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public long? EstimatedRemainingMilliseconds => EstimatedNarrationTotalMilliseconds is { } total
        ? Math.Max(0, total - ProcessingMilliseconds) : null;
}
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
    private long? estimatedNarrationTotalMilliseconds;
    private long assemblyStarted;
    private long? assemblyMilliseconds;
    private bool assembling;
    private int joinedSegments, assemblyTotalSegments;

    public void Start(JobRunTiming? saved)
    {
        lock (gate)
        {
            processingMilliseconds = Math.Clamp(saved?.ProcessingMilliseconds ?? 0, 0, TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond / 2);
            estimatedNarrationTotalMilliseconds = saved?.EstimatedNarrationTotalMilliseconds;
            assemblyMilliseconds = saved?.AssemblyMilliseconds;
            joinedSegments = saved?.JoinedSegments ?? 0;
            assemblyTotalSegments = saved?.AssemblyTotalSegments ?? 0;
            assembling = false;
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
    public void CompleteSegment(int completed, int total)
    {
        lock (gate)
        {
            var elapsed = ReadCore().Timing.ProcessingMilliseconds;
            estimatedNarrationTotalMilliseconds = completed > 0 && total >= completed
                ? (long)Math.Min(TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond / 2,
                    elapsed * ((double)total / completed)) : null;
            segmentNumber = null; segmentMilliseconds = 0;
        }
    }
    public void Update(string message, bool isWaiting = false)
    {
        lock (gate) { notice = message; waiting = isWaiting; }
    }
    public void StartAssembly(int total)
    {
        lock (gate)
        {
            assemblyStarted = time.GetTimestamp(); assemblyMilliseconds = 0; assembling = true;
            joinedSegments = 0; assemblyTotalSegments = total;
            segmentNumber = null; segmentMilliseconds = 0;
            notice = $"Joining 0 of {total} segments";
        }
    }
    public void SegmentJoined()
    {
        lock (gate)
        {
            if (!assembling || joinedSegments >= assemblyTotalSegments) return;
            joinedSegments++;
            notice = $"Joined {joinedSegments} of {assemblyTotalSegments} segments";
        }
    }
    public void FinishAssembly()
    {
        lock (gate)
        {
            if (!assembling) return;
            assemblyMilliseconds = ReadCore().Timing.AssemblyMilliseconds;
            assembling = false;
        }
    }
    public void Stop()
    {
        lock (gate)
        {
            if (!running) return;
            var snapshot = ReadCore(); processingMilliseconds = snapshot.Timing.ProcessingMilliseconds;
            segmentMilliseconds = snapshot.Timing.SegmentMilliseconds; running = false; waiting = false;
            assemblyMilliseconds = snapshot.Timing.AssemblyMilliseconds; assembling = false;
        }
    }
    public JobActivitySnapshot Read() { lock (gate) return ReadCore(); }
    private JobActivitySnapshot ReadCore() => new(running, waiting, notice, new(
        processingMilliseconds + (running ? (long)time.GetElapsedTime(started).TotalMilliseconds : 0), segmentNumber,
        running && segmentNumber is not null ? (long)time.GetElapsedTime(segmentStarted).TotalMilliseconds : segmentMilliseconds)
        {
            EstimatedNarrationTotalMilliseconds = estimatedNarrationTotalMilliseconds,
            AssemblyMilliseconds = assembling ? (assemblyMilliseconds ?? 0) + (long)time.GetElapsedTime(assemblyStarted).TotalMilliseconds : assemblyMilliseconds,
            JoinedSegments = joinedSegments, AssemblyTotalSegments = assemblyTotalSegments
        });
}
