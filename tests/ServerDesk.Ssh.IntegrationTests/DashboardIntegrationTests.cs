using ServerDesk.Application.Dashboard;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class DashboardIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";

    [Fact]
    public async Task RealOpenSshFixtureProducesCoreDashboardSnapshot()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Dashboard fixture",
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
        var commandFactory = new SshRemoteCommandExecutorFactory(
            secretStore,
            new TrustOnceHostTrustService(),
            new RejectInteractivePrompt(),
            options);
        var dashboard = new ServerDashboardService(
            commandFactory,
            ServerDashboardOptions.Default with { SamplingInterval = TimeSpan.FromMilliseconds(200) });

        var snapshot = await dashboard.GetAsync(profile, cancellationToken);

        Assert.Equal(profile.Id, snapshot.ServerProfileId);
        Assert.Equal(DashboardSectionStatus.Available, snapshot.Cpu.Status);
        Assert.InRange(snapshot.Cpu.Value!.UtilizationPercent, 0d, 100d);
        Assert.True(snapshot.Cpu.Value.LogicalProcessors >= 1);
        Assert.Equal(DashboardSectionStatus.Available, snapshot.Load.Status);
        Assert.Equal(DashboardSectionStatus.Available, snapshot.Uptime.Status);
        Assert.True(snapshot.Uptime.Value!.Uptime > TimeSpan.Zero);
        Assert.Equal(DashboardSectionStatus.Available, snapshot.Memory.Status);
        Assert.True(snapshot.Memory.Value!.TotalBytes > 0);
        Assert.Equal(DashboardSectionStatus.Available, snapshot.Network.Status);
        Assert.NotEmpty(snapshot.Network.Value!.Interfaces);
        Assert.Equal(DashboardSectionStatus.Available, snapshot.FileSystems.Status);
        Assert.Contains(snapshot.FileSystems.Value!, row => row.MountPoint == "/");
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
            throw new InvalidOperationException("Password dashboard fixture must not request interactive authentication.");
    }
}
