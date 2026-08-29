using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Firewall;

public enum FirewallMutationKind
{
    AddRule,
    RemoveRule,
    Enable,
    Disable,
}

public enum FirewallSshImpactKind
{
    NoKnownRestriction,
    PossibleRestriction,
    Unknown,
}

public sealed record FirewallRuleDraft(
    FirewallRuleAction Action,
    FirewallRuleDirection Direction,
    string Protocol,
    string PortOrService,
    string Source,
    string? Zone = null);

public sealed record FirewallMutationRequest(
    FirewallMutationKind Kind,
    FirewallAdapterKind Adapter,
    string? RuleId = null,
    FirewallRuleDraft? Rule = null);

public sealed record FirewallSshAccessContext(
    string? ClientSource,
    int ServerPort,
    bool IsFullyObserved);

public sealed record FirewallSshImpact(
    FirewallSshImpactKind Kind,
    string Message);

public sealed record FirewallMutationPreview(
    Guid PlanId,
    string Fingerprint,
    FirewallMutationRequest Request,
    FirewallRuntimeStatus BeforeStatus,
    string BeforeFingerprint,
    FirewallRuleInfo? BoundRule,
    FirewallSshAccessContext Ssh,
    FirewallSshImpact SshImpact,
    string Executable,
    IReadOnlyList<string> Arguments,
    OperationRisk Risk,
    string DisplayCommand);

public sealed record FirewallMutationPreviewResult(
    FirewallMutationPreview? Preview,
    RemoteError? Error)
{
    public bool IsSuccess => Preview is not null && Error is null;
}

public sealed record FirewallMutationResult(
    bool IsSuccess,
    bool AmbiguousState,
    string Message,
    RemoteError? Error = null,
    FirewallInventorySnapshot? VerifiedSnapshot = null);

public sealed record FirewallMutationOptions(
    TimeSpan CommandTimeout,
    string PrivilegeExecutable)
{
    public static FirewallMutationOptions Default { get; } =
        new(TimeSpan.FromSeconds(30), "sudo");

    public void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }

        if (string.IsNullOrWhiteSpace(PrivilegeExecutable))
        {
            throw new ArgumentException("Privilege executable is required.", nameof(PrivilegeExecutable));
        }
    }
}

public interface IFirewallMutationService
{
    Task<FirewallMutationPreviewResult> PreviewAsync(
        ServerProfile profile,
        FirewallMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<FirewallMutationResult> ExecuteAsync(
        ServerProfile profile,
        FirewallMutationPreview preview,
        CancellationToken cancellationToken = default);
}

public static partial class FirewallMutationPolicy
{
    private static readonly Regex SafeZoneOrService = SafeZoneOrServiceRegex();

    public static FirewallMutationRequest Normalize(FirewallMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Adapter is not (FirewallAdapterKind.Ufw or FirewallAdapterKind.Firewalld))
        {
            throw new ArgumentException("An explicit UFW or firewalld adapter is required.", nameof(request));
        }

        return request.Kind switch
        {
            FirewallMutationKind.AddRule => request with
            {
                RuleId = null,
                Rule = NormalizeRule(request.Adapter, request.Rule ?? throw new ArgumentException("An add operation requires a rule.")),
            },
            FirewallMutationKind.RemoveRule => request with
            {
                RuleId = NormalizeRuleId(request.RuleId),
                Rule = null,
            },
            FirewallMutationKind.Enable or FirewallMutationKind.Disable => request with
            {
                RuleId = null,
                Rule = null,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    public static FirewallRuleDraft NormalizeRule(FirewallAdapterKind adapter, FirewallRuleDraft rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        var protocol = (rule.Protocol ?? string.Empty).Trim().ToLowerInvariant();
        var target = (rule.PortOrService ?? string.Empty).Trim();
        var source = NormalizeSource(rule.Source);
        var zone = string.IsNullOrWhiteSpace(rule.Zone) ? null : rule.Zone.Trim();

        if (adapter == FirewallAdapterKind.Ufw)
        {
            if (rule.Action is not (FirewallRuleAction.Allow or FirewallRuleAction.Deny or FirewallRuleAction.Reject or FirewallRuleAction.Limit))
            {
                throw new ArgumentException("UFW add supports allow, deny, reject or limit actions.", nameof(rule));
            }

            if (rule.Direction is not (FirewallRuleDirection.Inbound or FirewallRuleDirection.Outbound))
            {
                throw new ArgumentException("UFW add requires an inbound or outbound direction.", nameof(rule));
            }

            ValidateNumericPort(target);
            ValidateProtocol(protocol);
            return rule with
            {
                Protocol = protocol,
                PortOrService = target,
                Source = source,
                Zone = null,
            };
        }

        if (rule.Action != FirewallRuleAction.Allow || rule.Direction != FirewallRuleDirection.Inbound)
        {
            throw new ArgumentException("firewalld common-rule add supports inbound allow rules only.", nameof(rule));
        }

        if (!string.Equals(source, "any", StringComparison.Ordinal))
        {
            throw new ArgumentException("firewalld common-rule add currently requires source 'any'; source-specific rich rules are not silently synthesized.", nameof(rule));
        }

        if (zone is null || !SafeZoneOrService.IsMatch(zone))
        {
            throw new ArgumentException("A safe explicit firewalld zone is required.", nameof(rule));
        }

        if (protocol.Length == 0)
        {
            if (!SafeZoneOrService.IsMatch(target))
            {
                throw new ArgumentException("Invalid firewalld service name.", nameof(rule));
            }
        }
        else
        {
            ValidateProtocol(protocol);
            ValidateNumericPort(target);
        }

        return rule with
        {
            Protocol = protocol,
            PortOrService = target,
            Source = source,
            Zone = zone,
        };
    }

    public static bool SameRule(FirewallRuleInfo actual, FirewallRuleDraft expected, FirewallAdapterKind adapter) =>
        actual.Adapter == adapter &&
        actual.Action == expected.Action &&
        actual.Direction == expected.Direction &&
        string.Equals(actual.Protocol ?? string.Empty, expected.Protocol, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(actual.PortOrService, expected.PortOrService, StringComparison.OrdinalIgnoreCase) &&
        SourceEquivalent(actual.Source, expected.Source) &&
        string.Equals(actual.Zone ?? string.Empty, expected.Zone ?? string.Empty, StringComparison.Ordinal);

    public static bool SameBoundRule(FirewallRuleInfo left, FirewallRuleInfo right) =>
        left.Adapter == right.Adapter &&
        left.Action == right.Action &&
        left.Direction == right.Direction &&
        string.Equals(left.Protocol, right.Protocol, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(left.PortOrService, right.PortOrService, StringComparison.OrdinalIgnoreCase) &&
        SourceEquivalent(left.Source, right.Source) &&
        string.Equals(left.Zone ?? string.Empty, right.Zone ?? string.Empty, StringComparison.Ordinal);

    private static string NormalizeRuleId(string? value)
    {
        var id = value?.Trim() ?? string.Empty;
        if (id.Length == 0 || id.Length > 256 || id.Any(char.IsControl))
        {
            throw new ArgumentException("A valid normalized firewall rule ID is required.", nameof(value));
        }

        return id;
    }

    private static string NormalizeSource(string? value)
    {
        var source = string.IsNullOrWhiteSpace(value) ? "any" : value.Trim();
        if (source.Equals("any", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("anywhere", StringComparison.OrdinalIgnoreCase))
        {
            return "any";
        }

        var slash = source.IndexOf('/');
        var addressText = slash < 0 ? source : source[..slash];
        if (!IPAddress.TryParse(addressText, out var address))
        {
            throw new ArgumentException("Source must be 'any', an IP address or CIDR.", nameof(value));
        }

        if (slash >= 0)
        {
            if (!int.TryParse(source[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
            {
                throw new ArgumentException("Invalid CIDR prefix.", nameof(value));
            }

            var maximum = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            if (prefix < 0 || prefix > maximum)
            {
                throw new ArgumentException("CIDR prefix is outside the address-family range.", nameof(value));
            }
        }

        return source;
    }

    private static void ValidateNumericPort(string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            throw new ArgumentException("Port must be an integer from 1 through 65535.", nameof(value));
        }
    }

    private static void ValidateProtocol(string value)
    {
        if (value is not ("tcp" or "udp"))
        {
            throw new ArgumentException("Protocol must be tcp or udp.", nameof(value));
        }
    }

    private static bool SourceEquivalent(string? left, string? right)
    {
        static string Normalize(string? value)
        {
            var item = value?.Trim() ?? string.Empty;
            return item.Equals("Anywhere", StringComparison.OrdinalIgnoreCase) ||
                   item.Equals("any", StringComparison.OrdinalIgnoreCase)
                ? "any"
                : item;
        }

        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex("^[A-Za-z0-9_.-]{1,128}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeZoneOrServiceRegex();
}

public sealed class FirewallMutationService : IFirewallMutationService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IFirewallManager _inventory;
    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly FirewallMutationOptions _options;
    private readonly ConcurrentDictionary<Guid, string> _capabilities = new();

    public FirewallMutationService(
        IFirewallManager inventory,
        IRemoteCommandExecutorFactory commandFactory,
        FirewallMutationOptions options)
    {
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<FirewallMutationPreviewResult> PreviewAsync(
        ServerProfile profile,
        FirewallMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        FirewallMutationRequest normalized;
        try
        {
            normalized = FirewallMutationPolicy.Normalize(request);
        }
        catch (ArgumentException exception)
        {
            return PreviewFailure(RemoteErrorCode.InvalidEndpoint, exception.Message);
        }

        var inspection = await _inventory.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!inspection.IsSuccess || inspection.Snapshot is null)
        {
            return new FirewallMutationPreviewResult(null, inspection.Error ?? new RemoteError(
                RemoteErrorCode.CommandFailed,
                "Firewall prestate could not be inspected."));
        }

        var before = inspection.Snapshot;
        var precondition = ResolvePrecondition(before, normalized);
        if (precondition.Error is not null)
        {
            return new FirewallMutationPreviewResult(null, precondition.Error);
        }

        var ssh = await InspectSshAccessAsync(profile, cancellationToken).ConfigureAwait(false);
        var command = BuildCommand(normalized, precondition.BoundRule);
        var impact = AnalyzeSshImpact(normalized, precondition.BoundRule, before, ssh);
        var beforeFingerprint = SnapshotFingerprint(before);
        var planId = Guid.NewGuid();
        var provisional = new FirewallMutationPreview(
            planId,
            string.Empty,
            normalized,
            before.Status,
            beforeFingerprint,
            precondition.BoundRule,
            ssh,
            impact,
            command.Executable,
            command.Arguments,
            command.Risk,
            Display(command.Executable, command.Arguments));
        var fingerprint = PreviewFingerprint(provisional);
        var preview = provisional with { Fingerprint = fingerprint };
        _capabilities[planId] = fingerprint;
        return new FirewallMutationPreviewResult(preview, null);
    }

    public async Task<FirewallMutationResult> ExecuteAsync(
        ServerProfile profile,
        FirewallMutationPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preview);

        var actualFingerprint = PreviewFingerprint(preview with { Fingerprint = string.Empty });
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(preview.Fingerprint),
                Encoding.UTF8.GetBytes(actualFingerprint)) ||
            !_capabilities.TryRemove(preview.PlanId, out var expectedFingerprint) ||
            !string.Equals(expectedFingerprint, preview.Fingerprint, StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict,
                "Firewall preview is stale, replayed or modified. Preview the live firewall again before executing.");
        }

        var reinspection = await _inventory.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!reinspection.IsSuccess || reinspection.Snapshot is null)
        {
            return Failure(reinspection.Error ?? new RemoteError(
                RemoteErrorCode.CommandFailed,
                "Firewall prestate could not be re-inspected before mutation."));
        }

        if (!string.Equals(SnapshotFingerprint(reinspection.Snapshot), preview.BeforeFingerprint, StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict,
                "Firewall state or normalized rules changed after Preview. Refresh and preview again before mutation.");
        }

        var currentSsh = await InspectSshAccessAsync(profile, cancellationToken).ConfigureAwait(false);
        if (currentSsh != preview.Ssh)
        {
            return Failure(RemoteErrorCode.PathConflict,
                "The observed SSH endpoint/source changed after Preview. Refresh before performing a lockout-sensitive mutation.");
        }

        var command = BuildCommand(preview.Request, preview.BoundRule);
        if (!string.Equals(command.Executable, preview.Executable, StringComparison.Ordinal) ||
            !command.Arguments.SequenceEqual(preview.Arguments, StringComparer.Ordinal) ||
            command.Risk != preview.Risk)
        {
            return Failure(RemoteErrorCode.PathConflict,
                "The previewed firewall command no longer matches the normalized request.");
        }

        await using var executor = _commandFactory.Create(profile);
        var mutationStarted = false;
        try
        {
            mutationStarted = true;
            var execution = await executor.ExecuteAsync(
                    new RemoteCommandSpec(
                        command.Executable,
                        command.Arguments,
                        _options.CommandTimeout,
                        command.Risk,
                        StableEnvironment),
                    cancellationToken)
                .ConfigureAwait(false);

            if (execution.Error is not null)
            {
                return IsAmbiguousTransport(execution.Error.Code)
                    ? Ambiguous(
                        "ServerDesk lost a reliable completion signal after the firewall mutation may have started. Do not retry until firewall state is refreshed and re-inspected.",
                        execution.Error.TechnicalDetails)
                    : Failure(execution.Error);
            }

            if (execution.Command!.ExitCode != 0)
            {
                var detail = FirstUseful(
                    execution.Command.StandardError,
                    execution.Command.StandardOutput,
                    "Firewall mutation command failed.");
                return Failure(ClassifyFailure(detail), detail);
            }

            return await VerifyAsync(profile, preview, reinspection.Snapshot, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (mutationStarted)
        {
            return Ambiguous(
                "Firewall execution was cancelled after mutation dispatch began. Completion is unknown; refresh firewall state before any retry.");
        }
    }

    private async Task<FirewallMutationResult> VerifyAsync(
        ServerProfile profile,
        FirewallMutationPreview preview,
        FirewallInventorySnapshot before,
        CancellationToken cancellationToken)
    {
        FirewallInventoryResult verification;
        try
        {
            verification = await _inventory.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Ambiguous(
                "The firewall command returned success, but post-verification was cancelled. Refresh before any retry.");
        }

        if (!verification.IsSuccess || verification.Snapshot is null)
        {
            return Ambiguous(
                "The firewall command returned success, but ServerDesk could not re-read firewall state for verification.",
                verification.Error?.TechnicalDetails);
        }

        var after = verification.Snapshot;
        var verified = preview.Request.Kind switch
        {
            FirewallMutationKind.AddRule => VerifyAdd(before, after, preview.Request),
            FirewallMutationKind.RemoveRule => VerifyRemove(before, after, preview.BoundRule),
            FirewallMutationKind.Enable => after.Status == FirewallRuntimeStatus.Available && after.ActiveAdapter == preview.Request.Adapter,
            FirewallMutationKind.Disable => AdapterIsInactive(after, preview.Request.Adapter),
            _ => false,
        };

        if (!verified)
        {
            return new FirewallMutationResult(
                false,
                true,
                "Firewall command returned success, but the resulting normalized state did not match the previewed mutation. Refresh before retrying.",
                new RemoteError(RemoteErrorCode.AmbiguousState,
                    "Post-mutation firewall verification did not match the expected normalized state."),
                after);
        }

        return new FirewallMutationResult(
            true,
            false,
            "Firewall mutation completed and the resulting state was verified.",
            null,
            after);
    }

    private static bool VerifyAdd(
        FirewallInventorySnapshot before,
        FirewallInventorySnapshot after,
        FirewallMutationRequest request)
    {
        var rule = request.Rule!;
        var beforeCount = before.Rules.Count(item => FirewallMutationPolicy.SameRule(item, rule, request.Adapter));
        var afterCount = after.Rules.Count(item => FirewallMutationPolicy.SameRule(item, rule, request.Adapter));
        return after.Status == FirewallRuntimeStatus.Available &&
            after.ActiveAdapter == request.Adapter &&
            afterCount == beforeCount + 1;
    }

    private static bool VerifyRemove(
        FirewallInventorySnapshot before,
        FirewallInventorySnapshot after,
        FirewallRuleInfo? boundRule)
    {
        if (boundRule is null)
        {
            return false;
        }

        var beforeCount = before.Rules.Count(item => FirewallMutationPolicy.SameBoundRule(item, boundRule));
        var afterCount = after.Rules.Count(item => FirewallMutationPolicy.SameBoundRule(item, boundRule));
        return afterCount == beforeCount - 1;
    }

    private static bool AdapterIsInactive(FirewallInventorySnapshot after, FirewallAdapterKind adapter) =>
        after.Adapters.Any(item => item.Adapter == adapter && item.CliAvailable && !item.IsActive && !item.PermissionDenied);

    private PreconditionResult ResolvePrecondition(
        FirewallInventorySnapshot snapshot,
        FirewallMutationRequest request)
    {
        if (snapshot.Status == FirewallRuntimeStatus.AdapterConflict)
        {
            return PreconditionResult.Fail(RemoteErrorCode.PathConflict,
                "Multiple firewall adapters are active. ServerDesk will not mutate policy while ownership is ambiguous.");
        }

        var adapter = snapshot.Adapters.FirstOrDefault(item => item.Adapter == request.Adapter);
        if (adapter is null || !adapter.CliAvailable)
        {
            return PreconditionResult.Fail(RemoteErrorCode.CapabilityUnavailable,
                $"{request.Adapter} is not available on the target host.");
        }

        if (adapter.PermissionDenied)
        {
            return PreconditionResult.Fail(RemoteErrorCode.PermissionDenied,
                $"{request.Adapter} state cannot be read safely with the current account.");
        }

        if (request.Kind == FirewallMutationKind.Enable)
        {
            if (adapter.IsActive)
            {
                return PreconditionResult.Fail(RemoteErrorCode.PathConflict,
                    $"{request.Adapter} is already active; no enable mutation is needed.");
            }

            if (snapshot.Adapters.Any(item => item.Adapter != request.Adapter && item.IsActive))
            {
                return PreconditionResult.Fail(RemoteErrorCode.PathConflict,
                    "Another supported firewall adapter is already active.");
            }

            return PreconditionResult.Success(null);
        }

        if (!adapter.IsActive || snapshot.Status != FirewallRuntimeStatus.Available || snapshot.ActiveAdapter != request.Adapter)
        {
            return PreconditionResult.Fail(RemoteErrorCode.PathConflict,
                $"{request.Adapter} must be the single active firewall before this mutation can be previewed.");
        }

        if (request.Kind == FirewallMutationKind.RemoveRule)
        {
            var bound = snapshot.Rules.FirstOrDefault(item =>
                item.Adapter == request.Adapter && string.Equals(item.Id, request.RuleId, StringComparison.Ordinal));
            return bound is null
                ? PreconditionResult.Fail(RemoteErrorCode.PathNotFound,
                    "The normalized firewall rule no longer exists. Refresh before removing it.")
                : PreconditionResult.Success(bound);
        }

        if (request.Kind == FirewallMutationKind.AddRule)
        {
            var duplicate = snapshot.Rules.Any(item =>
                FirewallMutationPolicy.SameRule(item, request.Rule!, request.Adapter));
            if (duplicate)
            {
                return PreconditionResult.Fail(RemoteErrorCode.PathConflict,
                    "An equivalent normalized firewall rule already exists; no duplicate add mutation will be sent.");
            }
        }

        return PreconditionResult.Success(null);
    }

    private CommandPlan BuildCommand(FirewallMutationRequest request, FirewallRuleInfo? boundRule) =>
        request.Adapter switch
        {
            FirewallAdapterKind.Ufw => BuildUfwCommand(request, boundRule),
            FirewallAdapterKind.Firewalld => BuildFirewalldCommand(request, boundRule),
            _ => throw new InvalidOperationException("Unsupported firewall adapter."),
        };

    private CommandPlan BuildUfwCommand(FirewallMutationRequest request, FirewallRuleInfo? boundRule)
    {
        var args = new List<string> { "-n", "ufw" };
        switch (request.Kind)
        {
            case FirewallMutationKind.AddRule:
                var rule = request.Rule!;
                args.Add(ActionVerb(rule.Action));
                args.Add(rule.Direction == FirewallRuleDirection.Inbound ? "in" : "out");
                if (!string.Equals(rule.Source, "any", StringComparison.Ordinal))
                {
                    args.Add("from");
                    args.Add(rule.Source);
                }

                args.Add("to");
                args.Add("any");
                args.Add("port");
                args.Add(rule.PortOrService);
                args.Add("proto");
                args.Add(rule.Protocol);
                return new CommandPlan(_options.PrivilegeExecutable, args, OperationRisk.Mutating);

            case FirewallMutationKind.RemoveRule:
                if (boundRule is null || !boundRule.Id.StartsWith("ufw:", StringComparison.Ordinal) ||
                    !int.TryParse(boundRule.Id[4..], NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 1)
                {
                    throw new InvalidOperationException("UFW normalized rule identity is invalid for deletion.");
                }

                args.Add("--force");
                args.Add("delete");
                args.Add(index.ToString(CultureInfo.InvariantCulture));
                return new CommandPlan(_options.PrivilegeExecutable, args, OperationRisk.Destructive);

            case FirewallMutationKind.Enable:
                args.Add("--force");
                args.Add("enable");
                return new CommandPlan(_options.PrivilegeExecutable, args, OperationRisk.Mutating);

            case FirewallMutationKind.Disable:
                args.Add("disable");
                return new CommandPlan(_options.PrivilegeExecutable, args, OperationRisk.Destructive);

            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private CommandPlan BuildFirewalldCommand(FirewallMutationRequest request, FirewallRuleInfo? boundRule)
    {
        if (request.Kind is FirewallMutationKind.Enable or FirewallMutationKind.Disable)
        {
            return new CommandPlan(
                _options.PrivilegeExecutable,
                ["-n", "systemctl", request.Kind == FirewallMutationKind.Enable ? "start" : "stop", "firewalld"],
                request.Kind == FirewallMutationKind.Enable ? OperationRisk.Mutating : OperationRisk.Destructive);
        }

        var rule = request.Kind == FirewallMutationKind.AddRule
            ? request.Rule!
            : BoundToDraft(boundRule ?? throw new InvalidOperationException("firewalld removal requires a bound rule."));
        var verb = request.Kind == FirewallMutationKind.AddRule ? "--add-" : "--remove-";
        var item = rule.Protocol.Length == 0
            ? verb + "service=" + rule.PortOrService
            : verb + "port=" + rule.PortOrService + "/" + rule.Protocol;
        return new CommandPlan(
            _options.PrivilegeExecutable,
            ["-n", "firewall-cmd", "--zone=" + rule.Zone, item],
            request.Kind == FirewallMutationKind.AddRule ? OperationRisk.Mutating : OperationRisk.Destructive);
    }

    private static FirewallRuleDraft BoundToDraft(FirewallRuleInfo rule) =>
        new(rule.Action, rule.Direction, rule.Protocol ?? string.Empty, rule.PortOrService, rule.Source, rule.Zone);

    private async Task<FirewallSshAccessContext> InspectSshAccessAsync(
        ServerProfile profile,
        CancellationToken cancellationToken)
    {
        await using var executor = _commandFactory.Create(profile);
        var result = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "printenv",
                    ["SSH_CONNECTION"],
                    TimeSpan.FromSeconds(5),
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Error is not null || result.Command is null || result.Command.ExitCode != 0)
        {
            return new FirewallSshAccessContext(null, profile.Port, false);
        }

        var parts = FirewallParser.Sanitize(result.Command.StandardOutput)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 4 &&
            IPAddress.TryParse(parts[0], out _) &&
            int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var serverPort) &&
            serverPort is >= 1 and <= 65535)
        {
            return new FirewallSshAccessContext(parts[0], serverPort, true);
        }

        return new FirewallSshAccessContext(null, profile.Port, false);
    }

    private static FirewallSshImpact AnalyzeSshImpact(
        FirewallMutationRequest request,
        FirewallRuleInfo? boundRule,
        FirewallInventorySnapshot before,
        FirewallSshAccessContext ssh)
    {
        const string noGuarantee = " This analysis cannot guarantee that the SSH session or a future reconnect will remain available.";
        if (request.Kind == FirewallMutationKind.Disable)
        {
            return new FirewallSshImpact(
                FirewallSshImpactKind.NoKnownRestriction,
                "Disabling the firewall does not appear to restrict the current SSH path, but it reduces host network protection." + noGuarantee);
        }

        if (request.Kind == FirewallMutationKind.Enable)
        {
            return new FirewallSshImpact(
                FirewallSshImpactKind.Unknown,
                $"Enabling {request.Adapter} may apply policy that is not observable while the adapter is inactive. Current SSH server port: {ssh.ServerPort}." + noGuarantee);
        }

        var candidate = request.Kind == FirewallMutationKind.AddRule
            ? request.Rule is null
                ? null
                : new FirewallRuleInfo(
                    "preview",
                    request.Adapter,
                    request.Rule.Zone,
                    request.Rule.Action,
                    request.Rule.Direction,
                    request.Rule.Protocol,
                    request.Rule.PortOrService,
                    request.Rule.Source,
                    "host",
                    string.Empty)
            : boundRule;
        if (candidate is null || !TargetsPort(candidate, ssh.ServerPort))
        {
            return new FirewallSshImpact(
                FirewallSshImpactKind.NoKnownRestriction,
                $"The previewed rule does not target the observed SSH server port {ssh.ServerPort}." + noGuarantee);
        }

        var sourceMatch = SourceMayMatch(candidate.Source, ssh.ClientSource);
        if (request.Kind == FirewallMutationKind.AddRule &&
            candidate.Action is FirewallRuleAction.Deny or FirewallRuleAction.Reject &&
            sourceMatch != false)
        {
            return new FirewallSshImpact(
                sourceMatch == true ? FirewallSshImpactKind.PossibleRestriction : FirewallSshImpactKind.Unknown,
                $"The new blocking rule targets SSH port {ssh.ServerPort} and may match the current SSH source." + noGuarantee);
        }

        if (request.Kind == FirewallMutationKind.RemoveRule &&
            candidate.Action is FirewallRuleAction.Allow or FirewallRuleAction.Limit &&
            sourceMatch != false)
        {
            return new FirewallSshImpact(
                sourceMatch == true ? FirewallSshImpactKind.PossibleRestriction : FirewallSshImpactKind.Unknown,
                $"Removing this permitting rule targets SSH port {ssh.ServerPort} and may remove access used by the current SSH source." + noGuarantee);
        }

        return new FirewallSshImpact(
            FirewallSshImpactKind.NoKnownRestriction,
            $"No direct restriction of the observed SSH server port {ssh.ServerPort} was identified from the normalized rule." + noGuarantee);
    }

    private static bool TargetsPort(FirewallRuleInfo rule, int port) =>
        rule.Direction is FirewallRuleDirection.Inbound or FirewallRuleDirection.Any &&
        string.Equals(rule.PortOrService, port.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);

    private static bool? SourceMayMatch(string? source, string? clientSource)
    {
        if (string.IsNullOrWhiteSpace(source) ||
            source.Equals("any", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("Anywhere", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("0.0.0.0/0", StringComparison.OrdinalIgnoreCase) ||
            source.Equals("::/0", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (clientSource is null || !IPAddress.TryParse(clientSource, out var client))
        {
            return null;
        }

        foreach (var item in source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(item, out var exact) && exact.Equals(client))
            {
                return true;
            }

            if (CidrContains(item, client))
            {
                return true;
            }
        }

        return false;
    }

    private static bool CidrContains(string candidate, IPAddress address)
    {
        var slash = candidate.IndexOf('/');
        if (slash <= 0 ||
            !IPAddress.TryParse(candidate[..slash], out var network) ||
            network.AddressFamily != address.AddressFamily ||
            !int.TryParse(candidate[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var prefix))
        {
            return false;
        }

        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        var maximum = networkBytes.Length * 8;
        if (prefix < 0 || prefix > maximum)
        {
            return false;
        }

        var fullBytes = prefix / 8;
        var remaining = prefix % 8;
        for (var index = 0; index < fullBytes; index++)
        {
            if (networkBytes[index] != addressBytes[index])
            {
                return false;
            }
        }

        if (remaining == 0)
        {
            return true;
        }

        var mask = (byte)(0xff << (8 - remaining));
        return (networkBytes[fullBytes] & mask) == (addressBytes[fullBytes] & mask);
    }

    private static string SnapshotFingerprint(FirewallInventorySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.Append(snapshot.Status).Append('|').Append(snapshot.ActiveAdapter).Append('\n');
        foreach (var rule in snapshot.Rules.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            builder.Append(RuleCanonical(rule)).Append('\n');
        }

        return Sha256(builder.ToString());
    }

    private static string PreviewFingerprint(FirewallMutationPreview preview)
    {
        var builder = new StringBuilder();
        builder.Append(preview.PlanId).Append('|')
            .Append(preview.Request.Kind).Append('|')
            .Append(preview.Request.Adapter).Append('|')
            .Append(preview.Request.RuleId).Append('|')
            .Append(DraftCanonical(preview.Request.Rule)).Append('|')
            .Append(preview.BeforeStatus).Append('|')
            .Append(preview.BeforeFingerprint).Append('|')
            .Append(preview.BoundRule is null ? string.Empty : RuleCanonical(preview.BoundRule)).Append('|')
            .Append(preview.Ssh.ClientSource).Append('|')
            .Append(preview.Ssh.ServerPort).Append('|')
            .Append(preview.Ssh.IsFullyObserved).Append('|')
            .Append(preview.SshImpact.Kind).Append('|')
            .Append(preview.SshImpact.Message).Append('|')
            .Append(preview.Executable).Append('|')
            .Append(string.Join("\u001f", preview.Arguments)).Append('|')
            .Append(preview.Risk).Append('|')
            .Append(preview.DisplayCommand);
        return Sha256(builder.ToString());
    }

    private static string RuleCanonical(FirewallRuleInfo rule) =>
        string.Join("\u001f",
            rule.Id,
            rule.Adapter,
            rule.Zone,
            rule.Action,
            rule.Direction,
            rule.Protocol,
            rule.PortOrService,
            rule.Source,
            rule.Destination,
            rule.Raw);

    private static string DraftCanonical(FirewallRuleDraft? rule) =>
        rule is null
            ? string.Empty
            : string.Join("\u001f",
                rule.Action,
                rule.Direction,
                rule.Protocol,
                rule.PortOrService,
                rule.Source,
                rule.Zone);

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string Display(string executable, IReadOnlyList<string> arguments) =>
        executable + " " + string.Join(" ", arguments.Select(TokenDisplay));

    private static string TokenDisplay(string value) =>
        value.All(character => char.IsLetterOrDigit(character) || "-._/:=@".Contains(character, StringComparison.Ordinal))
            ? value
            : "[token]";

    private static string ActionVerb(FirewallRuleAction action) =>
        action switch
        {
            FirewallRuleAction.Allow => "allow",
            FirewallRuleAction.Deny => "deny",
            FirewallRuleAction.Reject => "reject",
            FirewallRuleAction.Limit => "limit",
            _ => throw new InvalidOperationException("Unsupported UFW action."),
        };

    private static bool IsAmbiguousTransport(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or RemoteErrorCode.OperationCancelled or RemoteErrorCode.ConnectionFailed;

    private static RemoteErrorCode ClassifyFailure(string detail)
    {
        if (detail.Contains("password is required", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("a password is required", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("no tty present", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.SudoRequired;
        }

        if (detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not permitted", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PermissionDenied;
        }

        if (detail.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("unknown zone", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not enabled", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathConflict;
        }

        return RemoteErrorCode.CommandFailed;
    }

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return FirewallParser.Sanitize(first.Trim());
        }

        return !string.IsNullOrWhiteSpace(second)
            ? FirewallParser.Sanitize(second.Trim())
            : fallback;
    }

    private static FirewallMutationPreviewResult PreviewFailure(RemoteErrorCode code, string message) =>
        new(null, new RemoteError(code, message));

    private static FirewallMutationResult Failure(RemoteError error) =>
        new(false, error.Code == RemoteErrorCode.AmbiguousState, error.Message, error);

    private static FirewallMutationResult Failure(RemoteErrorCode code, string message) =>
        Failure(new RemoteError(code, message));

    private static FirewallMutationResult Ambiguous(string message, string? technicalDetails = null) =>
        new(false, true, message, new RemoteError(RemoteErrorCode.AmbiguousState, message, technicalDetails));

    private sealed record CommandPlan(
        string Executable,
        IReadOnlyList<string> Arguments,
        OperationRisk Risk);

    private sealed record PreconditionResult(
        FirewallRuleInfo? BoundRule,
        RemoteError? Error)
    {
        public static PreconditionResult Success(FirewallRuleInfo? rule) => new(rule, null);

        public static PreconditionResult Fail(RemoteErrorCode code, string message) =>
            new(null, new RemoteError(code, message));
    }
}

public sealed class AuditedFirewallMutationService : IFirewallMutationService
{
    private readonly IFirewallMutationService _inner;
    private readonly IOperationAudit _audit;

    public AuditedFirewallMutationService(IFirewallMutationService inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public Task<FirewallMutationPreviewResult> PreviewAsync(
        ServerProfile profile,
        FirewallMutationRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.PreviewAsync(profile, request, cancellationToken);

    public async Task<FirewallMutationResult> ExecuteAsync(
        ServerProfile profile,
        FirewallMutationPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preview);
        try
        {
            var result = await _inner.ExecuteAsync(profile, preview, cancellationToken).ConfigureAwait(false);
            var outcome = result.IsSuccess
                ? OperationOutcome.Succeeded
                : result.AmbiguousState
                    ? OperationOutcome.Unknown
                    : OperationOutcome.Failed;
            var persisted = await TryAuditAsync(profile, preview, outcome, cancellationToken).ConfigureAwait(false);
            return persisted
                ? result
                : result with
                {
                    Message = result.Message + " Audit persistence failed; do not repeat the firewall mutation solely to create an audit record.",
                };
        }
        catch (OperationCanceledException)
        {
            _ = await TryAuditAsync(profile, preview, OperationOutcome.Cancelled, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<bool> TryAuditAsync(
        ServerProfile profile,
        FirewallMutationPreview preview,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var identity = preview.BoundRule?.Id ?? preview.Request.RuleId ?? preview.Request.Rule?.PortOrService ?? "runtime";
            var target = $"{profile.Username}@{profile.Host}:{profile.Port} firewall:{preview.Request.Adapter}:{identity}";
            var entry = OperationAuditEntry.Create(
                "firewall-mutation",
                $"Firewall {preview.Request.Kind} requested for {preview.Request.Adapter} identity {identity}",
                preview.Risk,
                outcome,
                target);
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
