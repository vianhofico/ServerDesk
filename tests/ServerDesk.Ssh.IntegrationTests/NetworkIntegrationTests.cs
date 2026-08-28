using System.Globalization;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Networking;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class NetworkIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly int FixtureHttpPort = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_FORWARD_HTTP_PORT") ?? "18080",
        CultureInfo.InvariantCulture);

    [Fact]
    public async Task InspectFindsInterfacesCountersAndKnownListeningPort()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var service = new ServerNetworkService(fixture.CommandFactory, new ServerNetworkOptions(TimeSpan.FromMilliseconds(200)));

        var snapshot = await service.InspectAsync(fixture.Profile, cancellationToken);

        Assert.True(snapshot.IsSuccess, snapshot.Error?.Message);
        Assert.NotEmpty(snapshot.Interfaces);
        var loopback = Assert.Single(snapshot.Interfaces, item => item.Name == "lo");
        Assert.Contains(loopback.Addresses, address => address.Address == "127.0.0.1");
        Assert.True(loopback.RxBytes >= 0);
        Assert.True(loopback.TxBytes >= 0);

        var listener = Assert.Single(snapshot.ListeningSockets, item => item.Port == FixtureHttpPort && item.Protocol.StartsWith("tcp", StringComparison.Ordinal));
        Assert.Equal("127.0.0.1", listener.LocalAddress);
    }

    private static NetworkFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Network fixture",
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
        return new NetworkFixture(
            profile,
            new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options));
    }

    private sealed record NetworkFixture(
        ServerProfile Profile,
        SshRemoteCommandExecutorFactory CommandFactory);

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
