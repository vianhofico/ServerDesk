using ServerDesk.Application.Audit;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Services;

public sealed class AuditedServerServiceManager : IServerServiceManager
{
    private readonly IServerServiceManager _inner;
    private readonly IOperationAudit _audit;

    public AuditedServerServiceManager(IServerServiceManager inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public Task<ServerServiceQueryResult> ListAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default) =>
        _inner.ListAsync(profile, cancellationToken);

    public Task<ServerServiceQueryResult> GetAsync(
        ServerProfile profile,
        string unit,
        CancellationToken cancellationToken = default) =>
        _inner.GetAsync(profile, unit, cancellationToken);

    public async Task<ServerServiceActionResult> ExecuteAsync(
        ServerProfile profile,
        string unit,
        ServerServiceAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var risk = SystemdServiceManager.IsDisruptive(action)
            ? OperationRisk.Destructive
            : OperationRisk.Mutating;

        try
        {
            var result = await _inner.ExecuteAsync(profile, unit, action, cancellationToken).ConfigureAwait(false);
            var outcome = result.IsSuccess
                ? OperationOutcome.Succeeded
                : result.Error?.Code == RemoteErrorCode.AmbiguousState
                    ? OperationOutcome.Unknown
                    : OperationOutcome.Failed;
            var persisted = await TryAuditAsync(profile, unit, action, risk, outcome, cancellationToken)
                .ConfigureAwait(false);
            return persisted
                ? result
                : result with
                {
                    Message = result.Message + " Audit record could not be persisted; do not retry the service action solely for that reason.",
                };
        }
        catch (OperationCanceledException)
        {
            _ = await TryAuditAsync(
                    profile,
                    unit,
                    action,
                    risk,
                    OperationOutcome.Cancelled,
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<bool> TryAuditAsync(
        ServerProfile profile,
        string unit,
        ServerServiceAction action,
        OperationRisk risk,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = $"{profile.Username}@{profile.Host}:{profile.Port} {unit}";
            var entry = OperationAuditEntry.Create(
                "service-action",
                $"systemd {action.ToString().ToLowerInvariant()} requested for {unit}",
                risk,
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
