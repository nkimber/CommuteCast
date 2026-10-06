using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class OperationLifetimeTests
{
    [Fact] public async Task StopFencesNewActionsAndWaitsForAllAdmittedWork()
    {
        var lifetime = new OperationLifetime(); var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var second = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = lifetime.RunAsync(() => first.Task); var b = lifetime.RunAsync(() => second.Task);
        var stop = lifetime.StopAsync(); Assert.False(stop.IsCompleted); var invoked = false;
        await Assert.ThrowsAsync<OperationCanceledException>(() => lifetime.RunAsync(() => { invoked = true; return Task.CompletedTask; })); Assert.False(invoked);
        first.SetResult(); await a; Assert.False(stop.IsCompleted); second.SetResult(); await b; await stop;
        Assert.Same(stop, lifetime.StopAsync());
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task FailureAndCancellationStillReleaseTheStopBoundary(bool cancelled)
    {
        var lifetime = new OperationLifetime(); var action = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = lifetime.RunAsync(() => action.Task); var stop = lifetime.StopAsync();
        if (cancelled) action.SetCanceled(); else action.SetException(new IOException("Cannot save"));
        await Assert.ThrowsAnyAsync<Exception>(() => work); await stop; Assert.True(stop.IsCompletedSuccessfully);
    }
    [Fact] public async Task AlreadyIdleStopNeverAdmitsLaterWork()
    {
        var lifetime = new OperationLifetime(); await lifetime.RunAsync(() => Task.CompletedTask); await lifetime.StopAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lifetime.RunAsync(() => Task.CompletedTask));
    }
}
