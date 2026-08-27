using ServerDesk.Domain.Operations;

namespace ServerDesk.Domain.Audit;

public enum OperationOutcome
{
    Succeeded,
    Failed,
    Cancelled,
    Unknown,
}

public sealed record OperationAuditEntry(
    Guid Id,
    DateTimeOffset OccurredAtUtc,
    string Category,
    string Summary,
    string? Target,
    OperationRisk Risk,
    OperationOutcome Outcome)
{
    public static OperationAuditEntry Create(
        string category,
        string summary,
        OperationRisk risk,
        OperationOutcome outcome,
        string? target = null,
        DateTimeOffset? occurredAtUtc = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);
        ArgumentException.ThrowIfNullOrWhiteSpace(summary);

        return new OperationAuditEntry(
            Guid.NewGuid(),
            occurredAtUtc ?? DateTimeOffset.UtcNow,
            category.Trim(),
            summary.Trim(),
            string.IsNullOrWhiteSpace(target) ? null : target.Trim(),
            risk,
            outcome);
    }
}
