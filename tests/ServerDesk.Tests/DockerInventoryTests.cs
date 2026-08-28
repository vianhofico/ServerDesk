using System.Text.Json;
using ServerDesk.Application.Docker;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DockerInventoryTests
{
    [Theory]
    [InlineData("ubuntu-24.04-inventory.json")]
    [InlineData("ubuntu-26.04-inventory.json")]
    [InlineData("debian-13-inventory.json")]
    public void CertifiedDockerFixturesParseStructuredInventory(string fixtureName)
    {
        using var fixture = LoadFixture(fixtureName);
        var root = fixture.RootElement;

        var runtime = DockerInventoryParser.ParseRuntimeVersion(root.GetProperty("version").GetRawText());
        var system = DockerInventoryParser.ParseSystemInfo(root.GetProperty("info").GetRawText());
        var containers = DockerInventoryParser.ParseContainers(ToJsonLines(root.GetProperty("containers")));
        var images = DockerInventoryParser.ParseImages(ToJsonLines(root.GetProperty("images")));
        var volumes = DockerInventoryParser.ParseVolumes(ToJsonLines(root.GetProperty("volumes")));
        var networks = DockerInventoryParser.ParseNetworks(ToJsonLines(root.GetProperty("networks")));

        Assert.Equal(DockerRuntimeStatus.Available, runtime.Status);
        Assert.False(string.IsNullOrWhiteSpace(runtime.EngineVersion));
        Assert.False(string.IsNullOrWhiteSpace(runtime.ApiVersion));
        Assert.False(string.IsNullOrWhiteSpace(system.OperatingSystem));
        Assert.True(system.CpuCount > 0);
        Assert.True(system.MemoryBytes > 0);
        Assert.Equal(system.Containers, containers.Count + Math.Max(0, system.Containers - containers.Count));
        Assert.All(containers, item => Assert.False(string.IsNullOrWhiteSpace(item.Id)));
        Assert.All(images, item => Assert.False(string.IsNullOrWhiteSpace(item.Id)));
        Assert.All(networks, item => Assert.False(string.IsNullOrWhiteSpace(item.Id)));
        Assert.NotNull(volumes);
    }

    [Fact]
    public void DockerJsonLinesAllowMissingOptionalFields()
    {
        const string output = "{\"ID\":\"abc123\",\"Names\":\"gateway\",\"Image\":\"nginx:stable\",\"State\":\"running\"}\n";

        var row = Assert.Single(DockerInventoryParser.ParseContainers(output));

        Assert.Equal("abc123", row.Id);
        Assert.Equal("gateway", row.Name);
        Assert.Equal(string.Empty, row.Ports);
        Assert.Equal(string.Empty, row.Mounts);
        Assert.Equal(string.Empty, row.Size);
    }

    [Fact]
    public void DockerJsonLinesFailClosedOnMalformedOrMissingIdentity()
    {
        Assert.Throws<FormatException>(() => DockerInventoryParser.ParseContainers("not-json\n"));
        Assert.Throws<FormatException>(() => DockerInventoryParser.ParseContainers("{\"Names\":\"missing-id\"}\n"));
    }

    [Fact]
    public void DockerOutputSanitizesControlCharactersAsPlainText()
    {
        const string output = "{\"ID\":\"abc\",\"Names\":\"api\\u001b[31m\",\"Image\":\"example/api\"}\n";

        var row = Assert.Single(DockerInventoryParser.ParseContainers(output));

        Assert.Equal("api�[31m", row.Name);
        Assert.DoesNotContain('\u001b', row.Name);
    }

    [Fact]
    public void DockerProjectionFiltersInventoryClientSide()
    {
        var containers = new[]
        {
            new DockerContainerInfo("1", "api", "example/api", "running", "Up", "8080/tcp", "data", "app", "", ""),
            new DockerContainerInfo("2", "worker", "example/worker", "exited", "Exited", "", "", "app", "", ""),
        };
        var images = new[]
        {
            new DockerImageInfo("sha256:1", "example/api", "latest", "", "", "100MB"),
            new DockerImageInfo("sha256:2", "redis", "8", "", "", "60MB"),
        };

        var containerRows = DockerInventoryProjection.FilterContainers(containers, "worker");
        var imageRows = DockerInventoryProjection.FilterImages(images, "redis");

        Assert.Single(containerRows);
        Assert.Equal("worker", containerRows[0].Name);
        Assert.Single(imageRows);
        Assert.Equal("redis", imageRows[0].Repository);
    }

    [Fact]
    public async Task InventoryServiceUsesOnlyTokenizedReadOnlyDockerCommands()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = LoadFixture("ubuntu-24.04-inventory.json");
        var root = fixture.RootElement;
        var factory = new CaptureExecutorFactory(spec => RespondWithFixture(spec, root));
        var service = new DockerInventoryService(factory, DockerInventoryOptions.Default);

        var result = await service.InspectAsync(Profile(), cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(DockerRuntimeStatus.Available, result.Snapshot.Runtime.Status);
        Assert.NotEmpty(result.Snapshot.Containers);
        Assert.NotEmpty(result.Snapshot.Images);
        Assert.NotEmpty(result.Snapshot.Networks);
        Assert.All(factory.Commands, command =>
        {
            Assert.Equal("docker", command.Executable);
            Assert.Equal(OperationRisk.ReadOnly, command.Risk);
            Assert.Equal("C", command.Environment?["LC_ALL"]);
            Assert.DoesNotContain(command.Arguments, argument => argument.Contains("sh -c", StringComparison.Ordinal));
        });
        Assert.Contains(factory.Commands, command => command.Arguments.SequenceEqual(["version", "--format", "{{json .}}"]));
        Assert.Contains(factory.Commands, command => command.Arguments.SequenceEqual(["container", "ls", "--all", "--no-trunc", "--format", "{{json .}}"]));
        Assert.Contains(factory.Commands, command => command.Arguments.SequenceEqual(["image", "ls", "--all", "--no-trunc", "--format", "{{json .}}"]));
        Assert.Contains(factory.Commands, command => command.Arguments.SequenceEqual(["volume", "ls", "--format", "{{json .}}"]));
        Assert.Contains(factory.Commands, command => command.Arguments.SequenceEqual(["network", "ls", "--no-trunc", "--format", "{{json .}}"]));
    }

    [Fact]
    public async Task DockerPermissionDeniedIsDistinctFromCliAvailability()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new CaptureExecutorFactory(spec =>
            spec.Arguments.SequenceEqual(["--version"])
                ? Success(0, "Docker version 28.0.1, build fixture\n")
                : Success(1, standardError: "permission denied while trying to connect to the Docker daemon socket\n"));
        var service = new DockerInventoryService(factory, DockerInventoryOptions.Default);

        var result = await service.InspectAsync(Profile(), cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(DockerRuntimeStatus.PermissionDenied, result.Snapshot.Runtime.Status);
        Assert.Equal(2, factory.Commands.Count);
    }

    [Fact]
    public async Task DockerDaemonUnavailableIsNotReportedAsMissingCli()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new CaptureExecutorFactory(spec =>
            spec.Arguments.SequenceEqual(["--version"])
                ? Success(0, "Docker version 28.0.1, build fixture\n")
                : Success(1, standardError: "Cannot connect to the Docker daemon at unix:///var/run/docker.sock. Is the docker daemon running?\n"));
        var service = new DockerInventoryService(factory, DockerInventoryOptions.Default);

        var result = await service.InspectAsync(Profile(), cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(DockerRuntimeStatus.DaemonUnavailable, result.Snapshot.Runtime.Status);
    }

    [Fact]
    public async Task MissingDockerCliProducesCapabilityStateWithoutFurtherCommands()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new CaptureExecutorFactory(_ => Success(127, standardError: "docker: not found\n"));
        var service = new DockerInventoryService(factory, DockerInventoryOptions.Default);

        var result = await service.InspectAsync(Profile(), cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(DockerRuntimeStatus.CliUnavailable, result.Snapshot.Runtime.Status);
        Assert.Single(factory.Commands);
    }

    [Fact]
    public async Task MalformedOneInventorySourceBecomesPartialInsteadOfDiscardingOtherRows()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var fixture = LoadFixture("ubuntu-24.04-inventory.json");
        var root = fixture.RootElement;
        var factory = new CaptureExecutorFactory(spec =>
            spec.Arguments.Count > 0 && spec.Arguments[0] == "info"
                ? Success(0, "not-json\n")
                : RespondWithFixture(spec, root));
        var service = new DockerInventoryService(factory, DockerInventoryOptions.Default);

        var result = await service.InspectAsync(Profile(), cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.True(result.IsPartial);
        Assert.Contains(result.Warnings, error => error.Code == RemoteErrorCode.ParseFailed);
        Assert.NotNull(result.Snapshot);
        Assert.Null(result.Snapshot.System);
        Assert.NotEmpty(result.Snapshot.Containers);
        Assert.NotEmpty(result.Snapshot.Images);
    }

    [Fact]
    public async Task TransportCancellationRemainsTypedAndFatal()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new CaptureExecutorFactory(_ => RemoteExecutionResult.Failure(
            new RemoteError(RemoteErrorCode.OperationCancelled, "cancelled")));
        var service = new DockerInventoryService(factory, DockerInventoryOptions.Default);

        var result = await service.InspectAsync(Profile(), cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.OperationCancelled, result.Error?.Code);
        Assert.Null(result.Snapshot);
    }

    private static RemoteExecutionResult RespondWithFixture(RemoteCommandSpec spec, JsonElement root)
    {
        if (spec.Arguments.SequenceEqual(["--version"]))
        {
            return Success(0, "Docker version 27.5.1, build fixture\n");
        }

        if (spec.Arguments.SequenceEqual(["version", "--format", "{{json .}}"] ))
        {
            return Success(0, root.GetProperty("version").GetRawText());
        }

        if (spec.Arguments.SequenceEqual(["info", "--format", "{{json .}}"] ))
        {
            return Success(0, root.GetProperty("info").GetRawText());
        }

        if (spec.Arguments.Count > 1 && spec.Arguments[0] == "container")
        {
            return Success(0, ToJsonLines(root.GetProperty("containers")));
        }

        if (spec.Arguments.Count > 1 && spec.Arguments[0] == "image")
        {
            return Success(0, ToJsonLines(root.GetProperty("images")));
        }

        if (spec.Arguments.Count > 1 && spec.Arguments[0] == "volume")
        {
            return Success(0, ToJsonLines(root.GetProperty("volumes")));
        }

        if (spec.Arguments.Count > 1 && spec.Arguments[0] == "network")
        {
            return Success(0, ToJsonLines(root.GetProperty("networks")));
        }

        return Success(2, standardError: "unexpected Docker fixture command\n");
    }

    private static JsonDocument LoadFixture(string fileName) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Docker", fileName)));

    private static string ToJsonLines(JsonElement array) =>
        string.Join('\n', array.EnumerateArray().Select(element => element.GetRawText())) + (array.GetArrayLength() == 0 ? string.Empty : "\n");

    private static ServerProfile Profile() => ServerProfile.Create("Docker", "example.test", 22, "operator");

    private static RemoteExecutionResult Success(
        int exitCode,
        string standardOutput = "",
        string standardError = "") =>
        RemoteExecutionResult.Success(new RemoteCommandResult(exitCode, standardOutput, standardError, TimeSpan.Zero));

    private sealed class CaptureExecutorFactory : IRemoteCommandExecutorFactory
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public CaptureExecutorFactory(Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            _handler = handler;
        }

        public List<RemoteCommandSpec> Commands { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile) => new CaptureExecutor(profile.Id, Commands, _handler);
    }

    private sealed class CaptureExecutor : IRemoteCommandExecutor
    {
        private readonly List<RemoteCommandSpec> _commands;
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public CaptureExecutor(
            Guid serverProfileId,
            List<RemoteCommandSpec> commands,
            Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            ServerProfileId = serverProfileId;
            _commands = commands;
            _handler = handler;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _commands.Add(command);
            return Task.FromResult(_handler(command));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
