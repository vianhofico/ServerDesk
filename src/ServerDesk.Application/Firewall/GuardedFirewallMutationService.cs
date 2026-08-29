using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Firewall;

public sealed class GuardedFirewallMutationService : IFirewallMutationService
{
    private readonly FirewallMutationService _inner;
    private readonly IFirewallManager _inventory;
    private readonly ConcurrentDictionary<Guid, string> _previewStates = new();

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

        _previewStates[result.Preview.PlanId] = FirewallMutationStateFingerprint.Compute(baseline.Snapshot);
        return result;
    }

    public async Task<FirewallMutationResult> ExecuteAsync(
        ServerProfile profile,
        FirewallMutationPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preview);

        if (!_previewStates.TryRemove(preview.PlanId, out var expectedState))
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "Firewall Preview is missing, already consumed or no longer valid. Preview again before executing.");
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
                expectedState,
                StringComparison.Ordinal))
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "Firewall adapter state or normalized policy changed after Preview. Preview the live state again before mutation.");
        }

        var result = await _inner.ExecuteAsync(profile, preview, cancellationToken).ConfigureAwait(false);
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
        if (!string.Equals(verifiedState, expectedState, StringComparison.Ordinal))
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
