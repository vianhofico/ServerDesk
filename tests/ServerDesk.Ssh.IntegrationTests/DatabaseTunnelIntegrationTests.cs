using System.Globalization;
using System.Net;
using System.Net.Sockets;
using ServerDesk.Application.Databases;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class DatabaseTunnelIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly int ForwardTargetPort = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_FORWARD_HTTP_PORT") ?? "18080",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";

    [Fact]
    public async Task DatabaseTunnelUsesAutomaticLoopbackPortAndClosesAfterConnectivityTest()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var serverId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(serverId);
        var server = ServerProfile.Create(
            serverId,
            "Database tunnel fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var serverRepository = new MemoryProfileRepository(server);
        var secretStore = new MemorySecretStore(reference, Password);
        var options = new SshSessionOptions(
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));
        var sessionFactory = new SshPortForwardSessionFactory(
            secretStore,
            new TrustOnceHostTrustService(),
            new RejectInteractivePrompt(),
            options);
        var tunnelService = new DatabaseTunnelService(serverRepository, sessionFactory);
        var connectivity = new DatabaseTunnelConnectivityService(
            tunnelService,
            new DatabaseTunnelTestOptions(TimeSpan.FromSeconds(5)));
        var databaseProfile = DatabaseConnectionProfile.Create(
            Guid.NewGuid(),
            server.Id,
            "Fixture database endpoint",
            DatabaseEngineKind.PostgreSql,
            "127.0.0.1",
            ForwardTargetPort,
            "fixture",
            "fixture",
            DatabaseAuthenticationKind.None,
            null);

        var result = await connectivity.TestAsync(databaseProfile, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Endpoint);
        Assert.Equal(DatabaseTunnelService.LoopbackHost, result.Endpoint.LocalHost);
        Assert.InRange(result.Endpoint.LocalPort, 1, 65535);
        Assert.Equal(databaseProfile.RemoteHost, result.Endpoint.RemoteHost);
        Assert.Equal(databaseProfile.RemotePort, result.Endpoint.RemotePort);
        Assert.Contains("credentials are not tested", result.Message, StringComparison.OrdinalIgnoreCase);

        using var closedProbe = new TcpClient(AddressFamily.InterNetwork);
        var exception = await Record.ExceptionAsync(async () =>
            await closedProbe.ConnectAsync(
                IPAddress.Loopback,
                result.Endpoint.LocalPort,
                cancellationToken));
        Assert.NotNull(exception);
    }

    private sealed class MemoryProfileRepository : IProfileRepository
    {
        private readonly ServerProfile _profile;

        public MemoryProfileRepository(ServerProfile profile) => _profile = profile;

        public ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ServerProfile> profiles = [_profile];
            return ValueTask.FromResult(profiles);
        }

        public ValueTask<ServerProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<ServerProfile?>(id == _profile.Id ? _profile : null);
        }

        public ValueTask UpsertAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
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

        public ValueTask SetAsync(
            SecretReference reference,
            string secret,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(reference == _reference ? _secret : null);
        }

        public ValueTask DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default) =>
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
