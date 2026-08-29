using System.Globalization;
using ServerDesk.Application.Firewall;
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

public sealed class FirewallMutationIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";

    [Fact]
    public async Task GuardedAddCrossesOpenSshWithoutChangingRunnerFirewall()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var before = ActiveUfw();
        var added = UfwRule("ufw:1", "443");
        var after = ActiveUfw(added);
        var inventory = new SequenceFirewallInventory(before, before, after);
        var service = new FirewallMutationService(
            inventory,
            fixture.CommandFactory,
            new FirewallMutationOptions(TimeSpan.FromSeconds(10), "/bin/echo"));

        var previewResult = await service.PreviewAsync(
            fixture.Profile,
            new FirewallMutationRequest(
                FirewallMutationKind.AddRule,
                FirewallAdapterKind.Ufw,
                Rule: new FirewallRuleDraft(
                    FirewallRuleAction.Allow,
                    FirewallRuleDirection.Inbound,
                    "tcp",
                    "443",
                    "any")),
            cancellationToken);

        Assert.True(previewResult.IsSuccess, previewResult.Error?.Message);
        var preview = Assert.IsType<FirewallMutationPreview>(previewResult.Preview);
        Assert.Equal("/bin/echo", preview.Executable);
        Assert.Equal(
            ["-n", "ufw", "allow", "in", "to", "any", "port", "443", "proto", "tcp"],
            preview.Arguments);
        Assert.Contains("cannot guarantee", preview.SshImpact.Message, StringComparison.OrdinalIgnoreCase);

        var result = await service.ExecuteAsync(fixture.Profile, preview, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.AmbiguousState);
        Assert.Same(after, result.VerifiedSnapshot);
        Assert.Equal(3, inventory.InspectCount);
    }

    private static FirewallFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Firewall mutation fixture",
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
            new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options));
    }

    private static FirewallInventorySnapshot ActiveUfw(params FirewallRuleInfo[] rules)
    {
        var ufw = new FirewallAdapterObservation(
            FirewallAdapterKind.Ufw,
            true,
            true,
            false,
            "ufw fixture",
            "ufw-active",
            rules,
            "fixture");
        var firewalld = new FirewallAdapterObservation(
            FirewallAdapterKind.Firewalld,
            false,
            false,
            false,
            null,
            "firewalld-cli-unavailable",
            [],
            string.Empty);
        return new FirewallInventorySnapshot(
            FirewallRuntimeStatus.Available,
            FirewallAdapterKind.Ufw,
            rules,
            [ufw, firewalld],
            "ufw-active");
    }

    private static FirewallRuleInfo UfwRule(string id, string port) =>
        new(
            id,
            FirewallAdapterKind.Ufw,
            null,
            FirewallRuleAction.Allow,
            FirewallRuleDirection.Inbound,
            "tcp",
            port,
            "Anywhere",
            "host",
            $"{port}/tcp ALLOW IN Anywhere");

    private sealed record FirewallFixture(
        ServerProfile Profile,
        IRemoteCommandExecutorFactory CommandFactory);

    private sealed class SequenceFirewallInventory : IFirewallManager
    {
        private readonly Queue<FirewallInventoryResult> _results;

        public SequenceFirewallInventory(params FirewallInventorySnapshot[] snapshots)
        {
            _results = new Queue<FirewallInventoryResult>(
                snapshots.Select(snapshot => new FirewallInventoryResult(snapshot, null)));
        }

        public int InspectCount { get; private set; }

        public Task<FirewallInventoryResult> InspectAsync(
            ServerProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InspectCount++;
            if (_results.Count == 0)
            {
                throw new InvalidOperationException("No firewall fixture result remains.");
            }

            return Task.FromResult(_results.Dequeue());
        }
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
