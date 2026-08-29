using System.Globalization;
using ServerDesk.Application.Backups;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class BackupRestoreIntegrationTests
{
    private const string HashA = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string HashB = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";

    [Fact]
    public async Task VerifiedBackupAndExactRestoreCrossOpenSshWithoutTouchingRunnerFiles()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var state = new FixtureState();
        state.SetDirectory("/tmp/serverdesk-backups", 1000, 1000, 755);
        state.SetFile("/etc/serverdesk-fixture.conf", 12, 0, 0, 640, HashA);
        var factory = new FixtureCommandFactory(fixture.CommandFactory, state);
        var service = new BackupRestoreService(factory, BackupRestoreOptions.Default);

        var created = await service.CreateBackupAsync(
            fixture.Profile,
            new BackupCreateRequest("/etc/serverdesk-fixture.conf", "/tmp/serverdesk-backups"),
            cancellationToken);

        Assert.True(created.IsSuccess, created.Error?.Message);
        var manifest = Assert.IsType<BackupManifest>(created.Manifest);
        Assert.True(manifest.IsVerified);
        Assert.Contains(factory.Mutations, command => command.Arguments.Contains("install", StringComparer.Ordinal));

        state.SetFile("/etc/serverdesk-fixture.conf", 8, 0, 0, 600, HashB);
        var previewResult = await service.PreviewRestoreAsync(fixture.Profile, manifest, cancellationToken);
        Assert.True(previewResult.IsSuccess, previewResult.Error?.Message);
        var preview = Assert.IsType<RestorePreview>(previewResult.Preview);
        Assert.Equal("/etc/serverdesk-fixture.conf", preview.Impact.ExactOverwriteTarget.Value);
        Assert.False(preview.Impact.RollbackAvailable);

        var result = await service.ExecuteRestoreAsync(fixture.Profile, preview, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var target = state.Get("/etc/serverdesk-fixture.conf");
        Assert.Equal(HashA, target.Hash);
        Assert.Equal((short)640, target.Mode);
        Assert.Contains(factory.Mutations, command => command.Arguments.Contains("mv", StringComparer.Ordinal));
        Assert.All(factory.Mutations, command => Assert.Equal("sudo", command.Executable));
    }

    private static Fixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Backup restore fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var secretStore = new MemorySecretStore(reference, Password);
        var options = new SshSessionOptions(
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));
        return new Fixture(
            profile,
            new SshRemoteCommandExecutorFactory(
                secretStore,
                new TrustOnceHostTrustService(),
                new RejectInteractivePrompt(),
                options));
    }

    private static RemoteExecutionResult Success(string output = "", int exitCode = 0, string error = "") =>
        RemoteExecutionResult.Success(new RemoteCommandResult(exitCode, output, error, TimeSpan.FromMilliseconds(1)));

    private sealed record Fixture(ServerProfile Profile, IRemoteCommandExecutorFactory CommandFactory);

    private sealed class FixtureCommandFactory : IRemoteCommandExecutorFactory
    {
        private readonly IRemoteCommandExecutorFactory _inner;
        private readonly FixtureState _state;

        public FixtureCommandFactory(IRemoteCommandExecutorFactory inner, FixtureState state)
        {
            _inner = inner;
            _state = state;
        }

        public List<RemoteCommandSpec> Mutations { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile) =>
            new FixtureExecutor(_inner.Create(profile), _state, Mutations);
    }

    private sealed class FixtureExecutor : IRemoteCommandExecutor
    {
        private readonly IRemoteCommandExecutor _inner;
        private readonly FixtureState _state;
        private readonly List<RemoteCommandSpec> _mutations;

        public FixtureExecutor(IRemoteCommandExecutor inner, FixtureState state, List<RemoteCommandSpec> mutations)
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
            if (command.Executable != "sudo" || command.Arguments.Count < 2 || command.Arguments[0] != "-n")
            {
                throw new InvalidOperationException($"Unexpected backup fixture command: {command.Executable} {string.Join(' ', command.Arguments)}");
            }

            var verb = command.Arguments[1];
            if (verb == "stat")
            {
                var path = command.Arguments[^1];
                return _state.TryGet(path, out var node)
                    ? Success($"{node.Kind}\t{node.Size}\t{node.UserId}\t{node.GroupId}\t{node.Mode}")
                    : Success(string.Empty, 1, "No such file or directory");
            }

            if (verb == "sha256sum")
            {
                var path = command.Arguments[^1];
                return _state.TryGet(path, out var node) && node.Kind == "regular file"
                    ? Success($"{node.Hash}  {path}\n")
                    : Success(string.Empty, 1, "No such file or directory");
            }

            if (verb is "install" or "mv" or "rm")
            {
                _mutations.Add(command);
                var transport = await _inner.ExecuteAsync(
                    command with { Executable = "/bin/echo" },
                    cancellationToken).ConfigureAwait(false);
                if (transport.Error is not null || transport.Command?.ExitCode != 0)
                {
                    return transport;
                }

                if (verb == "install")
                {
                    var source = command.Arguments[^2];
                    var destination = command.Arguments[^1];
                    var sourceNode = _state.Get(source);
                    var mode = short.Parse(command.Arguments[IndexOf(command.Arguments, "-m") + 1], CultureInfo.InvariantCulture);
                    var uid = int.Parse(command.Arguments[IndexOf(command.Arguments, "-o") + 1], CultureInfo.InvariantCulture);
                    var gid = int.Parse(command.Arguments[IndexOf(command.Arguments, "-g") + 1], CultureInfo.InvariantCulture);
                    _state.SetFile(destination, sourceNode.Size, uid, gid, mode, sourceNode.Hash);
                }
                else if (verb == "mv")
                {
                    var source = command.Arguments[^2];
                    var destination = command.Arguments[^1];
                    _state.Set(destination, _state.Get(source));
                    _state.Remove(source);
                }
                else
                {
                    _state.Remove(command.Arguments[^1]);
                }

                return transport;
            }

            throw new InvalidOperationException($"Unexpected backup fixture verb: {verb}");
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        private static int IndexOf(IReadOnlyList<string> values, string value)
        {
            for (var index = 0; index < values.Count; index++)
            {
                if (string.Equals(values[index], value, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }
    }

    private sealed class FixtureState
    {
        private readonly Dictionary<string, Node> _nodes = new(StringComparer.Ordinal);

        public void SetDirectory(string path, int uid, int gid, short mode) =>
            _nodes[path] = new Node("directory", 0, uid, gid, mode, string.Empty);

        public void SetFile(string path, long size, int uid, int gid, short mode, string hash) =>
            _nodes[path] = new Node("regular file", size, uid, gid, mode, hash);

        public void Set(string path, Node node) => _nodes[path] = node;
        public Node Get(string path) => _nodes[path];
        public bool TryGet(string path, out Node node) => _nodes.TryGetValue(path, out node!);
        public void Remove(string path) => _nodes.Remove(path);
    }

    private sealed record Node(string Kind, long Size, int UserId, int GroupId, short Mode, string Hash);

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
            return ValueTask.FromResult(new HostTrustVerification(HostTrustOutcome.TrustedOnce, observation, []));
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
