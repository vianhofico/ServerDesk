using System.Globalization;
using System.Text;
using ServerDesk.Application.Firewall;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class FirewallInventoryIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task ReadOnlyFirewallDiscoveryCrossesOpenSshWithoutTouchingRunnerFirewall()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var token = Guid.NewGuid().ToString("N");
        var ufwPath = RemotePath.Parse($"{Home}/serverdesk-ufw-{token}");
        var firewalldPath = RemotePath.Parse($"{Home}/serverdesk-firewalld-{token}");
        await InstallFixtureAsync(fixture, "firewall-ufw-fixture.sh", ufwPath, cancellationToken);
        await InstallFixtureAsync(fixture, "firewall-firewalld-stopped-fixture.sh", firewalldPath, cancellationToken);

        try
        {
            var tracking = new TrackingRewriteFactory(
                fixture.CommandFactory,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ufw"] = ufwPath.Value,
                    ["firewall-cmd"] = firewalldPath.Value,
                });
            var service = new FirewallInventoryService(tracking, FirewallInventoryOptions.Default);

            var result = await service.InspectAsync(fixture.Profile, cancellationToken);

            Assert.True(result.IsSuccess, result.Error?.Message);
            Assert.Equal(FirewallRuntimeStatus.Available, result.Snapshot!.Status);
            Assert.Equal(FirewallAdapterKind.Ufw, result.Snapshot.ActiveAdapter);
            Assert.Equal(3, result.Snapshot.Rules.Count);
            Assert.Contains(result.Snapshot.Rules, rule =>
                rule.PortOrService == "22" &&
                rule.Protocol == "tcp" &&
                rule.Direction == FirewallRuleDirection.Inbound &&
                rule.Source == "10.20.0.0/16");
            Assert.Contains(result.Snapshot.Rules, rule =>
                rule.PortOrService == "53" &&
                rule.Protocol == "udp" &&
                rule.Direction == FirewallRuleDirection.Outbound &&
                rule.Action == FirewallRuleAction.Deny);

            Assert.Equal(4, tracking.Commands.Count);
            Assert.All(tracking.Commands, command =>
            {
                Assert.Equal(OperationRisk.ReadOnly, command.Risk);
                Assert.NotNull(command.Environment);
                Assert.Equal("C", command.Environment["LC_ALL"]);
            });
            Assert.Contains(tracking.Commands, command => command.Executable == "ufw" && command.Arguments.SequenceEqual(["--version"]));
            Assert.Contains(tracking.Commands, command => command.Executable == "ufw" && command.Arguments.SequenceEqual(["status", "numbered"]));
            Assert.Contains(tracking.Commands, command => command.Executable == "firewall-cmd" && command.Arguments.SequenceEqual(["--version"]));
            Assert.Contains(tracking.Commands, command => command.Executable == "firewall-cmd" && command.Arguments.SequenceEqual(["--state"]));
        }
        finally
        {
            await CleanupAsync(fixture, [ufwPath, firewalldPath], CancellationToken.None);
        }
    }

    private static async Task InstallFixtureAsync(
        FirewallFixture fixture,
        string fixtureName,
        RemotePath remotePath,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName),
            cancellationToken);
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        var payload = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(payload, writable: false);
        await fileSystem.UploadAsync(
            stream,
            remotePath,
            payload.Length,
            overwrite: false,
            cancellationToken: cancellationToken);
        await fileSystem.SetPermissionsAsync(remotePath, RemoteUnixPermissions.FromMode(700), cancellationToken);
    }

    private static async Task CleanupAsync(
        FirewallFixture fixture,
        IReadOnlyList<RemotePath> paths,
        CancellationToken cancellationToken)
    {
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        try
        {
            await fileSystem.ConnectAsync(cancellationToken);
            foreach (var path in paths)
            {
                try
                {
                    await fileSystem.DeleteFileAsync(path, cancellationToken);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static FirewallFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Firewall fixture",
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
        return new FirewallFixture(
            profile,
            new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options),
            new SftpRemoteFileSystemFactory(secretStore, trust, prompt, options));
    }

    private sealed record FirewallFixture(
        ServerProfile Profile,
        IRemoteCommandExecutorFactory CommandFactory,
        IRemoteFileSystemFactory FileSystemFactory);

    private sealed class TrackingRewriteFactory : IRemoteCommandExecutorFactory
    {
        private readonly IRemoteCommandExecutorFactory _inner;
        private readonly IReadOnlyDictionary<string, string> _rewrites;

        public TrackingRewriteFactory(
            IRemoteCommandExecutorFactory inner,
            IReadOnlyDictionary<string, string> rewrites)
        {
            _inner = inner;
            _rewrites = rewrites;
        }

        public List<RemoteCommandSpec> Commands { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile) =>
            new TrackingRewriteExecutor(_inner.Create(profile), _rewrites, Commands);
    }

    private sealed class TrackingRewriteExecutor : IRemoteCommandExecutor
    {
        private readonly IRemoteCommandExecutor _inner;
        private readonly IReadOnlyDictionary<string, string> _rewrites;
        private readonly ICollection<RemoteCommandSpec> _commands;

        public TrackingRewriteExecutor(
            IRemoteCommandExecutor inner,
            IReadOnlyDictionary<string, string> rewrites,
            ICollection<RemoteCommandSpec> commands)
        {
            _inner = inner;
            _rewrites = rewrites;
            _commands = commands;
        }

        public Guid ServerProfileId => _inner.ServerProfileId;

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            _commands.Add(command);
            return _inner.ExecuteAsync(
                _rewrites.TryGetValue(command.Executable, out var executable)
                    ? command with { Executable = executable }
                    : command,
                cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

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
