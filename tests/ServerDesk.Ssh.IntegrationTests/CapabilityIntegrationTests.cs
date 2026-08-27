using ServerDesk.Application.Capabilities;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class CapabilityIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";

    [Fact]
    public async Task CommandExecutorPreservesLiteralArgumentsAcrossRealSsh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        await using var executor = fixture.ExecutorFactory.Create(fixture.Profile);
        const string literal = "alpha;echo-this-is-data 'quoted' $HOME";

        var result = await executor.ExecuteAsync(
            RemoteCommandSpec.ReadOnly("printf", "%s", literal),
            cancellationToken);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Command);
        Assert.Equal(0, result.Command.ExitCode);
        Assert.Equal(literal, result.Command.StandardOutput);
    }

    [Fact]
    public async Task CapabilityScannerReturnsNormalizedCachedSnapshotAcrossRealSsh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        await using var service = new ServerCapabilityService(
            fixture.ExecutorFactory,
            new ServerCapabilityOptions(TimeSpan.FromMinutes(5)));

        var first = await service.GetAsync(fixture.Profile, cancellationToken: cancellationToken);
        var cached = await service.GetAsync(fixture.Profile, cancellationToken: cancellationToken);

        Assert.Equal(fixture.Profile.Id, first.ServerProfileId);
        Assert.False(string.IsNullOrWhiteSpace(first.Identity.OsId));
        Assert.NotEqual("unknown", first.Identity.Architecture);
        Assert.NotEqual("unknown", first.Identity.KernelVersion);
        Assert.Equal(Username, first.Identity.CurrentUser);
        Assert.NotNull(first.Identity.UserId);
        Assert.Equal(CapabilityStatus.Available, first.Git.Status);
        Assert.Equal(first.CapturedAtUtc, cached.CapturedAtUtc);
        Assert.Equal(first, cached);
    }

    private static CapabilityFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Capability fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var secretStore = new MemorySecretStore(reference, Password);
        var hostTrust = new TrustOnceHostTrustService();
        var interactivePrompt = new RejectInteractivePrompt();
        var options = new SshSessionOptions(
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));
        return new CapabilityFixture(
            profile,
            new SshRemoteCommandExecutorFactory(secretStore, hostTrust, interactivePrompt, options));
    }

    private sealed record CapabilityFixture(
        ServerProfile Profile,
        IRemoteCommandExecutorFactory ExecutorFactory);

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
            throw new InvalidOperationException("Password capability fixture must not request interactive authentication.");
    }
}
