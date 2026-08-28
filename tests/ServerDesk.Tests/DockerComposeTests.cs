using ServerDesk.Application.Docker;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DockerComposeTests
{
    private static readonly RemotePath ComposeFile = RemotePath.Parse("/srv/app/compose.yaml");
    private static readonly RemotePath OverrideFile = RemotePath.Parse("/srv/app/compose.prod.yaml");
    private static readonly DockerComposeProject Project = new(
        "serverdesk",
        "running(1)",
        [ComposeFile, OverrideFile],
        RemotePath.Parse("/srv/app"));

    [Fact]
    public void ProjectParserPreservesExplicitMultiFileIdentity()
    {
        const string json = "[{\"Name\":\"serverdesk\",\"Status\":\"running(1)\",\"ConfigFiles\":\"/srv/app/compose.yaml,/srv/app/compose.prod.yaml\"}]";

        var projects = DockerComposeParser.ParseProjects(json);

        var project = Assert.Single(projects);
        Assert.Equal("serverdesk", project.Name);
        Assert.Equal([ComposeFile, OverrideFile], project.ConfigFiles);
        Assert.Equal(RemotePath.Parse("/srv/app"), project.WorkingDirectory);
    }

    [Fact]
    public void ServiceParserAcceptsJsonLinesAndNormalizesPublisherSummary()
    {
        const string jsonLines = "{\"Name\":\"api-1\",\"Service\":\"api\",\"State\":\"running\",\"Status\":\"Up\",\"Health\":\"healthy\",\"Image\":\"example/api\",\"Publishers\":[{\"PublishedPort\":\"8080\",\"TargetPort\":\"80\",\"Protocol\":\"tcp\"}]}\n";

        var services = DockerComposeParser.ParseServices(jsonLines);

        var service = Assert.Single(services);
        Assert.Equal("api", service.Service);
        Assert.Equal("8080->80/tcp", service.Ports);
    }

    [Theory]
    [InlineData("--project-name")]
    [InlineData("UPPER")]
    [InlineData("../app")]
    [InlineData("app;touch")]
    [InlineData("app name")]
    public void UnsafeProjectIdentityIsRejected(string value)
    {
        Assert.Throws<ArgumentException>(() => DockerComposeService.ValidateProjectName(value));
    }

    [Fact]
    public async Task ListProjectsUsesReadOnlyStructuredComposeV2Command()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Compose", "example.invalid", 22, "dev");
        var executor = new RecordingExecutor(profile.Id, spec =>
        {
            if (spec.Arguments.SequenceEqual(["compose", "version", "--short"]))
            {
                return Success("2.39.1\n");
            }

            if (spec.Arguments.SequenceEqual(["compose", "ls", "--all", "--format", "json"]))
            {
                return Success("[{\"Name\":\"serverdesk\",\"Status\":\"running(1)\",\"ConfigFiles\":\"/srv/app/compose.yaml\"}]");
            }

            return Failure(9, "unexpected command");
        });
        var service = CreateService(new SingleExecutorFactory(executor));

        var result = await service.ListProjectsAsync(profile, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, executor.Commands.Count);
        Assert.All(executor.Commands, command => Assert.Equal(OperationRisk.ReadOnly, command.Risk));
        Assert.All(executor.Commands, command => Assert.Equal("docker", command.Executable));
    }

    [Fact]
    public async Task DownUsesExplicitProjectAndFileChainWithoutVolumeDeletionThenVerifiesEmptyState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Compose", "example.invalid", 22, "dev");
        var executor = new RecordingExecutor(profile.Id, spec =>
        {
            if (spec.Arguments.Contains("config"))
            {
                return Success();
            }

            if (spec.Arguments.LastOrDefault() == "down")
            {
                return Success();
            }

            if (spec.Arguments.Contains("ps"))
            {
                return Success(string.Empty);
            }

            return Failure(9, "unexpected command");
        });
        var service = CreateService(new SingleExecutorFactory(executor));

        var result = await service.ExecuteAsync(profile, Project, DockerComposeAction.Down, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var mutation = Assert.Single(executor.Commands, command => command.Risk == OperationRisk.Destructive);
        Assert.Equal("docker", mutation.Executable);
        Assert.Equal([
            "compose", "--project-name", "serverdesk", "--project-directory", "/srv/app",
            "--file", "/srv/app/compose.yaml", "--file", "/srv/app/compose.prod.yaml", "down"], mutation.Arguments);
        Assert.DoesNotContain("--volumes", mutation.Arguments);
        Assert.DoesNotContain("-v", mutation.Arguments);
        Assert.DoesNotContain("--remove-orphans", mutation.Arguments);
    }

    [Fact]
    public async Task AmbiguousMutationTransportFailureIsNeverRetried()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Compose", "example.invalid", 22, "dev");
        var mutationCalls = 0;
        var executor = new RecordingExecutor(profile.Id, spec =>
        {
            if (spec.Arguments.Contains("config"))
            {
                return Success();
            }

            mutationCalls++;
            return RemoteExecutionResult.Failure(new RemoteError(
                RemoteErrorCode.NetworkInterrupted,
                "SSH channel dropped after the request may have reached Docker."));
        });
        var service = CreateService(new SingleExecutorFactory(executor));

        var result = await service.ExecuteAsync(profile, Project, DockerComposeAction.Up, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Equal(1, mutationCalls);
        Assert.Equal(2, executor.Commands.Count);
    }

    [Fact]
    public async Task InvalidStagedYamlIsRejectedBeforeWritableEditorSave()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Compose", "example.invalid", 22, "dev");
        var fileSystem = new MemoryFileSystem(profile.Id);
        var editor = new RecordingEditor();
        var executor = new RecordingExecutor(profile.Id, spec =>
        {
            if (spec.Arguments.Contains("config") &&
                spec.Arguments.Any(argument => argument.StartsWith("/tmp/serverdesk-compose-", StringComparison.Ordinal)))
            {
                return Failure(1, "services.api.image must be a string");
            }

            return Success();
        });
        var service = new DockerComposeService(
            new SingleExecutorFactory(executor),
            new SingleFileSystemFactory(fileSystem),
            editor,
            DockerComposeOptions.Default);
        var original = Document(ComposeFile, "services:\n  api:\n    image: example/api\n");

        var result = await service.SaveConfigAsync(
            profile,
            Project,
            original,
            "services:\n  api:\n    image:\n      nested: invalid\n",
            privileged: false,
            cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.ValidationFailed);
        Assert.Equal(0, editor.WritableSaveCount);
        Assert.Contains(executor.Commands, command => command.Arguments.Contains("config"));
        Assert.True(fileSystem.DeleteCount > 0);
    }

    [Fact]
    public async Task ValidCandidateIsSavedAsRawTextThenLiveConfigIsVerified()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Compose", "example.invalid", 22, "dev");
        var fileSystem = new MemoryFileSystem(profile.Id);
        var editor = new RecordingEditor();
        var executor = new RecordingExecutor(profile.Id, _ => Success());
        var service = new DockerComposeService(
            new SingleExecutorFactory(executor),
            new SingleFileSystemFactory(fileSystem),
            editor,
            DockerComposeOptions.Default);
        var original = Document(ComposeFile, "x-common: &common\n  restart: unless-stopped\nservices: {}\n");
        const string edited = "x-common: &common\n  restart: unless-stopped\nservices:\n  api:\n    <<: *common\n    image: example/api\n";

        var result = await service.SaveConfigAsync(
            profile,
            Project,
            original,
            edited,
            privileged: false,
            cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(1, editor.WritableSaveCount);
        Assert.Equal(edited, editor.LastSavedText);
        Assert.Equal(2, executor.Commands.Count(command => command.Arguments.Contains("config")));
    }

    [Theory]
    [InlineData(DockerComposeAction.Up, OperationRisk.Destructive)]
    [InlineData(DockerComposeAction.Down, OperationRisk.Destructive)]
    [InlineData(DockerComposeAction.Restart, OperationRisk.Destructive)]
    [InlineData(DockerComposeAction.Pull, OperationRisk.Mutating)]
    [InlineData(DockerComposeAction.Build, OperationRisk.Mutating)]
    public void RiskClassificationIsExplicit(DockerComposeAction action, OperationRisk expected)
    {
        Assert.Equal(expected, DockerComposeService.Risk(action));
    }

    private static DockerComposeService CreateService(IRemoteCommandExecutorFactory factory) =>
        new(factory, new ThrowingFileSystemFactory(), new RecordingEditor(), DockerComposeOptions.Default);

    private static RemoteEditorDocument Document(RemotePath path, string text) =>
        new(
            new RemoteFileEntry(
                path,
                path.Name,
                RemoteFileKind.File,
                text.Length,
                DateTimeOffset.UtcNow,
                1000,
                1000,
                RemoteUnixPermissions.FromMode(644)),
            text);

    private static RemoteExecutionResult Success(string output = "") =>
        RemoteExecutionResult.Success(new RemoteCommandResult(0, output, string.Empty, TimeSpan.FromMilliseconds(1)));

    private static RemoteExecutionResult Failure(int exitCode, string error) =>
        RemoteExecutionResult.Success(new RemoteCommandResult(exitCode, string.Empty, error, TimeSpan.FromMilliseconds(1)));

    private sealed class RecordingExecutor : IRemoteCommandExecutor
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public RecordingExecutor(Guid serverProfileId, Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            ServerProfileId = serverProfileId;
            _handler = handler;
        }

        public Guid ServerProfileId { get; }
        public List<RemoteCommandSpec> Commands { get; } = [];

        public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(_handler(command));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleExecutorFactory : IRemoteCommandExecutorFactory
    {
        private readonly RecordingExecutor _executor;

        public SingleExecutorFactory(RecordingExecutor executor) => _executor = executor;

        public IRemoteCommandExecutor Create(ServerProfile profile) => _executor;
    }

    private sealed class RecordingEditor : IRemoteFileEditorService
    {
        public int WritableSaveCount { get; private set; }
        public string? LastSavedText { get; private set; }

        public ValueTask<RemoteEditorDocument> LoadAsync(ServerProfile profile, RemotePath path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<RemoteEditorSaveResult> SaveWritableAsync(
            ServerProfile profile,
            RemoteEditorDocument original,
            string editedText,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WritableSaveCount++;
            LastSavedText = editedText;
            return ValueTask.FromResult(new RemoteEditorSaveResult(true, false, "saved"));
        }

        public ValueTask<RemoteEditorSaveResult> SavePrivilegedAsync(
            ServerProfile profile,
            RemoteEditorDocument original,
            string editedText,
            RemoteEditValidationSpec? validation,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class MemoryFileSystem : IRemoteFileSystem
    {
        public MemoryFileSystem(Guid profileId) => ServerProfileId = profileId;

        public Guid ServerProfileId { get; }
        public bool IsConnected { get; private set; }
        public int DeleteCount { get; private set; }

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask UploadAsync(Stream source, RemotePath destination, long? totalBytes = null, bool overwrite = false, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask SetPermissionsAsync(RemotePath path, RemoteUnixPermissions permissions, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask DeleteFileAsync(RemotePath path, CancellationToken cancellationToken = default)
        {
            DeleteCount++;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<RemoteFileEntry> StatAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask CreateDirectoryAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask RenameAsync(RemotePath source, RemotePath destination, bool overwrite = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DeleteDirectoryAsync(RemotePath path, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DownloadAsync(RemotePath source, Stream destination, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleFileSystemFactory : IRemoteFileSystemFactory
    {
        private readonly IRemoteFileSystem _fileSystem;

        public SingleFileSystemFactory(IRemoteFileSystem fileSystem) => _fileSystem = fileSystem;

        public IRemoteFileSystem Create(ServerProfile profile) => _fileSystem;
    }

    private sealed class ThrowingFileSystemFactory : IRemoteFileSystemFactory
    {
        public IRemoteFileSystem Create(ServerProfile profile) => throw new InvalidOperationException("File system should not be used by this test.");
    }
}
