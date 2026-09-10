using ServerDesk.Application.Capabilities;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ServerCapabilityLifecycleTests
{
    [Fact]
    public async Task DisposeAsyncWaitsForAdmittedScanAndRejectsNewWork()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var scanStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseScan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var profile = ServerProfile.Create("Lifecycle", "example.invalid", 22, "operator");
        var service = new ServerCapabilityService(
            new BlockingCommandExecutorFactory(scanStarted, releaseScan),
            new ServerCapabilityOptions(TimeSpan.FromMinutes(5)));

        var scanTask = service.GetAsync(profile, forceRefresh: true, cancellationToken).AsTask();
        await scanStarted.Task.WaitAsync(cancellationToken);

        var disposeTask = service.DisposeAsync().AsTask();

        Assert.False(disposeTask.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.GetAsync(profile, forceRefresh: true, cancellationToken).AsTask());

        releaseScan.TrySetResult();
        var snapshot = await scanTask;
        await disposeTask;

        Assert.Equal(profile.Id, snapshot.ServerProfileId);
        await service.DisposeAsync();
    }

    private sealed class BlockingCommandExecutorFactory : IRemoteCommandExecutorFactory
    {
        private readonly TaskCompletionSource _scanStarted;
        private readonly TaskCompletionSource _releaseScan;

        public BlockingCommandExecutorFactory(
            TaskCompletionSource scanStarted,
            TaskCompletionSource releaseScan)
        {
            _scanStarted = scanStarted;
            _releaseScan = releaseScan;
        }

        public IRemoteCommandExecutor Create(ServerProfile profile) =>
            new BlockingCommandExecutor(profile.Id, _scanStarted, _releaseScan);
    }

    private sealed class BlockingCommandExecutor : IRemoteCommandExecutor
    {
        private readonly TaskCompletionSource _scanStarted;
        private readonly TaskCompletionSource _releaseScan;
        private bool _firstCommand = true;

        public BlockingCommandExecutor(
            Guid serverProfileId,
            TaskCompletionSource scanStarted,
            TaskCompletionSource releaseScan)
        {
            ServerProfileId = serverProfileId;
            _scanStarted = scanStarted;
            _releaseScan = releaseScan;
        }

        public Guid ServerProfileId { get; }

        public async Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(command);
            cancellationToken.ThrowIfCancellationRequested();
            if (_firstCommand)
            {
                _firstCommand = false;
                _scanStarted.TrySetResult();
                await _releaseScan.Task.WaitAsync(cancellationToken);
            }

            return RemoteExecutionResult.Success(new RemoteCommandResult(
                1,
                string.Empty,
                string.Empty,
                TimeSpan.FromMilliseconds(1)));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
