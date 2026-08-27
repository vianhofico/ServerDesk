using ServerDesk.Application.Audit;
using ServerDesk.Application.Services;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ServiceAuditTests
{
    [Fact]
    public async Task RestartIsAuditedAsDestructiveSuccess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var audit = new RecordingAudit();
        var manager = new AuditedServerServiceManager(
            new StubManager(new ServerServiceActionResult(true, null, "done")),
            audit);

        var result = await manager.ExecuteAsync(
            CreateProfile(),
            "fixture.service",
            ServerServiceAction.Restart,
            cancellationToken);

        Assert.True(result.IsSuccess);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("service-action", entry.Category);
        Assert.Equal(OperationRisk.Destructive, entry.Risk);
        Assert.Equal(OperationOutcome.Succeeded, entry.Outcome);
        Assert.Contains("restart", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AmbiguousServiceMutationIsAuditedUnknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var error = new RemoteError(RemoteErrorCode.AmbiguousState, "unknown completion");
        var audit = new RecordingAudit();
        var manager = new AuditedServerServiceManager(
            new StubManager(new ServerServiceActionResult(false, error, error.Message)),
            audit);

        var result = await manager.ExecuteAsync(
            CreateProfile(),
            "fixture.service",
            ServerServiceAction.Stop,
            cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Equal(OperationOutcome.Unknown, Assert.Single(audit.Entries).Outcome);
    }

    [Fact]
    public async Task AuditFailureDoesNotTurnConfirmedServiceActionIntoRetryableFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var manager = new AuditedServerServiceManager(
            new StubManager(new ServerServiceActionResult(true, null, "done")),
            new ThrowingAudit());

        var result = await manager.ExecuteAsync(
            CreateProfile(),
            "fixture.service",
            ServerServiceAction.Start,
            cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Error);
        Assert.Contains("do not retry", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static ServerProfile CreateProfile() =>
        ServerProfile.Create(Guid.NewGuid(), "Fixture", "example.test", 22, "deploy");

    private sealed class StubManager : IServerServiceManager
    {
        private readonly ServerServiceActionResult _result;

        public StubManager(ServerServiceActionResult result)
        {
            _result = result;
        }

        public Task<ServerServiceQueryResult> ListAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServerServiceQueryResult([], null));

        public Task<ServerServiceQueryResult> GetAsync(
            ServerProfile profile,
            string unit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServerServiceQueryResult([], null));

        public Task<ServerServiceActionResult> ExecuteAsync(
            ServerProfile profile,
            string unit,
            ServerServiceAction action,
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
