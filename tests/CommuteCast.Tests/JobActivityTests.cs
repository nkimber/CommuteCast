using CommuteCast.Core;
using System.Text.Json;

namespace CommuteCast.Tests;

public class JobActivityTests
{
    [Fact] public void SnapshotDetachesSavedFieldsButKeepsObservingLiveActivity()
    {
        var job = new Job { Title = "Original", Chunks = [new(0, 0, 1, "A", false)] };
        job.Activity.Start(new(10000, null, 0));
        var snapshot = job.CreateSnapshot();
        Assert.Same(job.Activity, snapshot.Activity); Assert.True(snapshot.Activity.Read().Running);
        snapshot.Title = "Changed"; snapshot.Chunks.Clear();
        Assert.Equal("Original", job.Title); Assert.Single(job.Chunks);
        job.Activity.StartAssembly(683); job.Activity.SegmentJoined();
        Assert.Equal(1, snapshot.Activity.Read().Timing.JoinedSegments);
        Assert.True(snapshot.Activity.Read().Timing.ProcessingMilliseconds >= 10000);
        Assert.NotNull(snapshot.Activity.Read().Timing.AssemblyMilliseconds);
        job.Activity.Stop(); Assert.False(snapshot.Activity.Read().Running);
        Assert.False(JsonSerializer.Deserialize<Job>(JsonSerializer.Serialize(snapshot))!.Activity.Read().Running);
    }
    [Fact] public void AssemblyHasItsOwnClockAndProgressWithoutResettingOverallTime()
    {
        var clock = new Clock(); var activity = new JobActivity(clock);
        activity.Start(null); clock.Advance(10000); activity.StartAssembly(683);
        Assert.Equal(0, activity.Read().Timing.JoinedSegments);
        Assert.Equal(683, activity.Read().Timing.AssemblyTotalSegments);
        clock.Advance(2000); activity.SegmentJoined();
        Assert.Equal(1, activity.Read().Timing.JoinedSegments);
        Assert.Equal(12000, activity.Read().Timing.ProcessingMilliseconds);
        Assert.Equal(2000, activity.Read().Timing.AssemblyMilliseconds);
        activity.FinishAssembly(); clock.Advance(1000);
        Assert.Equal(2000, activity.Read().Timing.AssemblyMilliseconds);
        Assert.Equal(13000, activity.Read().Timing.ProcessingMilliseconds);
    }
    [Fact] public void StoppedAssemblyFreezesAndRetryStartsJoiningFromZero()
    {
        var clock = new Clock(); var activity = new JobActivity(clock);
        activity.Start(null); activity.StartAssembly(2); clock.Advance(1000); activity.SegmentJoined(); activity.Stop();
        var saved = JsonSerializer.Deserialize<JobRunTiming>(JsonSerializer.Serialize(activity.Read().Timing))!;
        clock.Advance(90000); Assert.Equal(saved, activity.Read().Timing);
        activity.Start(saved); activity.StartAssembly(2);
        Assert.Equal(0, activity.Read().Timing.JoinedSegments);
        Assert.Equal(0, activity.Read().Timing.AssemblyMilliseconds);
        Assert.Equal(1000, activity.Read().Timing.ProcessingMilliseconds);
        activity.SegmentJoined(); activity.SegmentJoined(); activity.SegmentJoined();
        Assert.Equal(2, activity.Read().Timing.JoinedSegments);
    }
    [Fact] public void RemainingEstimateRecalculatesAtCompletionAndCountsDownBetweenSegments()
    {
        var clock = new Clock(); var activity = new JobActivity(clock);
        activity.Start(null);
        Assert.Null(activity.Read().Timing.EstimatedRemainingMilliseconds);
        activity.Segment(1); clock.Advance(10000); activity.CompleteSegment(1, 4);
        Assert.Equal(30000, activity.Read().Timing.EstimatedRemainingMilliseconds);
        activity.Segment(2); clock.Advance(5000);
        Assert.Equal(25000, activity.Read().Timing.EstimatedRemainingMilliseconds);
        activity.CompleteSegment(2, 4);
        Assert.Equal(15000, activity.Read().Timing.EstimatedRemainingMilliseconds);
        clock.Advance(20000);
        Assert.Equal(0, activity.Read().Timing.EstimatedRemainingMilliseconds);
        activity.CompleteSegment(3, 4);
        Assert.Equal(11666, activity.Read().Timing.EstimatedRemainingMilliseconds);
        activity.CompleteSegment(4, 4);
        Assert.Equal(0, activity.Read().Timing.EstimatedRemainingMilliseconds);
    }
    [Fact] public void RemainingEstimateIsSavedAndDoesNotCountStoppedTime()
    {
        var clock = new Clock(); var activity = new JobActivity(clock);
        activity.Start(null); clock.Advance(10000); activity.CompleteSegment(1, 3); activity.Stop();
        var saved = JsonSerializer.Deserialize<JobRunTiming>(JsonSerializer.Serialize(activity.Read().Timing))!;
        clock.Advance(90000);
        Assert.Equal(20000, activity.Read().Timing.EstimatedRemainingMilliseconds);
        activity.Start(saved); clock.Advance(5000);
        Assert.Equal(15000, activity.Read().Timing.EstimatedRemainingMilliseconds);
    }
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
