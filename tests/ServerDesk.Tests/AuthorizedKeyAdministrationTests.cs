using System.Text;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.UserAdministration;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class AuthorizedKeyAdministrationTests
{
    private const string KeyOne = "ssh-ed25519 AQIDBAUGBwgJCgsMDQ4PEA== dev-one";
    private const string KeyTwo = "ssh-ed25519 ERITFBUWFxgZGhscHR4fIA== dev-two";

    [Fact]
    public async Task PrivateKeyMaterialIsRejectedBeforeRemoteInspection()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new KeyFixtureState();
        var commands = new RecordingFactory(state);
        var service = Raw(commands, state);

        var result = await service.PreviewAsync(
            Profile(),
            User(),
            new AuthorizedKeyMutationRequest(
                AuthorizedKeyMutationKind.Add,
                "dev",
                "-----BEGIN OPENSSH PRIVATE KEY-----"),
            cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.InvalidEndpoint, result.Error?.Code);
        Assert.Equal(0, commands.CreateCount);
    }

    [Fact]
    public async Task PermissionDeniedStatIsNotTreatedAsMissingAuthorizedKeyState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new KeyFixtureState { DenyDirectoryStat = true };
        var commands = new RecordingFactory(state);
        var service = Guarded(commands, state);

        var result = await service.LoadAsync(Profile(), User(), cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PermissionDenied, result.Error?.Code);
        Assert.DoesNotContain(commands.Commands, command =>
            command.Arguments.Contains("cat", StringComparer.Ordinal));
    }

    [Fact]
    public async Task DirectoryOwnerDriftAfterPreviewRejectsBeforeAnyMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new KeyFixtureState();
        var commands = new RecordingFactory(state);
        var service = Guarded(commands, state);
        var profile = Profile();
        var user = User();
        var preview = Assert.IsType<AuthorizedKeyMutationPreview>((await service.PreviewAsync(
            profile,
            user,
            new AuthorizedKeyMutationRequest(AuthorizedKeyMutationKind.Add, "dev", KeyTwo),
            cancellationToken)).Preview);

        state.DirectoryUserId = 2000;
        var result = await service.ExecuteAsync(profile, user, preview, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.False(result.AmbiguousState);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.DoesNotContain(commands.Commands, IsMutation);
    }

    [Fact]
    public async Task SuccessfulAddUsesAtomicTypedMutationAndVerifiesSafeOwnershipModes()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new KeyFixtureState();
        var commands = new RecordingFactory(state);
        var service = Guarded(commands, state);
        var profile = Profile();
        var user = User();
        var preview = Assert.IsType<AuthorizedKeyMutationPreview>((await service.PreviewAsync(
            profile,
            user,
            new AuthorizedKeyMutationRequest(AuthorizedKeyMutationKind.Add, "dev", KeyTwo),
            cancellationToken)).Preview);

        var result = await service.ExecuteAsync(profile, user, preview, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.AmbiguousState);
        Assert.Equal((uint)1000, state.DirectoryUserId);
        Assert.Equal((uint)1000, state.DirectoryGroupId);
        Assert.Equal(700, state.DirectoryMode);
        Assert.Equal((uint)1000, state.FileUserId);
        Assert.Equal((uint)1000, state.FileGroupId);
        Assert.Equal(600, state.FileMode);
        Assert.Contains(KeyOne, state.FileText, StringComparison.Ordinal);
        Assert.Contains(KeyTwo, state.FileText, StringComparison.Ordinal);
        Assert.Contains(commands.Commands, command =>
            command.Executable == "sudo" &&
            command.Arguments.Contains("install", StringComparer.Ordinal) &&
            command.Arguments.Contains("700", StringComparer.Ordinal));
        Assert.Contains(commands.Commands, command =>
            command.Executable == "sudo" &&
            command.Arguments.Contains("mv", StringComparer.Ordinal));
    }

    [Fact]
    public async Task PartialDirectoryOwnerChangeOnDeterministicFailureBecomesAmbiguous()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new KeyFixtureState
        {
            DirectoryUserId = 2000,
            DirectoryGroupId = 2000,
            DirectoryMode = 700,
            FailFileInstall = true,
        };
        var commands = new RecordingFactory(state);
        var service = Guarded(commands, state);
        var profile = Profile();
        var user = User();
        var preview = Assert.IsType<AuthorizedKeyMutationPreview>((await service.PreviewAsync(
            profile,
            user,
            new AuthorizedKeyMutationRequest(AuthorizedKeyMutationKind.Add, "dev", KeyTwo),
            cancellationToken)).Preview);

        var result = await service.ExecuteAsync(profile, user, preview, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.AmbiguousState);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Equal((uint)1000, state.DirectoryUserId);
        Assert.Equal((uint)1000, state.DirectoryGroupId);
        Assert.DoesNotContain(KeyTwo, state.FileText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeterministicFailureWithUnchangedMetadataRemainsDeterministic()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new KeyFixtureState { FailDirectoryInstall = true };
        var commands = new RecordingFactory(state);
        var service = Guarded(commands, state);
        var profile = Profile();
        var user = User();
        var preview = Assert.IsType<AuthorizedKeyMutationPreview>((await service.PreviewAsync(
            profile,
            user,
            new AuthorizedKeyMutationRequest(AuthorizedKeyMutationKind.Add, "dev", KeyTwo),
            cancellationToken)).Preview);

        var result = await service.ExecuteAsync(profile, user, preview, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.False(result.AmbiguousState);
        Assert.Equal(RemoteErrorCode.PermissionDenied, result.Error?.Code);
        Assert.DoesNotContain(KeyTwo, state.FileText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemovingConnectedUsersKeyShowsRestrictionWarning()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new KeyFixtureState();
        var commands = new RecordingFactory(state);
        var service = Guarded(commands, state);
        var key = AuthorizedKeyAdministrationService.ParseSinglePublicKey(KeyOne);

        var result = await service.PreviewAsync(
            Profile(),
            User(),
            new AuthorizedKeyMutationRequest(
                AuthorizedKeyMutationKind.Remove,
                "dev",
                Fingerprint: key.Fingerprint),
            cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(ConnectedUserImpactKind.PossibleRestriction, result.Preview!.ConnectedUserImpact.Kind);
        Assert.Contains("cannot guarantee", result.Preview.ConnectedUserImpact.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OperationRisk.Destructive, result.Preview.Risk);
    }

    private static GuardedAuthorizedKeyAdministrationService Guarded(
        RecordingFactory commands,
        KeyFixtureState state) =>
        new(
            Raw(commands, state),
            commands,
            AuthorizedKeyAdministrationOptions.Default);

    private static AuthorizedKeyAdministrationService Raw(
        RecordingFactory commands,
        KeyFixtureState state) =>
        new(
            commands,
            new FixtureFileSystemFactory(state),
            AuthorizedKeyAdministrationOptions.Default);

    private static ServerProfile Profile() =>
        ServerProfile.Create("Keys", "example.invalid", 22, "dev");

    private static LocalUserInfo User() =>
        new(
            "dev",
            1000,
            1000,
            "dev",
            [],
            "/home/dev",
            "/bin/bash",
            UserLockState.Unlocked,
            false,
            false);

    private static bool IsMutation(RemoteCommandSpec command) =>
        command.Executable == "sudo" &&
        command.Arguments.Count > 1 &&
        command.Arguments[1] is "install" or "mv";

    private sealed class KeyFixtureState
    {
        public bool DirectoryExists { get; set; } = true;
        public uint DirectoryUserId { get; set; } = 1000;
        public uint DirectoryGroupId { get; set; } = 1000;
        public int DirectoryMode { get; set; } = 700;
        public bool FileExists { get; set; } = true;
        public uint FileUserId { get; set; } = 1000;
        public uint FileGroupId { get; set; } = 1000;
        public int FileMode { get; set; } = 600;
        public string FileText { get; set; } = KeyOne + "\n";
        public bool DenyDirectoryStat { get; init; }
        public bool FailDirectoryInstall { get; init; }
        public bool FailFileInstall { get; init; }
        public Dictionary<string, byte[]> UserStages { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> PrivilegedStages { get; } = new(StringComparer.Ordinal);
    }

    private sealed class RecordingFactory : IRemoteCommandExecutorFactory
    {
        private readonly KeyFixtureState _state;

        public RecordingFactory(KeyFixtureState state) => _state = state;

        public int CreateCount { get; private set; }
        public List<RemoteCommandSpec> Commands { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile)
        {
            CreateCount++;
            return new RecordingExecutor(profile.Id, _state, Commands);
        }
    }

    private sealed class RecordingExecutor : IRemoteCommandExecutor
    {
        private readonly KeyFixtureState _state;
        private readonly List<RemoteCommandSpec> _commands;

        public RecordingExecutor(Guid serverProfileId, KeyFixtureState state, List<RemoteCommandSpec> commands)
        {
            ServerProfileId = serverProfileId;
            _state = state;
            _commands = commands;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _commands.Add(command);
            return Task.FromResult(Handle(command));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private RemoteExecutionResult Handle(RemoteCommandSpec command)
        {
            if (command.Executable != "sudo" || command.Arguments.Count < 2 || command.Arguments[0] != "-n")
            {
                throw new InvalidOperationException($"Unexpected command: {command.Executable} {string.Join(' ', command.Arguments)}");
            }

            if (command.Arguments[1] == "stat")
            {
                var path = command.Arguments[^1];
                if (path == "/home/dev/.ssh")
                {
                    if (_state.DenyDirectoryStat)
                    {
                        return CommandResult(1, string.Empty, "stat: cannot statx '/home/dev/.ssh': Permission denied");
                    }

                    return _state.DirectoryExists
                        ? Success($"{_state.DirectoryUserId}:{_state.DirectoryGroupId}:{_state.DirectoryMode}")
                        : CommandResult(1, string.Empty, "stat: cannot statx '/home/dev/.ssh': No such file or directory");
                }

                if (path == "/home/dev/.ssh/authorized_keys")
                {
                    return _state.FileExists
                        ? Success($"{_state.FileUserId}:{_state.FileGroupId}:{_state.FileMode}")
                        : CommandResult(1, string.Empty, "stat: cannot statx '/home/dev/.ssh/authorized_keys': No such file or directory");
                }
            }

            if (command.Arguments[1] == "cat")
            {
                return _state.FileExists
                    ? Success(_state.FileText)
                    : CommandResult(1, string.Empty, "No such file or directory");
            }

            if (command.Arguments[1] == "install" && command.Arguments.Contains("-d", StringComparer.Ordinal))
            {
                if (_state.FailDirectoryInstall)
                {
                    return CommandResult(1, string.Empty, "permission denied");
                }

                _state.DirectoryExists = true;
                _state.DirectoryUserId = 1000;
                _state.DirectoryGroupId = 1000;
                _state.DirectoryMode = 700;
                return Success(string.Empty);
            }

            if (command.Arguments[1] == "install")
            {
                if (_state.FailFileInstall)
                {
                    return CommandResult(1, string.Empty, "permission denied");
                }

                var source = command.Arguments[^2];
                var destination = command.Arguments[^1];
                if (!_state.UserStages.TryGetValue(source, out var payload))
                {
                    throw new InvalidOperationException("Expected SFTP staging payload was not uploaded.");
                }

                _state.PrivilegedStages[destination] = Encoding.UTF8.GetString(payload);
                return Success(string.Empty);
            }

            if (command.Arguments[1] == "mv")
            {
                var source = command.Arguments[^2];
                if (!_state.PrivilegedStages.Remove(source, out var text))
                {
                    throw new InvalidOperationException("Expected privileged staging payload was not installed.");
                }

                _state.FileExists = true;
                _state.FileUserId = 1000;
                _state.FileGroupId = 1000;
                _state.FileMode = 600;
                _state.FileText = text;
                return Success(string.Empty);
            }

            if (command.Arguments[1] == "rm")
            {
                _state.PrivilegedStages.Remove(command.Arguments[^1]);
                return Success(string.Empty);
            }

            throw new InvalidOperationException($"Unexpected command: {command.Executable} {string.Join(' ', command.Arguments)}");
        }
    }

    private sealed class FixtureFileSystemFactory : IRemoteFileSystemFactory
    {
        private readonly KeyFixtureState _state;

        public FixtureFileSystemFactory(KeyFixtureState state) => _state = state;

        public IRemoteFileSystem Create(ServerProfile profile) => new FixtureFileSystem(profile.Id, _state);
    }

    private sealed class FixtureFileSystem : IRemoteFileSystem
    {
        private readonly KeyFixtureState _state;

        public FixtureFileSystem(Guid serverProfileId, KeyFixtureState state)
        {
            ServerProfileId = serverProfileId;
            _state = state;
        }

        public Guid ServerProfileId { get; }
        public bool IsConnected { get; private set; }

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(RemotePath path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<RemoteFileEntry> StatAsync(RemotePath path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask CreateDirectoryAsync(RemotePath path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask RenameAsync(RemotePath source, RemotePath destination, bool overwrite = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteFileAsync(RemotePath path, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state.UserStages.Remove(path.Value);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteDirectoryAsync(RemotePath path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SetPermissionsAsync(RemotePath path, RemoteUnixPermissions permissions, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public async ValueTask UploadAsync(
            Stream source,
            RemotePath destination,
            long? totalBytes = null,
            bool overwrite = false,
            IProgress<RemoteTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            if (!overwrite && _state.UserStages.ContainsKey(destination.Value))
            {
                throw new RemoteFileSystemException(new RemoteError(RemoteErrorCode.PathConflict, "Stage exists."));
            }

            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer, cancellationToken);
            _state.UserStages[destination.Value] = buffer.ToArray();
        }

        public ValueTask DownloadAsync(RemotePath source, Stream destination, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private static RemoteExecutionResult Success(string output) => CommandResult(0, output, string.Empty);

    private static RemoteExecutionResult CommandResult(int exitCode, string output, string error) =>
        RemoteExecutionResult.Success(new RemoteCommandResult(
            exitCode,
            output,
            error,
            TimeSpan.FromMilliseconds(1)));
}
