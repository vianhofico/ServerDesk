using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Application.UserAdministration;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class UserAdministrationTests
{
    [Fact]
    public void InventoryParserNormalizesUsersGroupsLockStateAndSudoVisibilityOutsideWpf()
    {
        var service = new UserAdministrationService(new ThrowingFactory(), UserAdministrationOptions.Default);
        var snapshot = service.ParseSnapshot(
            "root:x:0:0:root:/root:/bin/bash\ndev:x:1000:1000:Dev:/home/dev:/bin/bash\n",
            "root:x:0:\ndev:x:1000:\nsudo:x:27:dev\ndocker:x:999:dev\n",
            new Dictionary<string, UserLockState>(StringComparer.Ordinal)
            {
                ["root"] = UserLockState.Locked,
                ["dev"] = UserLockState.Unlocked,
            });

        var dev = Assert.Single(snapshot.Users, user => user.Username == "dev");
        Assert.Equal((uint)1000, dev.UserId);
        Assert.Equal("dev", dev.PrimaryGroup);
        Assert.Contains("sudo", dev.SupplementaryGroups);
        Assert.Contains("docker", dev.SupplementaryGroups);
        Assert.True(dev.HasSudoVisibility);
        Assert.Equal(UserLockState.Unlocked, dev.LockState);
        Assert.Contains(snapshot.Groups, group => group.Name == "sudo" && group.IsPrivilegeSensitive);
    }

    [Fact]
    public async Task RootMutationIsRejectedBeforeAnyRemoteCall()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var factory = new RecordingFactory(_ => throw new InvalidOperationException("Remote call must not occur."));
        var service = new UserAdministrationService(factory, UserAdministrationOptions.Default);
        var profile = Profile();

        var result = await service.PreviewAsync(
            profile,
            new UserMutationRequest(UserMutationKind.Unlock, "root"),
            cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.InvalidEndpoint, result.Error?.Code);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task PrivilegeGroupGrantIsIntentionallyUnavailable()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new UserFixtureState();
        var factory = FixtureFactory(state);
        var service = new UserAdministrationService(factory, UserAdministrationOptions.Default);

        var result = await service.PreviewAsync(
            Profile(),
            new UserMutationRequest(UserMutationKind.AddGroup, "dev", "sudo"),
            cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.CapabilityUnavailable, result.Error?.Code);
        Assert.DoesNotContain(factory.Commands, command => IsMutation(command));
    }

    [Fact]
    public async Task LockPreviewShowsConnectedUserImpactAndUsesTypedDestructiveCommand()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new UserFixtureState();
        var factory = FixtureFactory(state);
        var service = new UserAdministrationService(factory, UserAdministrationOptions.Default);

        var result = await service.PreviewAsync(
            Profile(),
            new UserMutationRequest(UserMutationKind.Lock, "dev"),
            cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var preview = Assert.IsType<UserMutationPreview>(result.Preview);
        Assert.Equal("sudo", preview.Executable);
        Assert.Equal(["-n", "usermod", "--lock", "--", "dev"], preview.Arguments);
        Assert.Equal(OperationRisk.Destructive, preview.Risk);
        Assert.Equal(ConnectedUserImpactKind.PossibleRestriction, preview.ConnectedUserImpact.Kind);
        Assert.Contains("cannot guarantee", preview.ConnectedUserImpact.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulLockRunsOnceAndVerifiesNormalizedPoststate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new UserFixtureState();
        var factory = FixtureFactory(state);
        var service = new UserAdministrationService(factory, UserAdministrationOptions.Default);
        var profile = Profile();
        var previewResult = await service.PreviewAsync(
            profile,
            new UserMutationRequest(UserMutationKind.Lock, "dev"),
            cancellationToken);
        var preview = Assert.IsType<UserMutationPreview>(previewResult.Preview);

        var result = await service.ExecuteAsync(profile, preview, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.AmbiguousState);
        Assert.Equal(UserLockState.Locked, Assert.Single(result.VerifiedSnapshot!.Users, user => user.Username == "dev").LockState);
        Assert.Single(factory.Commands, IsMutation);
    }

    [Fact]
    public async Task AmbiguousTransportIsNotRetried()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new UserFixtureState { MutationTransportError = true };
        var factory = FixtureFactory(state);
        var service = new UserAdministrationService(factory, UserAdministrationOptions.Default);
        var profile = Profile();
        var preview = Assert.IsType<UserMutationPreview>((await service.PreviewAsync(
            profile,
            new UserMutationRequest(UserMutationKind.Lock, "dev"),
            cancellationToken)).Preview);

        var result = await service.ExecuteAsync(profile, preview, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.AmbiguousState);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Single(factory.Commands, IsMutation);
    }

    [Fact]
    public async Task DeterministicFailureIsVerifiedAndRemainsDeterministicWhenStateIsUnchanged()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new UserFixtureState { MutationExitFailure = true };
        var factory = FixtureFactory(state);
        var service = new UserAdministrationService(factory, UserAdministrationOptions.Default);
        var profile = Profile();
        var preview = Assert.IsType<UserMutationPreview>((await service.PreviewAsync(
            profile,
            new UserMutationRequest(UserMutationKind.Lock, "dev"),
            cancellationToken)).Preview);

        var result = await service.ExecuteAsync(profile, preview, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.False(result.AmbiguousState);
        Assert.Equal(RemoteErrorCode.PermissionDenied, result.Error?.Code);
        Assert.NotNull(result.VerifiedSnapshot);
        Assert.Single(factory.Commands, IsMutation);
    }

    [Fact]
    public async Task PreviewCannotBeReplayed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var state = new UserFixtureState();
        var factory = FixtureFactory(state);
        var service = new UserAdministrationService(factory, UserAdministrationOptions.Default);
        var profile = Profile();
        var preview = Assert.IsType<UserMutationPreview>((await service.PreviewAsync(
            profile,
            new UserMutationRequest(UserMutationKind.Lock, "dev"),
            cancellationToken)).Preview);

        var first = await service.ExecuteAsync(profile, preview, cancellationToken);
        var replay = await service.ExecuteAsync(profile, preview, cancellationToken);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.False(replay.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, replay.Error?.Code);
        Assert.Single(factory.Commands, IsMutation);
    }

    private static ServerProfile Profile() =>
        ServerProfile.Create("Users", "example.invalid", 22, "dev");

    private static RecordingFactory FixtureFactory(UserFixtureState state) =>
        new(command =>
        {
            if (command.Executable == "getent" && command.Arguments.SequenceEqual(["passwd"]))
            {
                return Success(state.Passwd);
            }

            if (command.Executable == "getent" && command.Arguments.SequenceEqual(["group"]))
            {
                return Success(state.Group);
            }

            if (command.Executable == "sudo" && command.Arguments.SequenceEqual(["-n", "passwd", "-S", "-a"]))
            {
                return Success($"dev {(state.Locked ? "L" : "P")} 2026-08-29 0 99999 7 -1\n");
            }

            if (IsMutation(command))
            {
                if (state.MutationTransportError)
                {
                    return RemoteExecutionResult.Failure(new RemoteError(
                        RemoteErrorCode.NetworkInterrupted,
                        "SSH dropped after dispatch."));
                }

                if (state.MutationExitFailure)
                {
                    return CommandResult(1, string.Empty, "permission denied");
                }

                if (command.Arguments.Contains("--lock", StringComparer.Ordinal))
                {
                    state.Locked = true;
                }

                return Success(string.Empty);
            }

            throw new InvalidOperationException($"Unexpected command: {command.Executable} {string.Join(' ', command.Arguments)}");
        });

    private static bool IsMutation(RemoteCommandSpec command) =>
        command.Executable == "sudo" &&
        command.Arguments.Count > 1 &&
        command.Arguments[1] is "useradd" or "usermod" or "gpasswd";

    private static RemoteExecutionResult Success(string output) => CommandResult(0, output, string.Empty);

    private static RemoteExecutionResult CommandResult(int exitCode, string output, string error) =>
        RemoteExecutionResult.Success(new RemoteCommandResult(
            exitCode,
            output,
            error,
            TimeSpan.FromMilliseconds(1)));

    private sealed class UserFixtureState
    {
        public string Passwd { get; } = "root:x:0:0:root:/root:/bin/bash\ndev:x:1000:1000:Dev:/home/dev:/bin/bash\n";
        public string Group { get; } = "root:x:0:\ndev:x:1000:\nsudo:x:27:\ndocker:x:999:\n";
        public bool Locked { get; set; }
        public bool MutationTransportError { get; init; }
        public bool MutationExitFailure { get; init; }
    }

    private sealed class RecordingFactory : IRemoteCommandExecutorFactory
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public RecordingFactory(Func<RemoteCommandSpec, RemoteExecutionResult> handler) => _handler = handler;

        public int CreateCount { get; private set; }
        public List<RemoteCommandSpec> Commands { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile)
        {
            CreateCount++;
            return new RecordingExecutor(profile.Id, _handler, Commands);
        }
    }

    private sealed class RecordingExecutor : IRemoteCommandExecutor
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;
        private readonly List<RemoteCommandSpec> _commands;

        public RecordingExecutor(
            Guid serverProfileId,
            Func<RemoteCommandSpec, RemoteExecutionResult> handler,
            List<RemoteCommandSpec> commands)
        {
            ServerProfileId = serverProfileId;
            _handler = handler;
            _commands = commands;
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

    private sealed class ThrowingFactory : IRemoteCommandExecutorFactory
    {
        public IRemoteCommandExecutor Create(ServerProfile profile) => throw new NotSupportedException();
    }
}
