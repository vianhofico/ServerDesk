using System.Globalization;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.UserAdministration;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class UserAdministrationIntegrationTests
{
    private const string ExistingKey = "ssh-ed25519 AQIDBAUGBwgJCgsMDQ4PEA== existing";
    private const string NewKey = "ssh-ed25519 ERITFBUWFxgZGhscHR4fIA== added";
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";

    [Fact]
    public async Task ConnectedUserLockCrossesOpenSshAsTypedEchoWithoutMutatingRunnerAccount()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var state = new UserState();
        var factory = new UserFixtureCommandFactory(fixture.CommandFactory, state);
        var service = new UserAdministrationService(factory, UserAdministrationOptions.Default);

        var previewResult = await service.PreviewAsync(
            fixture.Profile,
            new UserMutationRequest(UserMutationKind.Lock, Username),
            cancellationToken);

        Assert.True(previewResult.IsSuccess, previewResult.Error?.Message);
        var preview = Assert.IsType<UserMutationPreview>(previewResult.Preview);
        Assert.Equal(["-n", "usermod", "--lock", "--", Username], preview.Arguments);
        Assert.Equal(ConnectedUserImpactKind.PossibleRestriction, preview.ConnectedUserImpact.Kind);
        Assert.Contains("cannot guarantee", preview.ConnectedUserImpact.Message, StringComparison.OrdinalIgnoreCase);

        var result = await service.ExecuteAsync(fixture.Profile, preview, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(state.Locked);
        var mutation = Assert.Single(factory.Mutations);
        Assert.Equal("sudo", mutation.Executable);
        Assert.Equal(["-n", "usermod", "--lock", "--", Username], mutation.Arguments);
    }

    [Fact]
    public async Task AuthorizedKeyAddUsesRealSftpStageAndOpenSshTokensWithoutTouchingRunnerAuthorizedKeys()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var token = Guid.NewGuid().ToString("N");
        var user = new LocalUserInfo(
            Username,
            1000,
            1000,
            Username,
            [],
            $"/home/{Username}/serverdesk-key-fixture-{token}",
            "/bin/bash",
            UserLockState.Unlocked,
            false,
            false);
        var state = new KeyState();
        var commandFactory = new KeyFixtureCommandFactory(fixture.CommandFactory, state, user);
        var raw = new AuthorizedKeyAdministrationService(
            commandFactory,
            fixture.FileSystemFactory,
            AuthorizedKeyAdministrationOptions.Default);
        var service = new GuardedAuthorizedKeyAdministrationService(
            raw,
            commandFactory,
            AuthorizedKeyAdministrationOptions.Default);

        var previewResult = await service.PreviewAsync(
            fixture.Profile,
            user,
            new AuthorizedKeyMutationRequest(
                AuthorizedKeyMutationKind.Add,
                Username,
                PublicKeyLine: NewKey),
            cancellationToken);

        Assert.True(previewResult.IsSuccess, previewResult.Error?.Message);
        var preview = Assert.IsType<AuthorizedKeyMutationPreview>(previewResult.Preview);
        Assert.Contains("SHA256:", preview.Summary, StringComparison.Ordinal);

        var result = await service.ExecuteAsync(fixture.Profile, user, preview, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(700, state.DirectoryMode);
        Assert.Equal(600, state.FileMode);
        Assert.Contains(ExistingKey, state.FileText, StringComparison.Ordinal);
        Assert.Contains(NewKey, state.FileText, StringComparison.Ordinal);
        Assert.Equal(3, commandFactory.Mutations.Count);
        Assert.Contains(commandFactory.Mutations, command => command.Arguments.Contains("-d", StringComparer.Ordinal));
        Assert.Contains(commandFactory.Mutations, command => command.Arguments.Contains("mv", StringComparer.Ordinal));
    }

    private static Fixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "User admin fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var secretStore = new MemorySecretStore(reference, Password);
        var trust = new TrustOnceHostTrustService();
        var prompt = new RejectInteractivePrompt();
        var options = new SshSessionOptions(
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));
        return new Fixture(
            profile,
            new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options),
            new SftpRemoteFileSystemFactory(secretStore, trust, prompt, options));
    }

    private sealed record Fixture(
        ServerProfile Profile,
        IRemoteCommandExecutorFactory CommandFactory,
        IRemoteFileSystemFactory FileSystemFactory);

    private sealed class UserState
    {
        public bool Locked { get; set; }
    }

    private sealed class UserFixtureCommandFactory : IRemoteCommandExecutorFactory
    {
        private readonly IRemoteCommandExecutorFactory _inner;
        private readonly UserState _state;

        public UserFixtureCommandFactory(IRemoteCommandExecutorFactory inner, UserState state)
        {
            _inner = inner;
            _state = state;
        }

        public List<RemoteCommandSpec> Mutations { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile) =>
            new UserFixtureExecutor(_inner.Create(profile), _state, Mutations);
    }

    private sealed class UserFixtureExecutor : IRemoteCommandExecutor
    {
        private readonly IRemoteCommandExecutor _inner;
        private readonly UserState _state;
        private readonly List<RemoteCommandSpec> _mutations;

        public UserFixtureExecutor(
            IRemoteCommandExecutor inner,
            UserState state,
            List<RemoteCommandSpec> mutations)
        {
            _inner = inner;
            _state = state;
            _mutations = mutations;
        }

        public Guid ServerProfileId => _inner.ServerProfileId;

        public async Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            if (command.Executable == "getent" && command.Arguments.SequenceEqual(["passwd"]))
            {
                return Success($"{Username}:x:1000:1000:CI:/home/{Username}:/bin/bash\n");
            }

            if (command.Executable == "getent" && command.Arguments.SequenceEqual(["group"]))
            {
                return Success($"{Username}:x:1000:\ndocker:x:999:\nsudo:x:27:\n");
            }

            if (command.Executable == "sudo" && command.Arguments.SequenceEqual(["-n", "passwd", "-S", "-a"]))
            {
                return Success($"{Username} {(_state.Locked ? "L" : "P")} 2026-08-29 0 99999 7 -1\n");
            }

            if (command.Executable == "sudo" && command.Arguments.Count > 1 && command.Arguments[1] == "usermod")
            {
                _mutations.Add(command);
                var execution = await _inner.ExecuteAsync(
                    command with { Executable = "/bin/echo" },
                    cancellationToken).ConfigureAwait(false);
                if (execution.Error is null && execution.Command?.ExitCode == 0 &&
                    command.Arguments.Contains("--lock", StringComparer.Ordinal))
                {
                    _state.Locked = true;
                }

                return execution;
            }

            throw new InvalidOperationException($"Unexpected fixture command: {command.Executable} {string.Join(' ', command.Arguments)}");
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class KeyState
    {
        public uint DirectoryUserId { get; set; } = 1000;
        public uint DirectoryGroupId { get; set; } = 1000;
        public int DirectoryMode { get; set; } = 700;
        public uint FileUserId { get; set; } = 1000;
        public uint FileGroupId { get; set; } = 1000;
        public int FileMode { get; set; } = 600;
        public string FileText { get; set; } = ExistingKey + "\n";
        public string? PendingText { get; set; }
    }

    private sealed class KeyFixtureCommandFactory : IRemoteCommandExecutorFactory
    {
        private readonly IRemoteCommandExecutorFactory _inner;
        private readonly KeyState _state;
        private readonly LocalUserInfo _user;

        public KeyFixtureCommandFactory(
            IRemoteCommandExecutorFactory inner,
            KeyState state,
            LocalUserInfo user)
        {
            _inner = inner;
            _state = state;
            _user = user;
        }

        public List<RemoteCommandSpec> Mutations { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile) =>
            new KeyFixtureExecutor(_inner.Create(profile), _state, _user, Mutations);
    }

    private sealed class KeyFixtureExecutor : IRemoteCommandExecutor
    {
        private readonly IRemoteCommandExecutor _inner;
        private readonly KeyState _state;
        private readonly LocalUserInfo _user;
        private readonly List<RemoteCommandSpec> _mutations;

        public KeyFixtureExecutor(
            IRemoteCommandExecutor inner,
            KeyState state,
            LocalUserInfo user,
            List<RemoteCommandSpec> mutations)
        {
            _inner = inner;
            _state = state;
            _user = user;
            _mutations = mutations;
        }

        public Guid ServerProfileId => _inner.ServerProfileId;

        public async Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            if (command.Executable == "sudo" && command.Arguments.Count > 1 && command.Arguments[1] == "stat")
            {
                var path = command.Arguments[^1];
                if (path == _user.Home + "/.ssh")
                {
                    return Success($"{_state.DirectoryUserId}:{_state.DirectoryGroupId}:{_state.DirectoryMode}");
                }

                if (path == _user.Home + "/.ssh/authorized_keys")
                {
                    return Success($"{_state.FileUserId}:{_state.FileGroupId}:{_state.FileMode}");
                }
            }

            if (command.Executable == "sudo" && command.Arguments.Count > 1 && command.Arguments[1] == "cat")
            {
                return Success(_state.FileText);
            }

            if (command.Executable == "sudo" && command.Arguments.Count > 1 &&
                command.Arguments[1] is "install" or "mv")
            {
                _mutations.Add(command);
                var execution = await _inner.ExecuteAsync(
                    command with { Executable = "/bin/echo" },
                    cancellationToken).ConfigureAwait(false);
                if (execution.Error is not null || execution.Command?.ExitCode != 0)
                {
                    return execution;
                }

                if (command.Arguments[1] == "install" && command.Arguments.Contains("-d", StringComparer.Ordinal))
                {
                    _state.DirectoryUserId = _user.UserId;
                    _state.DirectoryGroupId = _user.PrimaryGroupId;
                    _state.DirectoryMode = 700;
                }
                else if (command.Arguments[1] == "install")
                {
                    var stage = command.Arguments[^2];
                    var readStage = await _inner.ExecuteAsync(
                        new RemoteCommandSpec("cat", ["--", stage], TimeSpan.FromSeconds(8), OperationRisk.ReadOnly),
                        cancellationToken).ConfigureAwait(false);
                    if (readStage.Error is not null || readStage.Command?.ExitCode != 0)
                    {
                        return readStage;
                    }

                    _state.PendingText = readStage.Command.StandardOutput;
                }
                else
                {
                    _state.FileUserId = _user.UserId;
                    _state.FileGroupId = _user.PrimaryGroupId;
                    _state.FileMode = 600;
                    _state.FileText = _state.PendingText ?? throw new InvalidOperationException("Staged key payload was not captured.");
                    _state.PendingText = null;
                }

                return execution;
            }

            if (command.Executable == "sudo" && command.Arguments.Count > 1 && command.Arguments[1] == "rm")
            {
                return await _inner.ExecuteAsync(
                    command with { Executable = "/bin/echo" },
                    cancellationToken).ConfigureAwait(false);
            }

            throw new InvalidOperationException($"Unexpected key fixture command: {command.Executable} {string.Join(' ', command.Arguments)}");
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static RemoteExecutionResult Success(string output) =>
        RemoteExecutionResult.Success(new RemoteCommandResult(
            0,
            output,
            string.Empty,
            TimeSpan.FromMilliseconds(1)));

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly SecretReference _reference;
        private readonly string _secret;

        public MemorySecretStore(SecretReference reference, string secret)
        {
            _reference = reference;
            _secret = secret;
        }

        public ValueTask SetAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(reference == _reference ? _secret : null);
        }

        public ValueTask DeleteAsync(SecretReference reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TrustOnceHostTrustService : IHostTrustService
    {
        public ValueTask<HostTrustVerification> VerifyAsync(
            HostKeyObservation observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new HostTrustVerification(
                HostTrustOutcome.TrustedOnce,
                observation,
                []));
        }
    }

    private sealed class RejectInteractivePrompt : IInteractiveAuthenticationPrompt
    {
        public ValueTask<IReadOnlyList<string>?> PromptAsync(
            InteractiveAuthenticationChallenge challenge,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Password fixture must not request keyboard-interactive authentication.");
    }
}
