using ServerDesk.Application.Firewall;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class GuardedFirewallMutationImpactTests
{
    [Fact]
    public async Task RemovingUnresolvedSshServiceRuleIsPresentedAsUnknownImpact()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Firewall", "example.invalid", 22, "dev");
        var rule = new FirewallRuleInfo(
            "ufw:1",
            FirewallAdapterKind.Ufw,
            null,
            FirewallRuleAction.Allow,
            FirewallRuleDirection.Inbound,
            string.Empty,
            "ssh",
            "Anywhere (v6)",
            "host",
            "ssh ALLOW IN Anywhere (v6)");
        var snapshot = ActiveUfw(rule);
        var inventory = new SequenceFirewallInventory(snapshot, snapshot);
        var executor = new RecordingExecutor(profile.Id);
        var raw = new FirewallMutationService(
            inventory,
            new RecordingFactory(executor),
            FirewallMutationOptions.Default);
        var service = new GuardedFirewallMutationService(raw, inventory);

        var result = await service.PreviewAsync(
            profile,
            new FirewallMutationRequest(
                FirewallMutationKind.RemoveRule,
                FirewallAdapterKind.Ufw,
                RuleId: rule.Id),
            cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var preview = Assert.IsType<FirewallMutationPreview>(result.Preview);
        Assert.Equal(FirewallSshImpactKind.Unknown, preview.SshImpact.Kind);
        Assert.Contains("cannot guarantee", preview.SshImpact.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(OperationRisk.Destructive, preview.Risk);
    }

    private static FirewallInventorySnapshot ActiveUfw(FirewallRuleInfo rule)
    {
        var ufw = new FirewallAdapterObservation(
            FirewallAdapterKind.Ufw,
            true,
            true,
            false,
            "ufw fixture",
            "ufw-active",
            [rule],
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
            [rule],
            [ufw, firewalld],
            "ufw-active");
    }

    private sealed class SequenceFirewallInventory : IFirewallManager
    {
        private readonly Queue<FirewallInventoryResult> _results;

        public SequenceFirewallInventory(params FirewallInventorySnapshot[] snapshots)
        {
            _results = new Queue<FirewallInventoryResult>(
                snapshots.Select(snapshot => new FirewallInventoryResult(snapshot, null)));
        }

        public Task<FirewallInventoryResult> InspectAsync(
            ServerProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingFactory : IRemoteCommandExecutorFactory
    {
        private readonly RecordingExecutor _executor;

        public RecordingFactory(RecordingExecutor executor) => _executor = executor;

        public IRemoteCommandExecutor Create(ServerProfile profile) => _executor;
    }

    private sealed class RecordingExecutor : IRemoteCommandExecutor
    {
        public RecordingExecutor(Guid serverProfileId) => ServerProfileId = serverProfileId;

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = command.Executable == "printenv"
                ? new RemoteCommandResult(
                    0,
                    "2001:db8::10 50000 2001:db8::20 22\n",
                    string.Empty,
                    TimeSpan.FromMilliseconds(1))
                : new RemoteCommandResult(0, string.Empty, string.Empty, TimeSpan.FromMilliseconds(1));
            return Task.FromResult(RemoteExecutionResult.Success(result));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
