using CommuteCast.Infrastructure;

namespace CommuteCast.Tests;

public class ServiceRecoveryTests
{
    [Fact] public async Task ExhaustedOutageBudgetSurvivesNewInstanceUntilExplicitReset()
    {
        using var test = new TestWorkspace();
        await new RecoveryBudget(test.Workspace).BeginAsync("kokoro", default);
        var reopened = new RecoveryBudget(new Workspace(test.Workspace.Root));
        await Assert.ThrowsAsync<IOException>(() => reopened.BeginAsync("kokoro", default));
        await reopened.ResetAsync("kokoro"); await reopened.BeginAsync("kokoro", default);
        await reopened.BeginAsync("piper", default); // Distinct owned engine allowance.
        await Assert.ThrowsAsync<IOException>(() => reopened.BeginAsync("piper", default));
    }
    [Fact] public async Task CancellationBeforeRecoveryDoesNotSpendBudget()
    {
        using var test = new TestWorkspace(); var budget = new RecoveryBudget(test.Workspace);
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => budget.BeginAsync("kokoro", cancel.Token));
        await budget.BeginAsync("kokoro", default);
        await Assert.ThrowsAsync<ArgumentException>(() => budget.BeginAsync("../../escape", default));
    }
    [Theory]
    [InlineData("npipe:////./pipe/dockerDesktopLinuxEngine", true)]
    [InlineData("npipe:////remote/pipe/dockerDesktopLinuxEngine", false)]
    [InlineData("npipe:////./pipe/foreignDocker", false)]
    [InlineData("tcp://127.0.0.1:2375", false)]
    [InlineData("ssh://host", false)]
    public void OnlyApprovedLocalLinuxDockerPipeIsPermitted(string endpoint, bool expected) => Assert.Equal(expected, LocalSpeechProvider.IsLocalContext(endpoint));
}
