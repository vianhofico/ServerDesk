using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Firewall;

public sealed class GuardedFirewallMutationService : IFirewallMutationService
{
    private readonly FirewallMutationService _inner;
    private readonly IFirewallManager _inventory;
    private readonly ConcurrentDictionary<Guid, PreviewState> _previewStates = new();

    public GuardedFirewallMutationService(
        FirewallMutationService inner,
        IFirewallManager inventory)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _inventory = inventory ?? throw new ArgumentNullException(nameof(inventory));
    }

    public async Task<FirewallMutationPreviewResult> PreviewAsync(
        ServerProfile profile,
        FirewallMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(request);

        var baseline = await _inventory.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!baseline.IsSuccess || baseline.Snapshot is null)
        {
            return new FirewallMutationPreviewResult(
                null,
                baseline.Error ?? new RemoteError(
                    RemoteErrorCode.CommandFailed,
                    "Firewall state could not be captured before Preview."));
        }

        var result = await _inner.PreviewAsync(profile, request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Preview is null)
        {
            return result;
        }

        var rawPreview = result.Preview;
        var presented = ApplyConservativeSshImpact(rawPreview) with { Fingerprint = string.Empty };
        var presentedFingerprint = PreviewFingerprint(presented);
        presented = presented with { Fingerprint = presentedFingerprint };
        _previewStates[rawPreview.PlanId] = new PreviewState(
            FirewallMutationStateFingerprint.Compute(baseline.Snapshot),
            rawPreview,
            presentedFingerprint);
        return new FirewallMutationPreviewResult(presented, null);
    }

    public async Task<FirewallMutationResult> ExecuteAsync(
        ServerProfile profile,
        FirewallMutationPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preview);

        var actualPresentedFingerprint = PreviewFingerprint(preview with { Fingerprint = string.Empty });
        if (!_previewStates.TryRemove(preview.PlanId, out var state) ||
            !FixedTimeEquals(preview.Fingerprint, actualPresentedFingerprint) ||
            !FixedTimeEquals(preview.Fingerprint, state.PresentedFingerprint))
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "Firewall Preview is missing, replayed or modified. Preview the live state again before executing.");
        }

        var before = await _inventory.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!before.IsSuccess || before.Snapshot is null)
        {
            return Failure(
                before.Error ?? new RemoteError(
                    RemoteErrorCode.CommandFailed,
                    "Firewall state could not be re-read before execution."));
        }

        if (!string.Equals(
                FirewallMutationStateFingerprint.Compute(before.Snapshot),
                state.StateFingerprint,
                StringComparison.Ordinal))
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "Firewall adapter state or normalized policy changed after Preview. Preview the live state again before mutation.");
        }

        var result = await _inner.ExecuteAsync(profile, state.RawPreview, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess || result.AmbiguousState)
        {
            return result;
        }

        FirewallInventoryResult verification;
        try
        {
            verification = await _inventory.InspectAsync(profile, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Ambiguous(
                "The firewall command failed deterministically, but post-failure state verification was cancelled. Refresh firewall state before any retry.");
        }

        if (!verification.IsSuccess || verification.Snapshot is null)
        {
            return Ambiguous(
                "The firewall command failed deterministically, but ServerDesk could not verify the resulting firewall state. Refresh before any retry.",
                verification.Error?.TechnicalDetails);
        }

        var verifiedState = FirewallMutationStateFingerprint.Compute(verification.Snapshot);
        if (!string.Equals(verifiedState, state.StateFingerprint, StringComparison.Ordinal))
        {
            return new FirewallMutationResult(
                false,
                true,
                "The firewall command reported failure, but live firewall state changed. Completion is ambiguous; refresh before any retry.",
                new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    "Deterministic command failure was followed by an unexpected firewall state change."),
                verification.Snapshot);
        }

        return result with { VerifiedSnapshot = verification.Snapshot };
    }

    private static FirewallMutationPreview ApplyConservativeSshImpact(FirewallMutationPreview preview)
    {
        if (preview.SshImpact.Kind != FirewallSshImpactKind.NoKnownRestriction ||
            !CanRestrictSsh(preview, out var candidate))
        {
            return preview;
        }

        var port = candidate.PortOrService?.Trim() ?? string.Empty;
        if (int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var numericPort) &&
            numericPort != preview.Ssh.ServerPort)
        {
            return preview;
        }

        const string message =
            "The common firewall inventory cannot prove that this rule/service is unrelated to the observed SSH endpoint. Treat the SSH impact as unknown. This analysis cannot guarantee that the current session or a future reconnect will remain available.";
        return preview with
        {
            SshImpact = new FirewallSshImpact(FirewallSshImpactKind.Unknown, message),
        };
    }

    private static bool CanRestrictSsh(
        FirewallMutationPreview preview,
        out FirewallRuleDraft candidate)
    {
        if (preview.Request.Kind == FirewallMutationKind.AddRule &&
            preview.Request.Rule is { Action: FirewallRuleAction.Deny or FirewallRuleAction.Reject } add)
        {
            candidate = add;
            return true;
        }

        if (preview.Request.Kind == FirewallMutationKind.RemoveRule &&
            preview.BoundRule is { Action: FirewallRuleAction.Allow or FirewallRuleAction.Limit } remove)
        {
            candidate = new FirewallRuleDraft(
                remove.Action,
                remove.Direction,
                remove.Protocol,
                remove.PortOrService,
                remove.Source,
                remove.Zone);
            return true;
        }

        candidate = null!;
        return false;
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
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static string RuleCanonical(FirewallRuleInfo rule) =>
        string.Join(
            "\u001f",
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
            : string.Join(
                "\u001f",
                rule.Action,
                rule.Direction,
                rule.Protocol,
                rule.PortOrService,
                rule.Source,
                rule.Zone);

    private static bool FixedTimeEquals(string left, string right)
    {
        if (left.Length != right.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
    }

    private static FirewallMutationResult Failure(RemoteError error) =>
        new(false, error.Code == RemoteErrorCode.AmbiguousState, error.Message, error);

    private static FirewallMutationResult Failure(RemoteErrorCode code, string message) =>
        Failure(new RemoteError(code, message));

    private static FirewallMutationResult Ambiguous(string message, string? technicalDetails = null) =>
        new(
            false,
            true,
            message,
            new RemoteError(RemoteErrorCode.AmbiguousState, message, technicalDetails));

    private sealed record PreviewState(
        string StateFingerprint,
        FirewallMutationPreview RawPreview,
        string PresentedFingerprint);
}

public static class FirewallMutationStateFingerprint
{
    public static string Compute(FirewallInventorySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var builder = new StringBuilder();
        builder.Append(snapshot.Status)
            .Append('|')
            .Append(snapshot.ActiveAdapter)
            .Append('|')
            .Append(snapshot.Detail)
            .Append('\n');

        foreach (var adapter in snapshot.Adapters.OrderBy(item => item.Adapter))
        {
            builder.Append("adapter|")
                .Append(adapter.Adapter).Append('|')
                .Append(adapter.CliAvailable).Append('|')
                .Append(adapter.IsActive).Append('|')
                .Append(adapter.PermissionDenied).Append('|')
                .Append(adapter.Version).Append('|')
                .Append(adapter.Detail)
                .Append('\n');

            foreach (var rule in adapter.Rules.OrderBy(item => item.Id, StringComparer.Ordinal))
            {
                AppendRule(builder, rule, "adapter-rule");
            }
        }

        foreach (var rule in snapshot.Rules.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            AppendRule(builder, rule, "rule");
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static void AppendRule(StringBuilder builder, FirewallRuleInfo rule, string prefix)
    {
        builder.Append(prefix).Append('|')
            .Append(rule.Id).Append('|')
            .Append(rule.Adapter).Append('|')
            .Append(rule.Zone).Append('|')
            .Append(rule.Action).Append('|')
            .Append(rule.Direction).Append('|')
            .Append(rule.Protocol).Append('|')
            .Append(rule.PortOrService).Append('|')
            .Append(rule.Source).Append('|')
            .Append(rule.Destination).Append('|')
            .Append(rule.Raw)
            .Append('\n');
    }
}
