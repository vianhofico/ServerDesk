using ServerDesk.Application.Audit;
using ServerDesk.Application.Docker;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Terminal;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DockerContainerActionTests
{
    private const string ContainerId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task StartUsesValidatedTokenizedMutationAndVerifiesRunningState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");
        var diagnostics = new SequenceDiagnosticsService(
            Details(running: false),
            Details(running: true));
        var executor = new RecordingCommandExecutor(profile.Id, _ => Success());
        var factory = new RecordingCommandExecutorFactory(executor);
        var service = new DockerContainerActionService(factory, diagnostics, DockerContainerActionOptions.Default);

        var result = await service.ExecuteAsync(profile, ContainerId, DockerContainerAction.Start, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.VerifiedDetails);
        var command = Assert.Single(executor.Commands);
        Assert.Equal("docker", command.Executable);
        Assert.Equal(["container", "start", "--", ContainerId], command.Arguments);
        Assert.Equal(OperationRisk.Mutating, command.Risk);
        Assert.Equal(2, diagnostics.InspectCount);
    }

    [Fact]
    public async Task KillUsesExplicitSignalAndDestructiveRisk()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");
        var diagnostics = new SequenceDiagnosticsService(
            Details(running: true),
            Details(running: false));
        var executor = new RecordingCommandExecutor(profile.Id, _ => Success());
        var service = new DockerContainerActionService(
            new RecordingCommandExecutorFactory(executor),
            diagnostics,
            DockerContainerActionOptions.Default);

        var result = await service.ExecuteAsync(profile, ContainerId, DockerContainerAction.Kill, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var command = Assert.Single(executor.Commands);
        Assert.Equal(["container", "kill", "--signal", "KILL", "--", ContainerId], command.Arguments);
        Assert.Equal(OperationRisk.Destructive, command.Risk);
    }

    [Fact]
    public async Task RemoveRefusesRunningContainerWithoutSendingMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");
        var diagnostics = new SequenceDiagnosticsService(Details(running: true));
        var executor = new RecordingCommandExecutor(profile.Id, _ => Success());
        var factory = new RecordingCommandExecutorFactory(executor);
        var service = new DockerContainerActionService(factory, diagnostics, DockerContainerActionOptions.Default);

        var result = await service.ExecuteAsync(profile, ContainerId, DockerContainerAction.Remove, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.Empty(executor.Commands);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task RemoveDoesNotForceOrDeleteVolumesAndVerifiesAbsence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");
        var diagnostics = new SequenceDiagnosticsService(
            Details(running: false),
            new DockerContainerDetailsResult(
                null,
                new RemoteError(RemoteErrorCode.PathNotFound, "No such container.")));
        var executor = new RecordingCommandExecutor(profile.Id, _ => Success());
        var service = new DockerContainerActionService(
            new RecordingCommandExecutorFactory(executor),
            diagnostics,
            DockerContainerActionOptions.Default);

        var result = await service.ExecuteAsync(profile, ContainerId, DockerContainerAction.Remove, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var command = Assert.Single(executor.Commands);
        Assert.Equal(["container", "rm", "--", ContainerId], command.Arguments);
        Assert.DoesNotContain("--force", command.Arguments);
        Assert.DoesNotContain("-f", command.Arguments);
        Assert.DoesNotContain("--volumes", command.Arguments);
        Assert.DoesNotContain("-v", command.Arguments);
        Assert.Equal(OperationRisk.Destructive, command.Risk);
    }

    [Fact]
    public async Task AmbiguousTransportFailureIsNotRetriedOrVerifiedAsSuccess()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");
        var diagnostics = new SequenceDiagnosticsService(Details(running: true));
        var executor = new RecordingCommandExecutor(
            profile.Id,
            _ => RemoteExecutionResult.Failure(new RemoteError(
                RemoteErrorCode.NetworkInterrupted,
                "SSH channel dropped after request was sent.")));
        var factory = new RecordingCommandExecutorFactory(executor);
        var service = new DockerContainerActionService(factory, diagnostics, DockerContainerActionOptions.Default);

        var result = await service.ExecuteAsync(profile, ContainerId, DockerContainerAction.Stop, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Single(executor.Commands);
        Assert.Equal(1, factory.CreateCount);
        Assert.Equal(1, diagnostics.InspectCount);
        Assert.Contains("Refresh", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulCommandWithUnexpectedVerifiedStateReturnsAmbiguousState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");
        var diagnostics = new SequenceDiagnosticsService(
            Details(running: true),
            Details(running: true));
        var executor = new RecordingCommandExecutor(profile.Id, _ => Success());
        var service = new DockerContainerActionService(
            new RecordingCommandExecutorFactory(executor),
            diagnostics,
            DockerContainerActionOptions.Default);

        var result = await service.ExecuteAsync(profile, ContainerId, DockerContainerAction.Stop, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Equal(2, diagnostics.InspectCount);
        Assert.Single(executor.Commands);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("api")]
    [InlineData("../../api")]
    [InlineData("aaaaaaaaaaa")]
    public async Task HostileOrNonIdTargetsAreRejectedBeforeAnyRemoteCall(string value)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");
        var diagnostics = new SequenceDiagnosticsService(Details(running: false));
        var executor = new RecordingCommandExecutor(profile.Id, _ => Success());
        var factory = new RecordingCommandExecutorFactory(executor);
        var service = new DockerContainerActionService(factory, diagnostics, DockerContainerActionOptions.Default);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ExecuteAsync(profile, value, DockerContainerAction.Start, cancellationToken));
        Assert.Equal(0, diagnostics.InspectCount);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task AuditMarksAmbiguousDestructiveActionUnknownWithoutSecretData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");
        var audit = new RecordingAudit();
        var inner = new StubActionService(new DockerContainerActionResult(
            false,
            new RemoteError(RemoteErrorCode.AmbiguousState, "State unknown."),
            "State unknown."));
        var service = new AuditedDockerContainerActionService(inner, audit);

        var result = await service.ExecuteAsync(profile, ContainerId, DockerContainerAction.Remove, cancellationToken);

        Assert.False(result.IsSuccess);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("docker-container-action", entry.Category);
        Assert.Equal(OperationRisk.Destructive, entry.Risk);
        Assert.Equal(OperationOutcome.Unknown, entry.Outcome);
        Assert.Contains(ContainerId, entry.Target!, StringComparison.Ordinal);
        Assert.DoesNotContain("password", entry.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecFactoryUsesExistingTerminalAndSendsOnlyFixedCommandWithValidatedId()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");
        var inner = new RecordingTerminalSession(profile.Id);
        var innerFactory = new RecordingTerminalFactory(inner);
        var factory = new DockerExecTerminalSessionFactory(innerFactory);
        await using var session = factory.Create(profile, ContainerId);

        await session.ConnectAsync(TerminalSize.Default, cancellationToken);

        Assert.Equal(1, inner.ConnectCount);
        Assert.Equal(["docker exec -it -- aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa /bin/sh\r"], inner.Sent);
        Assert.Equal(1, innerFactory.CreateCount);
    }

    [Fact]
    public void ExecFactoryRejectsUnvalidatedNameBeforeCreatingPty()
    {
        var profile = ServerProfile.Create("Docker", "example.invalid", 22, "dev");
        var inner = new RecordingTerminalSession(profile.Id);
        var innerFactory = new RecordingTerminalFactory(inner);
        var factory = new DockerExecTerminalSessionFactory(innerFactory);

        Assert.Throws<ArgumentException>(() => factory.Create(profile, "api; touch /tmp/pwn"));
        Assert.Equal(0, innerFactory.CreateCount);
    }

    [Theory]
    [InlineData(DockerContainerAction.Start, OperationRisk.Mutating)]
    [InlineData(DockerContainerAction.Pause, OperationRisk.Mutating)]
    [InlineData(DockerContainerAction.Unpause, OperationRisk.Mutating)]
    [InlineData(DockerContainerAction.Stop, OperationRisk.Destructive)]
    [InlineData(DockerContainerAction.Restart, OperationRisk.Destructive)]
    [InlineData(DockerContainerAction.Kill, OperationRisk.Destructive)]
    [InlineData(DockerContainerAction.Remove, OperationRisk.Destructive)]
    public void RiskClassificationIsExplicit(DockerContainerAction action, OperationRisk expected)
    {
        Assert.Equal(expected, DockerContainerActionService.Risk(action));
    }

    private static DockerContainerDetailsResult Details(
        bool running,
        bool paused = false,
        string startedAt = "2026-08-28T04:00:00Z") =>
        new(
            new DockerContainerDetails(
                ContainerId,
                "api",
                "example/api:latest",
                "2026-08-28T03:00:00Z",
                "dotnet",
                ["Api.dll"],
                "1000:1000",
                "/app",
                0,
                new DockerContainerStateDetails(
                    running ? "running" : "exited",
                    running,
                    paused,
                    false,
                    false,
                    false,
                    running ? 123 : null,
                    running ? 0 : 0,
                    startedAt,
                    running ? string.Empty : "2026-08-28T04:05:00Z",
                    running ? "healthy" : string.Empty),
                [],
                new Dictionary<string, string>(),
                [],
                []),
            null);

    private static RemoteExecutionResult Success() =>
        RemoteExecutionResult.Success(new RemoteCommandResult(
            0,
            ContainerId + "\n",
            string.Empty,
            TimeSpan.FromMilliseconds(1)));

    private sealed class SequenceDiagnosticsService : IDockerContainerDiagnosticsService
    {
        private readonly Queue<DockerContainerDetailsResult> _results;

        public SequenceDiagnosticsService(params DockerContainerDetailsResult[] results)
        {
            _results = new Queue<DockerContainerDetailsResult>(results);
        }

        public DockerContainerDiagnosticsOptions Options => DockerContainerDiagnosticsOptions.Default;
        public int InspectCount { get; private set; }

        public Task<DockerContainerDetailsResult> InspectAsync(
            ServerProfile profile,
            string containerId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCount++;
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No diagnostic fixture result remains.");
            }

            return Task.FromResult(_results.Dequeue());
        }

        public Task<DockerContainerStatsResult> ReadStatsAsync(
            ServerProfile profile,
            string containerId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DockerContainerLogReadResult> ReadRecentLogsAsync(
            ServerProfile profile,
            string containerId,
            int? count = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DockerContainerLogReadResult> ReadLogsSinceAsync(
            ServerProfile profile,
            string containerId,
            string timestampToken,
            int? count = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class RecordingCommandExecutorFactory : IRemoteCommandExecutorFactory
    {
        private readonly RecordingCommandExecutor _executor;

        public RecordingCommandExecutorFactory(RecordingCommandExecutor executor) => _executor = executor;

        public int CreateCount { get; private set; }

        public IRemoteCommandExecutor Create(ServerProfile profile)
        {
            CreateCount++;
            return _executor;
        }
    }

    private sealed class RecordingCommandExecutor : IRemoteCommandExecutor
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public RecordingCommandExecutor(Guid serverProfileId, Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            ServerProfileId = serverProfileId;
            _handler = handler;
        }

        public Guid ServerProfileId { get; }
        public List<RemoteCommandSpec> Commands { get; } = [];

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(_handler(command));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubActionService : IDockerContainerActionService
    {
        private readonly DockerContainerActionResult _result;

        public StubActionService(DockerContainerActionResult result) => _result = result;

        public Task<DockerContainerActionResult> ExecuteAsync(
            ServerProfile profile,
            string containerId,
            DockerContainerAction action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingAudit : IOperationAudit
    {
        public List<OperationAuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OperationAuditEntry>>(Entries.Take(limit).ToArray());
    }

    private sealed class RecordingTerminalFactory : IRemoteTerminalSessionFactory
    {
        private readonly RecordingTerminalSession _session;

        public RecordingTerminalFactory(RecordingTerminalSession session) => _session = session;

        public int CreateCount { get; private set; }

        public IRemoteTerminalSession Create(ServerProfile profile)
        {
            CreateCount++;
            return _session;
        }
    }

    private sealed class RecordingTerminalSession : IRemoteTerminalSession
    {
        public RecordingTerminalSession(Guid serverProfileId)
        {
            ServerProfileId = serverProfileId;
        }

        public Guid ServerProfileId { get; }
        public TerminalSessionState State { get; private set; } = TerminalSessionState.Created;
        public RemoteError? LastError => null;
        public int ConnectCount { get; private set; }
        public List<string> Sent { get; } = [];

        public event Action<TerminalSessionState>? StateChanged;
        public event Action<string>? OutputReceived;

        public ValueTask ConnectAsync(TerminalSize initialSize, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ConnectCount++;
            State = TerminalSessionState.Connected;
            StateChanged?.Invoke(State);
            return ValueTask.CompletedTask;
        }

        public ValueTask SendAsync(string input, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Add(input);
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(TerminalSize size, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = TerminalSessionState.Disconnected;
            StateChanged?.Invoke(State);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        public void Emit(string value) => OutputReceived?.Invoke(value);
    }
}
