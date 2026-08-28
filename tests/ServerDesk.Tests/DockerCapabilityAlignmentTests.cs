using ServerDesk.Application.Capabilities;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DockerCapabilityAlignmentTests
{
    [Fact]
    public async Task CapabilitySnapshotReportsDockerPermissionDeniedWhenCliExistsButDaemonAccessIsDenied()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new FixtureExecutorFactory(spec => Respond(spec, DockerScenario.PermissionDenied));
        await using var service = new ServerCapabilityService(
            factory,
            new ServerCapabilityOptions(TimeSpan.FromMinutes(5)));

        var snapshot = await service.GetAsync(Profile(), cancellationToken: cancellationToken);

        Assert.Equal(CapabilityStatus.PermissionDenied, snapshot.Docker.Status);
        Assert.Contains("cannot access", snapshot.Docker.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CapabilityStatus.Unavailable, snapshot.DockerCompose.Status);
    }

    [Fact]
    public async Task CapabilitySnapshotRequiresDockerDaemonCommunicationBeforeReportingAvailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new FixtureExecutorFactory(spec => Respond(spec, DockerScenario.Available));
        await using var service = new ServerCapabilityService(
            factory,
            new ServerCapabilityOptions(TimeSpan.FromMinutes(5)));

        var snapshot = await service.GetAsync(Profile(), cancellationToken: cancellationToken);

        Assert.Equal(CapabilityStatus.Available, snapshot.Docker.Status);
        Assert.Equal("Docker version 27.5.1, build fixture", snapshot.Docker.Version);
        Assert.Contains("daemon", snapshot.Docker.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(CapabilityStatus.Available, snapshot.DockerCompose.Status);
    }

    [Fact]
    public async Task CapabilitySnapshotReportsDaemonUnavailableSeparatelyFromMissingDockerCli()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new FixtureExecutorFactory(spec => Respond(spec, DockerScenario.DaemonUnavailable));
        await using var service = new ServerCapabilityService(
            factory,
            new ServerCapabilityOptions(TimeSpan.FromMinutes(5)));

        var snapshot = await service.GetAsync(Profile(), cancellationToken: cancellationToken);

        Assert.Equal(CapabilityStatus.Unavailable, snapshot.Docker.Status);
        Assert.Contains("daemon is unavailable", snapshot.Docker.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnsupportedDockerApiCombinationMapsToUnsupportedClassification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new FixtureExecutorFactory(spec => Respond(spec, DockerScenario.Unsupported));
        await using var service = new ServerCapabilityService(
            factory,
            new ServerCapabilityOptions(TimeSpan.FromMinutes(5)));

        var snapshot = await service.GetAsync(Profile(), cancellationToken: cancellationToken);

        Assert.Equal(CapabilityStatus.Unavailable, snapshot.Docker.Status);
        Assert.Equal(CapabilitySupportClassification.Unsupported, CapabilitySupportClassifier.Classify(snapshot.Docker));
    }

    private static RemoteExecutionResult Respond(RemoteCommandSpec spec, DockerScenario scenario)
    {
        if (IsShellScript(spec, "cat /etc/os-release 2>/dev/null"))
        {
            return Success(0, "ID=ubuntu\nNAME=Ubuntu\nVERSION_ID=24.04\nPRETTY_NAME=\"Ubuntu 24.04 LTS\"\n");
        }

        if (IsShellScript(spec, "uname -m"))
        {
            return Success(0, "x86_64\n");
        }

        if (IsShellScript(spec, "uname -r"))
        {
            return Success(0, "6.8.0-fixture\n");
        }

        if (IsShellScript(spec, "id -un"))
        {
            return Success(0, "operator\n");
        }

        if (IsShellScript(spec, "id -u"))
        {
            return Success(0, "1000\n");
        }

        if (IsCommandLookup(spec, "docker"))
        {
            return Success(0);
        }

        if (spec.Executable == "docker" && spec.Arguments.SequenceEqual(["--version"]))
        {
            return Success(0, "Docker version 27.5.1, build fixture\n");
        }

        if (spec.Executable == "docker" && spec.Arguments.SequenceEqual(["version", "--format", "{{json .}}"]))
        {
            return scenario switch
            {
                DockerScenario.Available => Success(0, "{\"Client\":{\"Version\":\"27.5.1\"},\"Server\":{\"Version\":\"27.5.1\"}}\n"),
                DockerScenario.PermissionDenied => Success(1, standardError: "permission denied while trying to connect to the Docker daemon socket\n"),
                DockerScenario.DaemonUnavailable => Success(1, standardError: "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?\n"),
                DockerScenario.Unsupported => Success(1, standardError: "client version 28.0 is too new. Maximum supported API version is 1.47; minimum supported API version is 1.24\n"),
                _ => Success(2),
            };
        }

        if (spec.Executable == "docker" && spec.Arguments.SequenceEqual(["compose", "version", "--short"]))
        {
            return Success(0, "2.35.1\n");
        }

        if (IsAnyCommandLookup(spec))
        {
            return Success(1);
        }

        return Success(1, standardError: "fixture command unavailable\n");
    }

    private static bool IsShellScript(RemoteCommandSpec spec, string script) =>
        spec.Executable == "/bin/sh" &&
        spec.Arguments.Count == 2 &&
        spec.Arguments[0] == "-lc" &&
        spec.Arguments[1] == script;

    private static bool IsCommandLookup(RemoteCommandSpec spec, string command) =>
        IsShellScript(spec, $"command -v {command} >/dev/null 2>&1");

    private static bool IsAnyCommandLookup(RemoteCommandSpec spec) =>
        spec.Executable == "/bin/sh" &&
        spec.Arguments.Count == 2 &&
        spec.Arguments[0] == "-lc" &&
        spec.Arguments[1].StartsWith("command -v ", StringComparison.Ordinal);

    private static ServerProfile Profile() => ServerProfile.Create("Docker capability", "example.test", 22, "operator");

    private static RemoteExecutionResult Success(
        int exitCode,
        string standardOutput = "",
        string standardError = "") =>
        RemoteExecutionResult.Success(new RemoteCommandResult(
            exitCode,
            standardOutput,
            standardError,
            TimeSpan.FromMilliseconds(1)));

    private enum DockerScenario
    {
        Available,
        PermissionDenied,
        DaemonUnavailable,
        Unsupported,
    }

    private sealed class FixtureExecutorFactory : IRemoteCommandExecutorFactory
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public FixtureExecutorFactory(Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            _handler = handler;
        }

        public IRemoteCommandExecutor Create(ServerProfile profile) => new FixtureExecutor(profile.Id, _handler);
    }

    private sealed class FixtureExecutor : IRemoteCommandExecutor
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public FixtureExecutor(Guid serverProfileId, Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            ServerProfileId = serverProfileId;
            _handler = handler;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(command));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
