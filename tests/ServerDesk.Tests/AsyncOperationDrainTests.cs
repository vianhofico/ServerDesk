using ServerDesk.App;
using Xunit;

namespace ServerDesk.Tests;

public sealed class AsyncOperationDrainTests
{
    [Fact]
    public async Task StopWaitsForEveryAdmittedOperationAndRejectsNewWork()
    {
        var drain = new AsyncOperationDrain();
        Assert.True(drain.TryEnter(out var first));
        Assert.True(drain.TryEnter(out var second));

        var stopping = drain.StopAndDrainAsync();

        Assert.True(drain.IsStopping);
        Assert.False(stopping.IsCompleted);
        Assert.False(drain.TryEnter(out _));
        Assert.Throws<ObjectDisposedException>(() => drain.EnterOrThrow(new object()));

        first.Dispose();
        Assert.False(stopping.IsCompleted);

        second.Dispose();
        await stopping;
        Assert.True(stopping.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RepeatedStopCallsObserveTheSameDrainAndLeaseDisposalIsIdempotent()
    {
        var drain = new AsyncOperationDrain();
        Assert.True(drain.TryEnter(out var operation));

        var firstStop = drain.StopAndDrainAsync();
        var secondStop = drain.StopAndDrainAsync();

        Assert.Same(firstStop, secondStop);
        operation.Dispose();
        operation.Dispose();

        await Task.WhenAll(firstStop, secondStop);
        Assert.True(firstStop.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task TeardownWinsAgainstSetupGuardOnceStoppingBegins()
    {
        var drain = new AsyncOperationDrain();
        var setupCalls = 0;

        Assert.True(drain.TryRunWhileAccepting(() => setupCalls++));
        await drain.StopAndDrainAsync();

        Assert.False(drain.TryRunWhileAccepting(() => setupCalls++));
        Assert.Equal(1, setupCalls);
    }
}
