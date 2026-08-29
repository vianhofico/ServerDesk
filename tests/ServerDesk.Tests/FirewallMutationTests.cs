using ServerDesk.Application.Audit;
using ServerDesk.Application.Firewall;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class FirewallMutationTests
{
    [Fact]
    public async Task PreviewUfwBlockingSshRuleUsesExactTypedTokensAndWarnsPossibleRestriction()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var inventory = new SequenceFirewallInventory(ActiveUfw());
        var executor = Executor(profile, Success());
        var service = RawService(inventory, executor);

        var result = await service.PreviewAsync(
            profile,
            AddUfw(FirewallRuleAction.Deny, "22"),
            cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var preview = Assert.IsType<FirewallMutationPreview>(result.Preview);
        Assert.Equal("sudo", preview.Executable);
        Assert.Equal(
            ["-n", "ufw", "deny", "in", "to", "any", "port", "22", "proto", "tcp"],
            preview.Arguments);
        Assert.Equal(OperationRisk.Mutating, preview.Risk);
        Assert.Equal(FirewallSshImpactKind.PossibleRestriction, preview.SshImpact.Kind);
        Assert.Contains("cannot guarantee", preview.SshImpact.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("203.0.113.10", preview.Ssh.ClientSource);
        Assert.Equal(22, preview.Ssh.ServerPort);
    }

    [Fact]
    public async Task RemovePreviewBindsNormalizedRuleIdentityAndUsesDestructiveRisk()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var rule = UfwRule("ufw:3", FirewallRuleAction.Allow, "22", "Anywhere");
        var inventory = new SequenceFirewallInventory(ActiveUfw(rule));
        var executor = Executor(profile, Success());
        var service = RawService(inventory, executor);

        var result = await service.PreviewAsync(
            profile,
            new FirewallMutationRequest(
                FirewallMutationKind.RemoveRule,
                FirewallAdapterKind.Ufw,
                RuleId: rule.Id),
            cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var preview = Assert.IsType<FirewallMutationPreview>(result.Preview);
        Assert.Equal(rule, preview.BoundRule);
        Assert.Equal(["-n", "ufw", "--force", "delete", "3"], preview.Arguments);
        Assert.Equal(OperationRisk.Destructive, preview.Risk);
        Assert.Equal(FirewallSshImpactKind.PossibleRestriction, preview.SshImpact.Kind);
    }

    [Fact]
    public async Task EnableAndDisableHaveExplicitTypedCommandsAndRisk()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();

        var enableInventory = new SequenceFirewallInventory(DisabledUfw());
        var enableExecutor = Executor(profile, Success());
        var enableService = RawService(enableInventory, enableExecutor);
        var enable = await enableService.PreviewAsync(
            profile,
            new FirewallMutationRequest(FirewallMutationKind.Enable, FirewallAdapterKind.Ufw),
            cancellationToken);

        var enablePreview = Assert.IsType<FirewallMutationPreview>(enable.Preview);
        Assert.Equal(["-n", "ufw", "--force", "enable"], enablePreview.Arguments);
        Assert.Equal(OperationRisk.Mutating, enablePreview.Risk);
        Assert.Equal(FirewallSshImpactKind.Unknown, enablePreview.SshImpact.Kind);

        var disableInventory = new SequenceFirewallInventory(ActiveUfw());
        var disableExecutor = Executor(profile, Success());
        var disableService = RawService(disableInventory, disableExecutor);
        var disable = await disableService.PreviewAsync(
            profile,
            new FirewallMutationRequest(FirewallMutationKind.Disable, FirewallAdapterKind.Ufw),
            cancellationToken);

        var disablePreview = Assert.IsType<FirewallMutationPreview>(disable.Preview);
        Assert.Equal(["-n", "ufw", "disable"], disablePreview.Arguments);
        Assert.Equal(OperationRisk.Destructive, disablePreview.Risk);
        Assert.Contains("cannot guarantee", disablePreview.SshImpact.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SuccessfulMutationRunsOnceAndVerifiesExpectedPoststate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var before = ActiveUfw();
        var added = UfwRule("ufw:1", FirewallRuleAction.Allow, "443", "Anywhere");
        var after = ActiveUfw(added);
        var inventory = new SequenceFirewallInventory(before, before, after);
        var executor = Executor(profile, Success());
        var service = RawService(inventory, executor);

        var previewResult = await service.PreviewAsync(profile, AddUfw(FirewallRuleAction.Allow, "443"), cancellationToken);
        var preview = Assert.IsType<FirewallMutationPreview>(previewResult.Preview);
        var result = await service.ExecuteAsync(profile, preview, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.AmbiguousState);
        Assert.Same(after, result.VerifiedSnapshot);
        Assert.Equal(3, inventory.InspectCount);
        var mutation = Assert.Single(executor.Commands, command => command.Executable == "sudo");
        Assert.Equal(
            ["-n", "ufw", "allow", "in", "to", "any", "port", "443", "proto", "tcp"],
            mutation.Arguments);
    }

    [Fact]
    public async Task TamperedPreviewIsRejectedBeforeMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var inventory = new SequenceFirewallInventory(ActiveUfw());
        var executor = Executor(profile, Success());
        var service = RawService(inventory, executor);
        var previewResult = await service.PreviewAsync(profile, AddUfw(FirewallRuleAction.Allow, "443"), cancellationToken);
        var preview = Assert.IsType<FirewallMutationPreview>(previewResult.Preview);

        var result = await service.ExecuteAsync(
            profile,
            preview with { Arguments = [.. preview.Arguments, "--force"] },
            cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.DoesNotContain(executor.Commands, command => command.Executable == "sudo");
    }

    [Fact]
    public async Task PreviewCapabilityCannotBeReplayed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var before = ActiveUfw();
        var added = UfwRule("ufw:1", FirewallRuleAction.Allow, "443", "Anywhere");
        var inventory = new SequenceFirewallInventory(before, before, ActiveUfw(added));
        var executor = Executor(profile, Success());
        var service = RawService(inventory, executor);
        var previewResult = await service.PreviewAsync(profile, AddUfw(FirewallRuleAction.Allow, "443"), cancellationToken);
        var preview = Assert.IsType<FirewallMutationPreview>(previewResult.Preview);

        var first = await service.ExecuteAsync(profile, preview, cancellationToken);
        var replay = await service.ExecuteAsync(profile, preview, cancellationToken);

        Assert.True(first.IsSuccess, first.Error?.Message);
        Assert.False(replay.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, replay.Error?.Code);
        Assert.Single(executor.Commands, command => command.Executable == "sudo");
    }

    [Fact]
    public async Task StaleNormalizedPrestateBlocksMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var before = ActiveUfw();
        var changed = ActiveUfw(UfwRule("ufw:1", FirewallRuleAction.Allow, "80", "Anywhere"));
        var inventory = new SequenceFirewallInventory(before, changed);
        var executor = Executor(profile, Success());
        var service = RawService(inventory, executor);
        var previewResult = await service.PreviewAsync(profile, AddUfw(FirewallRuleAction.Allow, "443"), cancellationToken);
        var preview = Assert.IsType<FirewallMutationPreview>(previewResult.Preview);

        var result = await service.ExecuteAsync(profile, preview, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.DoesNotContain(executor.Commands, command => command.Executable == "sudo");
    }

    [Fact]
    public async Task GuardedServiceBlocksAdapterStateDriftEvenWhenNormalizedRulesAreSame()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var before = ActiveUfwWithDetail("ufw-active");
        var drifted = ActiveUfwWithDetail("ufw-active-reloaded");
        var inventory = new SequenceFirewallInventory(before, before, drifted);
        var executor = Executor(profile, Success());
        var guarded = GuardedService(inventory, executor);

        var previewResult = await guarded.PreviewAsync(profile, AddUfw(FirewallRuleAction.Allow, "443"), cancellationToken);
        var preview = Assert.IsType<FirewallMutationPreview>(previewResult.Preview);
        var result = await guarded.ExecuteAsync(profile, preview, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.DoesNotContain(executor.Commands, command => command.Executable == "sudo");
    }

    [Fact]
    public async Task AmbiguousTransportStopsWithoutRetryOrPostVerification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var before = ActiveUfw();
        var inventory = new SequenceFirewallInventory(before, before);
        var executor = new RecordingCommandExecutor(profile.Id, command =>
            command.Executable == "printenv"
                ? SshConnection()
                : RemoteExecutionResult.Failure(new RemoteError(
                    RemoteErrorCode.NetworkInterrupted,
                    "SSH channel dropped after dispatch.")));
        var service = RawService(inventory, executor);
        var previewResult = await service.PreviewAsync(profile, AddUfw(FirewallRuleAction.Allow, "443"), cancellationToken);
        var preview = Assert.IsType<FirewallMutationPreview>(previewResult.Preview);

        var result = await service.ExecuteAsync(profile, preview, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.AmbiguousState);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Single(executor.Commands, command => command.Executable == "sudo");
        Assert.Equal(2, inventory.InspectCount);
        Assert.Contains("refresh", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeterministicFailureIsFollowedByStateVerification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var before = ActiveUfw();
        var inventory = new SequenceFirewallInventory(before, before, before, before, before);
        var executor = Executor(profile, CommandFailure("permission denied"));
        var guarded = GuardedService(inventory, executor);

        var previewResult = await guarded.PreviewAsync(profile, AddUfw(FirewallRuleAction.Allow, "443"), cancellationToken);
        var preview = Assert.IsType<FirewallMutationPreview>(previewResult.Preview);
        var result = await guarded.ExecuteAsync(profile, preview, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.False(result.AmbiguousState);
        Assert.Equal(RemoteErrorCode.PermissionDenied, result.Error?.Code);
        Assert.NotNull(result.VerifiedSnapshot);
        Assert.Equal(5, inventory.InspectCount);
        Assert.Single(executor.Commands, command => command.Executable == "sudo");
    }

    [Fact]
    public async Task DeterministicFailureWithChangedStateIsUpgradedToAmbiguous()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var before = ActiveUfw();
        var changed = ActiveUfw(UfwRule("ufw:1", FirewallRuleAction.Allow, "443", "Anywhere"));
        var inventory = new SequenceFirewallInventory(before, before, before, before, changed);
        var executor = Executor(profile, CommandFailure("ufw returned failure"));
        var guarded = GuardedService(inventory, executor);

        var previewResult = await guarded.PreviewAsync(profile, AddUfw(FirewallRuleAction.Allow, "443"), cancellationToken);
        var preview = Assert.IsType<FirewallMutationPreview>(previewResult.Preview);
        var result = await guarded.ExecuteAsync(profile, preview, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.AmbiguousState);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Same(changed, result.VerifiedSnapshot);
    }

    [Fact]
    public async Task SuccessfulCommandWithUnexpectedPoststateReturnsAmbiguousState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var before = ActiveUfw();
        var inventory = new SequenceFirewallInventory(before, before, before);
        var executor = Executor(profile, Success());
        var service = RawService(inventory, executor);
        var previewResult = await service.PreviewAsync(profile, AddUfw(FirewallRuleAction.Allow, "443"), cancellationToken);
        var preview = Assert.IsType<FirewallMutationPreview>(previewResult.Preview);

        var result = await service.ExecuteAsync(profile, preview, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.AmbiguousState);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Equal(3, inventory.InspectCount);
    }

    [Fact]
    public async Task FirewalldAddUsesExplicitZoneAndTypedPortToken()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var inventory = new SequenceFirewallInventory(ActiveFirewalld());
        var executor = Executor(profile, Success());
        var service = RawService(inventory, executor);

        var result = await service.PreviewAsync(
            profile,
            new FirewallMutationRequest(
                FirewallMutationKind.AddRule,
                FirewallAdapterKind.Firewalld,
                Rule: new FirewallRuleDraft(
                    FirewallRuleAction.Allow,
                    FirewallRuleDirection.Inbound,
                    "tcp",
                    "443",
                    "any",
                    "public")),
            cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var preview = Assert.IsType<FirewallMutationPreview>(result.Preview);
        Assert.Equal(["-n", "firewall-cmd", "--zone=public", "--add-port=443/tcp"], preview.Arguments);
        Assert.Equal(OperationRisk.Mutating, preview.Risk);
    }

    [Theory]
    [InlineData("22;touch /tmp/pwn", "tcp", "any")]
    [InlineData("22", "tcp;id", "any")]
    [InlineData("22", "tcp", "any;id")]
    [InlineData("0", "tcp", "any")]
    [InlineData("65536", "tcp", "any")]
    public async Task HostileUfwRuleInputsAreRejectedBeforeRemoteInspection(
        string port,
        string protocol,
        string source)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var inventory = new SequenceFirewallInventory(ActiveUfw());
        var executor = Executor(profile, Success());
        var factory = new RecordingCommandExecutorFactory(executor);
        var service = new FirewallMutationService(inventory, factory, FirewallMutationOptions.Default);

        var result = await service.PreviewAsync(
            profile,
            new FirewallMutationRequest(
                FirewallMutationKind.AddRule,
                FirewallAdapterKind.Ufw,
                Rule: new FirewallRuleDraft(
                    FirewallRuleAction.Allow,
                    FirewallRuleDirection.Inbound,
                    protocol,
                    port,
                    source)),
            cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.InvalidEndpoint, result.Error?.Code);
        Assert.Equal(0, inventory.InspectCount);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task FirewalldSourceSpecificRuleIsRejectedInsteadOfSynthesizingRichRule()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var inventory = new SequenceFirewallInventory(ActiveFirewalld());
        var executor = Executor(profile, Success());
        var factory = new RecordingCommandExecutorFactory(executor);
        var service = new FirewallMutationService(inventory, factory, FirewallMutationOptions.Default);

        var result = await service.PreviewAsync(
            profile,
            new FirewallMutationRequest(
                FirewallMutationKind.AddRule,
                FirewallAdapterKind.Firewalld,
                Rule: new FirewallRuleDraft(
                    FirewallRuleAction.Allow,
                    FirewallRuleDirection.Inbound,
                    "tcp",
                    "443",
                    "203.0.113.10",
                    "public")),
            cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.InvalidEndpoint, result.Error?.Code);
        Assert.Equal(0, inventory.InspectCount);
        Assert.Equal(0, factory.CreateCount);
    }

    [Fact]
    public async Task AuditMarksAmbiguousDestructiveMutationUnknownWithoutSshSource()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = Profile();
        var preview = new FirewallMutationPreview(
            Guid.NewGuid(),
            "fingerprint",
            new FirewallMutationRequest(FirewallMutationKind.RemoveRule, FirewallAdapterKind.Ufw, "ufw:3"),
            FirewallRuntimeStatus.Available,
            "before",
            UfwRule("ufw:3", FirewallRuleAction.Allow, "22", "203.0.113.10"),
            new FirewallSshAccessContext("203.0.113.10", 22, true),
            new FirewallSshImpact(FirewallSshImpactKind.PossibleRestriction, "possible"),
            "sudo",
            ["-n", "ufw", "--force", "delete", "3"],
            OperationRisk.Destructive,
            "sudo -n ufw --force delete 3");
        var inner = new StubMutationService(new FirewallMutationResult(
            false,
            true,
            "Unknown.",
            new RemoteError(RemoteErrorCode.AmbiguousState, "Unknown.")));
        var audit = new RecordingAudit();
        var service = new AuditedFirewallMutationService(inner, audit);

        var result = await service.ExecuteAsync(profile, preview, cancellationToken);

        Assert.True(result.AmbiguousState);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("firewall-mutation", entry.Category);
        Assert.Equal(OperationRisk.Destructive, entry.Risk);
        Assert.Equal(OperationOutcome.Unknown, entry.Outcome);
        Assert.Contains("ufw:3", entry.Target!, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.10", entry.Target!, StringComparison.Ordinal);
        Assert.DoesNotContain("203.0.113.10", entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("password", entry.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static ServerProfile Profile() =>
        ServerProfile.Create("Firewall", "example.invalid", 22, "dev");

    private static FirewallMutationService RawService(
        IFirewallManager inventory,
        RecordingCommandExecutor executor) =>
        new(inventory, new RecordingCommandExecutorFactory(executor), FirewallMutationOptions.Default);

    private static GuardedFirewallMutationService GuardedService(
        IFirewallManager inventory,
        RecordingCommandExecutor executor)
    {
        var raw = RawService(inventory, executor);
        return new GuardedFirewallMutationService(raw, inventory);
    }

    private static FirewallMutationRequest AddUfw(FirewallRuleAction action, string port) =>
        new(
            FirewallMutationKind.AddRule,
            FirewallAdapterKind.Ufw,
            Rule: new FirewallRuleDraft(
                action,
                FirewallRuleDirection.Inbound,
                "tcp",
                port,
                "any"));

    private static FirewallInventorySnapshot ActiveUfw(params FirewallRuleInfo[] rules) =>
        ActiveUfwWithDetail("ufw-active", rules);

    private static FirewallInventorySnapshot ActiveUfwWithDetail(
        string detail,
        params FirewallRuleInfo[] rules)
    {
        var ufw = new FirewallAdapterObservation(
            FirewallAdapterKind.Ufw,
            true,
            true,
            false,
            "ufw 0.36.2",
            detail,
            rules,
            "Status: active");
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
            detail);
    }

    private static FirewallInventorySnapshot DisabledUfw()
    {
        var ufw = new FirewallAdapterObservation(
            FirewallAdapterKind.Ufw,
            true,
            false,
            false,
            "ufw 0.36.2",
            "ufw-inactive",
            [],
            "Status: inactive");
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
            FirewallRuntimeStatus.Disabled,
            FirewallAdapterKind.Ufw,
            [],
            [ufw, firewalld],
            "ufw-inactive");
    }

    private static FirewallInventorySnapshot ActiveFirewalld(params FirewallRuleInfo[] rules)
    {
        var ufw = new FirewallAdapterObservation(
            FirewallAdapterKind.Ufw,
            false,
            false,
            false,
            null,
            "ufw-cli-unavailable",
            [],
            string.Empty);
        var firewalld = new FirewallAdapterObservation(
            FirewallAdapterKind.Firewalld,
            true,
            true,
            false,
            "2.3.1",
            "firewalld-running",
            rules,
            "public (active)");
        return new FirewallInventorySnapshot(
            FirewallRuntimeStatus.Available,
            FirewallAdapterKind.Firewalld,
            rules,
            [ufw, firewalld],
            "firewalld-running");
    }

    private static FirewallRuleInfo UfwRule(
        string id,
        FirewallRuleAction action,
        string port,
        string source) =>
        new(
            id,
            FirewallAdapterKind.Ufw,
            null,
            action,
            FirewallRuleDirection.Inbound,
            "tcp",
            port,
            source,
            "host",
            $"{port}/tcp {action} IN {source}");

    private static RecordingCommandExecutor Executor(
        ServerProfile profile,
        RemoteExecutionResult mutationResult) =>
        new(
            profile.Id,
            command => command.Executable == "printenv" ? SshConnection() : mutationResult);

    private static RemoteExecutionResult SshConnection() =>
        RemoteExecutionResult.Success(new RemoteCommandResult(
            0,
            "203.0.113.10 50000 192.0.2.10 22\n",
            string.Empty,
            TimeSpan.FromMilliseconds(1)));

    private static RemoteExecutionResult Success() =>
        RemoteExecutionResult.Success(new RemoteCommandResult(
            0,
            string.Empty,
            string.Empty,
            TimeSpan.FromMilliseconds(1)));

    private static RemoteExecutionResult CommandFailure(string message) =>
        RemoteExecutionResult.Success(new RemoteCommandResult(
            1,
            string.Empty,
            message,
            TimeSpan.FromMilliseconds(1)));

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

    private sealed class RecordingCommandExecutorFactory : IRemoteCommandExecutorFactory
    {
        private readonly RecordingCommandExecutor _executor;

        public RecordingCommandExecutorFactory(RecordingCommandExecutor executor) => _executor = executor;

        public int CreateCount { get; private set; }

        public IRemoteCommandExecutor Create(ServerProfile profile)
        {
            CreateCount++;
            return _executor;
        }
    }

    private sealed class RecordingCommandExecutor : IRemoteCommandExecutor
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public RecordingCommandExecutor(Guid serverProfileId, Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            ServerProfileId = serverProfileId;
            _handler = handler;
        }

        public Guid ServerProfileId { get; }
        public List<RemoteCommandSpec> Commands { get; } = [];

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Commands.Add(command);
            return Task.FromResult(_handler(command));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubMutationService : IFirewallMutationService
    {
        private readonly FirewallMutationResult _result;

        public StubMutationService(FirewallMutationResult result) => _result = result;

        public Task<FirewallMutationPreviewResult> PreviewAsync(
            ServerProfile profile,
            FirewallMutationRequest request,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<FirewallMutationResult> ExecuteAsync(
            ServerProfile profile,
            FirewallMutationPreview preview,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_result);
        }
    }

    private sealed class RecordingAudit : IOperationAudit
    {
        public List<OperationAuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OperationAuditEntry>>(Entries.Take(limit).ToArray());
    }
}
