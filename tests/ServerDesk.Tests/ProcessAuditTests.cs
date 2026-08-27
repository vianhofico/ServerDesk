using ServerDesk.Application.Audit;
using ServerDesk.Application.Processes;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ProcessAuditTests
{
    [Fact]
    public async Task ForceKillIsAuditedAsDestructiveSuccess()
    {
        var audit = new RecordingAudit();
        var service = new AuditedServerProcessService(
            new StubProcessService(new ServerProcessActionResult(true, null, "sent")),
            audit);

        var result = await service.SignalAsync(CreateProfile(), 4242, ServerProcessSignal.ForceKill);

        Assert.True(result.IsSuccess);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("process-signal", entry.Category);
        Assert.Equal(OperationRisk.Destructive, entry.Risk);
        Assert.Equal(OperationOutcome.Succeeded, entry.Outcome);
        Assert.Contains("SIGKILL", entry.Summary, StringComparison.Ordinal);
        Assert.Contains("PID 4242", entry.Target, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AmbiguousSignalIsAuditedUnknownWithoutChangingError()
    {
        var error = new RemoteError(RemoteErrorCode.AmbiguousState, "unknown completion");
        var audit = new RecordingAudit();
        var service = new AuditedServerProcessService(
            new StubProcessService(new ServerProcessActionResult(false, error, error.Message)),
            audit);

        var result = await service.SignalAsync(CreateProfile(), 4242, ServerProcessSignal.Terminate);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Equal(OperationRisk.Mutating, Assert.Single(audit.Entries).Risk);
        Assert.Equal(OperationOutcome.Unknown, audit.Entries[0].Outcome);
    }

    [Fact]
    public async Task AuditFailureNeverTurnsSuccessfulSignalIntoRetryableFailure()
    {
        var service = new AuditedServerProcessService(
            new StubProcessService(new ServerProcessActionResult(true, null, "sent")),
            new ThrowingAudit());

        var result = await service.SignalAsync(CreateProfile(), 4242, ServerProcessSignal.Terminate);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Contains("do not retry", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ServerProfile CreateProfile() =>
        ServerProfile.Create(Guid.NewGuid(), "Fixture", "example.test", 22, "deploy");

    private sealed class StubProcessService : IServerProcessService
    {
        private readonly ServerProcessActionResult _result;

        public StubProcessService(ServerProcessActionResult result)
        {
            _result = result;
        }

        public Task<ServerProcessQueryResult> ListAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServerProcessQueryResult([], null));

        public Task<ServerProcessQueryResult> GetAsync(ServerProfile profile, int processId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServerProcessQueryResult([], null));

        public Task<ServerProcessActionResult> SignalAsync(
            ServerProfile profile,
            int processId,
            ServerProcessSignal signal,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    private sealed class RecordingAudit : IOperationAudit
    {
        public List<OperationAuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OperationAuditEntry>>(Entries.Take(limit).ToArray());
    }

    private sealed class ThrowingAudit : IOperationAudit
    {
        public ValueTask AppendAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default) =>
            ValueTask.FromException(new InvalidOperationException("audit unavailable"));

        public ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OperationAuditEntry>>([]);
    }
}
