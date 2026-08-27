using ServerDesk.Domain.Audit;

namespace ServerDesk.Application.Audit;

public interface IOperationAudit
{
    ValueTask AppendAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default);
}
