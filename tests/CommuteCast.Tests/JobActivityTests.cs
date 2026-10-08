using CommuteCast.Core;
using System.Text.Json;

namespace CommuteCast.Tests;

public class JobActivityTests
{
    [Fact] public void WaitingKeepsBothClocksRunningAndStopFreezesThem()
    {
        var clock = new Clock(); var activity = new JobActivity(clock);
        activity.Start(new(2000, null, 0)); activity.Segment(4);
        activity.Update("Waiting for the previous speech request", true); clock.Advance(3000);
        var waiting = activity.Read(); Assert.True(waiting.Running); Assert.True(waiting.Waiting);
        Assert.Equal(5000, waiting.Timing.ProcessingMilliseconds); Assert.Equal(3000, waiting.Timing.SegmentMilliseconds);
        activity.Stop(); clock.Advance(9000);
        Assert.False(activity.Read().Running); Assert.False(activity.Read().Waiting);
        Assert.Equal(waiting.Timing, activity.Read().Timing);
    }
    [Fact] public void ResumeAccumulatesProcessingTimeAndNewSegmentResetsOnlyItsClock()
    {
        var clock = new Clock(); var activity = new JobActivity(clock);
        activity.Start(null); activity.Segment(1); clock.Advance(5000); activity.Stop();
        var saved = activity.Read().Timing; clock.Advance(90000);
        activity.Start(saved); activity.Segment(2); clock.Advance(2000);
        Assert.Equal(new JobRunTiming(7000, 2, 2000), activity.Read().Timing);
        activity.EndSegment(); Assert.Null(activity.Read().Timing.SegmentNumber);
    }
    [Fact] public void TimingSurvivesSerializationWhileLiveStateDoesNot()
    {
        var job = new Job { RunTiming = new(12345, 4, 6789) };
        job.Activity.Start(job.RunTiming); job.Activity.Update("Transient wait", true);
        var json = JsonSerializer.Serialize(job);
        Assert.DoesNotContain("Transient wait", json); Assert.DoesNotContain("\"Activity\"", json);
        var restored = JsonSerializer.Deserialize<Job>(json)!;
        Assert.Equal(job.RunTiming, restored.RunTiming); Assert.False(restored.Activity.Read().Running);
        Assert.Equal(job.Fingerprint, restored.Fingerprint);
    }
    [Fact] public void OldJobRecordsRemainReadableWithoutTiming()
    {
        var restored = JsonSerializer.Deserialize<Job>("{}")!;
        Assert.Null(restored.RunTiming); Assert.False(restored.Activity.Read().Running);
    }
    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => timestamp;
        public void Advance(long milliseconds) => timestamp += milliseconds;
    }
}
