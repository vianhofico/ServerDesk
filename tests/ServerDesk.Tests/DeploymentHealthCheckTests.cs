using ServerDesk.Application.Deployment;
using ServerDesk.Application.Docker;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Services;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DeploymentHealthCheckTests
{
    [Fact]
    public async Task HttpHealthUsesReadOnlyCurlTokensAndNeverShell()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new RecordingFactory(_ => Success());
        var runner = new DeploymentHealthCheckRunner(factory, new ServiceManager(), new DockerInventory(), Options());
        var check = new DeploymentHealthCheck(
            "api-http",
            DeploymentHealthCheckKind.Http,
            "https://127.0.0.1:8443/health");

        var result = await runner.RunAsync(Profile(), check, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var command = Assert.Single(factory.Commands);
        Assert.Equal("curl", command.Executable);
        Assert.Equal(OperationRisk.ReadOnly, command.Risk);
        Assert.Equal(
            ["--fail", "--silent", "--show-error", "--location", "--max-time", "2", "--output", "/dev/null", "--", "https://127.0.0.1:8443/health"],
            command.Arguments);
    }

    [Fact]
    public async Task TcpAndProcessHealthRemainTokenizedAndReadOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new RecordingFactory(command =>
            command.Executable == "ps" ? Success("4242\n") : Success());
        var runner = new DeploymentHealthCheckRunner(factory, new ServiceManager(), new DockerInventory(), Options());

        var tcp = await runner.RunAsync(
            Profile(),
            new DeploymentHealthCheck("tcp", DeploymentHealthCheckKind.Tcp, "127.0.0.1", 8080),
            cancellationToken);
        var process = await runner.RunAsync(
            Profile(),
            new DeploymentHealthCheck("process", DeploymentHealthCheckKind.Process, "4242"),
            cancellationToken);

        Assert.True(tcp.IsSuccess, tcp.Error?.Message);
        Assert.True(process.IsSuccess, process.Error?.Message);
        Assert.Equal(2, factory.Commands.Count);
        Assert.Equal(["-z", "-w", "2", "127.0.0.1", "8080"], factory.Commands[0].Arguments);
        Assert.Equal(["-p", "4242", "-o", "pid="], factory.Commands[1].Arguments);
        Assert.All(factory.Commands, command => Assert.Equal(OperationRisk.ReadOnly, command.Risk));
        Assert.DoesNotContain(factory.Commands, command => command.Executable is "sh" or "bash" or "cmd" or "powershell");
    }

    [Fact]
    public async Task ReadOnlyHealthFailureRetriesOnlyWithinConfiguredBound()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new RecordingFactory(_ => Failure(new RemoteError(RemoteErrorCode.CommandTimeout, "timeout")));
        var runner = new DeploymentHealthCheckRunner(
            factory,
            new ServiceManager(),
            new DockerInventory(),
            new DeploymentOptions(TimeSpan.FromSeconds(1), 3, TimeSpan.Zero, 10));

        var result = await runner.RunAsync(
            Profile(),
            new DeploymentHealthCheck("process", DeploymentHealthCheckKind.Process, "4242"),
            cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(3, factory.Commands.Count);
        Assert.All(factory.Commands, command => Assert.Equal(OperationRisk.ReadOnly, command.Risk));
    }

    private static DeploymentOptions Options() => new(TimeSpan.FromSeconds(2), 1, TimeSpan.Zero, 10);

    private static ServerProfile Profile() => ServerProfile.Create("Deploy", "example.invalid", 22, "dev");

    private static RemoteExecutionResult Success(string output = "") =>
        RemoteExecutionResult.Success(new RemoteCommandResult(0, output, string.Empty, TimeSpan.Zero));

    private static RemoteExecutionResult Failure(RemoteError error) => RemoteExecutionResult.Failure(error);

    private sealed class RecordingFactory : IRemoteCommandExecutorFactory
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public RecordingFactory(Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            _handler = handler;
        }

        public List<RemoteCommandSpec> Commands { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile) => new RecordingExecutor(profile.Id, this);

        private sealed class RecordingExecutor : IRemoteCommandExecutor
        {
            private readonly RecordingFactory _owner;

            public RecordingExecutor(Guid profileId, RecordingFactory owner)
            {
                ServerProfileId = profileId;
                _owner = owner;
            }

            public Guid ServerProfileId { get; }

            public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default)
            {
                _owner.Commands.Add(command);
                return Task.FromResult(_owner._handler(command));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ServiceManager : IServerServiceManager
    {
        public Task<ServerServiceQueryResult> ListAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServerServiceQueryResult([], null));

        public Task<ServerServiceQueryResult> GetAsync(ServerProfile profile, string unit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServerServiceQueryResult(
                [new ServerServiceInfo(unit, "service", "loaded", "active", "running", "enabled", 42, string.Empty)],
                null));

        public Task<ServerServiceActionResult> ExecuteAsync(ServerProfile profile, string unit, ServerServiceAction action, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class DockerInventory : IDockerInventoryService
    {
        public Task<DockerInventoryResult> InspectAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DockerInventoryResult(
                new DockerInventorySnapshot(
                    new DockerRuntimeState(DockerRuntimeStatus.Available, "", "", "", "", "", ""),
                    null,
                    [],
                    [],
                    [],
                    []),
                [],
                null));
    }
}
