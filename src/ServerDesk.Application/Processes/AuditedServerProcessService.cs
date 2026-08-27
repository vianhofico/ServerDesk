using ServerDesk.Application.Audit;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Processes;

public sealed class AuditedServerProcessService : IServerProcessService
{
    private readonly IServerProcessService _inner;
    private readonly IOperationAudit _audit;

    public AuditedServerProcessService(IServerProcessService inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public Task<ServerProcessQueryResult> ListAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default) =>
        _inner.ListAsync(profile, cancellationToken);

    public Task<ServerProcessQueryResult> GetAsync(
        ServerProfile profile,
        int processId,
        CancellationToken cancellationToken = default) =>
        _inner.GetAsync(profile, processId, cancellationToken);

    public async Task<ServerProcessActionResult> SignalAsync(
        ServerProfile profile,
        int processId,
        ServerProcessSignal signal,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var risk = signal == ServerProcessSignal.ForceKill
            ? OperationRisk.Destructive
            : OperationRisk.Mutating;
        var signalName = signal == ServerProcessSignal.ForceKill ? "SIGKILL" : "SIGTERM";

        try
        {
            var result = await _inner.SignalAsync(profile, processId, signal, cancellationToken).ConfigureAwait(false);
            var outcome = result.IsSuccess
                ? OperationOutcome.Succeeded
                : result.Error?.Code == RemoteErrorCode.AmbiguousState
                    ? OperationOutcome.Unknown
                    : OperationOutcome.Failed;
            var auditPersisted = await TryAppendAuditAsync(
                    profile,
                    processId,
                    signalName,
                    risk,
                    outcome,
                    cancellationToken)
                .ConfigureAwait(false);
            return auditPersisted
                ? result
                : result with { Message = result.Message + " Audit record could not be persisted; do not retry the process signal solely for that reason." };
        }
        catch (OperationCanceledException)
        {
            _ = await TryAppendAuditAsync(
                    profile,
                    processId,
                    signalName,
                    risk,
                    OperationOutcome.Cancelled,
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<bool> TryAppendAuditAsync(
        ServerProfile profile,
        int processId,
        string signalName,
        OperationRisk risk,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = $"{profile.Username}@{profile.Host}:{profile.Port} PID {processId}";
            var entry = OperationAuditEntry.Create(
                "process-signal",
                $"{signalName} requested for PID {processId}",
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
