using ServerDesk.Application.Dashboard;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DashboardServiceTests
{
    [Fact]
    public async Task SamplingDelayIsCancellableWithoutASecondRemoteCommand()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new FakeCommandExecutorFactory();
        var service = new ServerDashboardService(
            factory,
            ServerDashboardOptions.Default with { SamplingInterval = TimeSpan.FromSeconds(1) });
        var profile = ServerProfile.Create("Dashboard", "example.invalid", 22, "operator");
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(30));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await service.GetAsync(profile, cancellation.Token));

        Assert.Equal(1, factory.Executor.ExecuteCount);
    }

    [Fact]
    public void InvalidDashboardThresholdsFailBeforeRemoteWork()
    {
        var options = ServerDashboardOptions.Default with
        {
            DiskWarningPercent = 99,
            DiskCriticalPercent = 95,
        };

        Assert.Throws<ArgumentException>(() => new ServerDashboardService(new FakeCommandExecutorFactory(), options));
    }

    private sealed class FakeCommandExecutorFactory : IRemoteCommandExecutorFactory
    {
        public FakeCommandExecutor Executor { get; } = new();

        public IRemoteCommandExecutor Create(ServerProfile profile) => Executor;
    }

    private sealed class FakeCommandExecutor : IRemoteCommandExecutor
    {
        public Guid ServerProfileId { get; } = Guid.NewGuid();

        public int ExecuteCount { get; private set; }

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteCount++;
            return Task.FromResult(RemoteExecutionResult.Success(new RemoteCommandResult(
                0,
                "cpu 100 0 0 900\n__SD_NET__\neth0: 1000 1 0 0 0 0 0 0 500 1 0 0 0 0 0 0\n",
                string.Empty,
                TimeSpan.FromMilliseconds(1))));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
