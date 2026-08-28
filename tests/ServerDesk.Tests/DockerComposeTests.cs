using System.Text.Json;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Docker;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DockerComposeTests
{
    private static readonly DockerComposeProject Project = new(
        "serverdesk-demo",
        "running(2)",
        ["/srv/demo/compose.yaml"]);

    [Theory]
    [InlineData("ubuntu-24.04.json", "serverdesk-demo", 2)]
    [InlineData("ubuntu-26.04.json", "edge-stack", 1)]
    [InlineData("debian-13.json", "jobs", 1)]
    public void CertifiedFixturesNormalizeProjectsServicesAndConfig(string file, string projectName, int serviceCount)
    {
        var fixture = ReadFixture(file);
        using var document = JsonDocument.Parse(fixture);

        var projects = DockerComposeParser.ParseProjects(document.RootElement.GetProperty("projects").GetRawText());
        var services = DockerComposeParser.ParseServices(document.RootElement.GetProperty("services").GetRawText());
        var config = DockerComposeParser.NormalizeConfigJson(document.RootElement.GetProperty("config").GetRawText());

        var project = Assert.Single(projects);
        Assert.Equal(projectName, project.Name);
        Assert.Equal(serviceCount, services.Count);
        Assert.Contains("services", config, StringComparison.Ordinal);
        Assert.All(project.ConfigFiles, path => Assert.StartsWith("/", path, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("--project-name")]
    [InlineData("../prod")]
    [InlineData("demo;rm")]
    [InlineData(" demo ")]
    public void HostileProjectNamesAreRejected(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => DockerComposeIdentity.NormalizeProjectName(value));
    }

    [Theory]
    [InlineData("compose.yaml")]
    [InlineData("../compose.yaml")]
    [InlineData("/srv/demo/compose.yaml\n--project-name")]
    public void UnsafeConfigPathsAreRejected(string value)
    {
        Assert.ThrowsAny<ArgumentException>(() => DockerComposeIdentity.NormalizeConfigPath(value));
    }

    [Fact]
    public void ValidationSpecUsesStagedFileAndOriginalProjectDirectory()
    {
        var service = new DockerComposeService(new RecordingFactory(_ => Success()), DockerComposeOptions.Default);
        var project = new DockerComposeProject(
            "demo",
            "running(1)",
            ["/srv/demo/compose.yaml", "/srv/demo/compose.prod.yaml"]);

        RemoteEditValidationSpec validation = service.BuildConfigValidation(project);
        var resolved = validation.Resolve(ServerDesk.Application.RemoteFiles.RemotePath.Parse("/tmp/serverdesk-edit.tmp"));

        Assert.Equal("docker", validation.Executable);
        Assert.Equal(
            [
                "compose", "--project-name", "demo", "--project-directory", "/srv/demo",
                "--file", "/tmp/serverdesk-edit.tmp", "--file", "/srv/demo/compose.prod.yaml",
                "config", "--quiet",
            ],
            resolved);
    }

    [Fact]
    public async Task DownIsDestructiveNeverDeletesVolumesAndVerifiesProjectAbsence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Compose", "example.invalid", 22, "dev");
        var factory = new RecordingFactory(command =>
        {
            if (command.Arguments.Contains("version")) return Success("2.40.0\n");
            if (command.Arguments.Contains("ls")) return Success("[]");
            if (command.Arguments.Contains("ps")) return Success(ServiceJson());
            if (command.Arguments.Contains("config")) return Success(ConfigJson());
            if (command.Arguments.Contains("down")) return Success();
            return Success();
        });
        var service = new DockerComposeService(factory, DockerComposeOptions.Default);

        var result = await service.ExecuteAsync(profile, Project, DockerComposeAction.Down, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var down = Assert.Single(factory.Commands.Where(command => command.Arguments.Contains("down")));
        Assert.Equal(OperationRisk.Destructive, down.Risk);
        Assert.DoesNotContain("--volumes", down.Arguments);
        Assert.DoesNotContain("-v", down.Arguments);
        Assert.Contains("--file", down.Arguments);
        Assert.Contains(Project.PrimaryConfigFile, down.Arguments);
    }

    [Fact]
    public async Task UpUsesTokenizedIdentityAndVerifiesServices()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Compose", "example.invalid", 22, "dev");
        var factory = new RecordingFactory(command =>
        {
            if (command.Arguments.Contains("ps")) return Success(ServiceJson());
            if (command.Arguments.Contains("config")) return Success(ConfigJson());
            if (command.Arguments.Contains("up")) return Success();
            return Success();
        });
        var service = new DockerComposeService(factory, DockerComposeOptions.Default);

        var result = await service.ExecuteAsync(profile, Project, DockerComposeAction.Up, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var up = Assert.Single(factory.Commands.Where(command => command.Arguments.Contains("up")));
        Assert.Equal(OperationRisk.Destructive, up.Risk);
        Assert.Equal("docker", up.Executable);
        Assert.Equal(
            ["compose", "--project-name", "serverdesk-demo", "--file", "/srv/demo/compose.yaml", "up", "--detach"],
            up.Arguments);
    }

    [Fact]
    public async Task MutationTransportDropReturnsAmbiguousWithoutRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Compose", "example.invalid", 22, "dev");
        var factory = new RecordingFactory(command =>
        {
            if (command.Arguments.Contains("ps")) return Success(ServiceJson());
            if (command.Arguments.Contains("config")) return Success(ConfigJson());
            if (command.Arguments.Contains("restart"))
            {
                return RemoteExecutionResult.Failure(new RemoteError(RemoteErrorCode.NetworkInterrupted, "channel dropped"));
            }

            return Success();
        });
        var service = new DockerComposeService(factory, DockerComposeOptions.Default);

        var result = await service.ExecuteAsync(profile, Project, DockerComposeAction.Restart, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Single(factory.Commands.Where(command => command.Arguments.Contains("restart")));
        Assert.Equal(3, factory.Commands.Count);
    }

    [Fact]
    public async Task ProjectIdentityIsValidatedBeforeRemoteExecution()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Compose", "example.invalid", 22, "dev");
        var factory = new RecordingFactory(_ => Success());
        var service = new DockerComposeService(factory, DockerComposeOptions.Default);
        var hostile = new DockerComposeProject("demo; touch", "", ["/srv/demo/compose.yaml"]);

        await Assert.ThrowsAsync<FormatException>(() => service.InspectProjectAsync(profile, hostile, cancellationToken));
        Assert.Empty(factory.Commands);
    }

    [Fact]
    public void LogsArePlainTextSanitizedAndBounded()
    {
        var lines = DockerComposeParser.ParseLogLines("api | ok\napi | bad\u001b[31m\nthird\nfourth", 3);

        Assert.Equal(3, lines.Count);
        Assert.DoesNotContain(lines, line => line.Contains('\u001b'));
        Assert.Contains(lines, line => line.Contains('\uFFFD'));
        Assert.Equal("fourth", lines[^1]);
    }

    [Theory]
    [InlineData(DockerComposeAction.Up, OperationRisk.Destructive)]
    [InlineData(DockerComposeAction.Down, OperationRisk.Destructive)]
    [InlineData(DockerComposeAction.Restart, OperationRisk.Destructive)]
    [InlineData(DockerComposeAction.Pull, OperationRisk.Mutating)]
    [InlineData(DockerComposeAction.Build, OperationRisk.Mutating)]
    public void RiskClassificationIsExplicit(DockerComposeAction action, OperationRisk expected)
    {
        Assert.Equal(expected, DockerComposeProjection.Risk(action));
    }

    [Fact]
    public async Task AuditMarksAmbiguousComposeMutationUnknown()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Compose", "example.invalid", 22, "dev");
        var audit = new RecordingAudit();
        var inner = new StubComposeService(new DockerComposeActionResult(
            false,
            new RemoteError(RemoteErrorCode.AmbiguousState, "unknown"),
            "unknown"));
        var service = new AuditedDockerComposeService(inner, audit);

        await service.ExecuteAsync(profile, Project, DockerComposeAction.Down, cancellationToken);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("docker-compose-action", entry.Category);
        Assert.Equal(OperationRisk.Destructive, entry.Risk);
        Assert.Equal(OperationOutcome.Unknown, entry.Outcome);
        Assert.Contains(Project.Name, entry.Target!, StringComparison.Ordinal);
    }

    private static string ReadFixture(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Docker", "Compose", file));

    private static string ServiceJson() =>
        "[{\"ID\":\"aaaaaaaaaaaaaaaa\",\"Name\":\"serverdesk-demo-api-1\",\"Service\":\"api\",\"Image\":\"demo/api:latest\",\"State\":\"running\",\"Status\":\"Up\",\"Publishers\":[]}]";

    private static string ConfigJson() =>
        "{\"name\":\"serverdesk-demo\",\"services\":{\"api\":{\"image\":\"demo/api:latest\"}}}";

    private static RemoteExecutionResult Success(string output = "") =>
        RemoteExecutionResult.Success(new RemoteCommandResult(0, output, string.Empty, TimeSpan.Zero));

    private sealed class RecordingFactory : IRemoteCommandExecutorFactory
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public RecordingFactory(Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            _handler = handler;
        }

        public List<RemoteCommandSpec> Commands { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile) => new RecordingExecutor(profile.Id, Commands, _handler);
    }

    private sealed class RecordingExecutor : IRemoteCommandExecutor
    {
        private readonly List<RemoteCommandSpec> _commands;
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public RecordingExecutor(Guid profileId, List<RemoteCommandSpec> commands, Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            ServerProfileId = profileId;
            _commands = commands;
            _handler = handler;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _commands.Add(command);
            return Task.FromResult(_handler(command));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingAudit : IOperationAudit
    {
        public List<OperationAuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OperationAuditEntry>>(Entries.Take(limit).ToArray());
    }

    private sealed class StubComposeService : IDockerComposeService
    {
        private readonly DockerComposeActionResult _result;

        public StubComposeService(DockerComposeActionResult result)
        {
            _result = result;
        }

        public Task<DockerComposeSnapshotResult> InspectAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DockerComposeSnapshotResult(null, new RemoteError(RemoteErrorCode.CommandFailed, "unused")));

        public Task<DockerComposeProjectResult> InspectProjectAsync(ServerProfile profile, DockerComposeProject project, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DockerComposeProjectResult(null, new RemoteError(RemoteErrorCode.CommandFailed, "unused")));

        public Task<DockerComposeLogsResult> ReadLogsAsync(ServerProfile profile, DockerComposeProject project, int tail, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DockerComposeLogsResult([], null));

        public Task<DockerComposeActionResult> ExecuteAsync(ServerProfile profile, DockerComposeProject project, DockerComposeAction action, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);

        public RemoteEditValidationSpec BuildConfigValidation(DockerComposeProject project) => throw new NotSupportedException();
    }
}
