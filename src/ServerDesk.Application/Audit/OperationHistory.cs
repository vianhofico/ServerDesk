using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Audit;

public sealed record OperationAuditQuery(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    Guid? ServerProfileId = null,
    string? Category = null,
    OperationRisk? Risk = null,
    OperationOutcome? Outcome = null,
    string? SearchText = null,
    int Limit = 250);

public interface IOperationAuditReader
{
    ValueTask<IReadOnlyList<OperationAuditEntry>> QueryAsync(
        OperationAuditQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record OperationHistoryItem(
    OperationAuditEntry Entry,
    Guid? ServerProfileId,
    string? Verification,
    bool HasUnknownRemoteState);

public sealed record OperationHistoryResult(
    IReadOnlyList<OperationHistoryItem> Items,
    int AppliedLimit);

public interface IOperationHistoryService
{
    ValueTask<OperationHistoryResult> QueryAsync(
        OperationAuditQuery query,
        CancellationToken cancellationToken = default);
}

public sealed class OperationHistoryService : IOperationHistoryService
{
    public const int MaximumLimit = 500;
    private readonly IOperationAuditReader _reader;

    public OperationHistoryService(IOperationAuditReader reader)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public async ValueTask<OperationHistoryResult> QueryAsync(
        OperationAuditQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var normalized = Normalize(query);
        var entries = await _reader.QueryAsync(normalized, cancellationToken).ConfigureAwait(false);
        var items = entries
            .Select(entry => new OperationHistoryItem(
                entry,
                OperationAuditMetadata.TryGetServerProfileId(entry.Target),
                OperationAuditMetadata.TryGetVerification(entry.Target),
                entry.Outcome == OperationOutcome.Unknown))
            .ToArray();
        return new OperationHistoryResult(items, normalized.Limit);
    }

    internal static OperationAuditQuery Normalize(OperationAuditQuery query)
    {
        if (query.Limit is < 1 or > MaximumLimit)
        {
            throw new ArgumentOutOfRangeException(
                nameof(query),
                $"History limit must be between 1 and {MaximumLimit}.");
        }

        var from = query.FromUtc?.ToUniversalTime();
        var to = query.ToUtc?.ToUniversalTime();
        if (from is not null && to is not null && from > to)
        {
            throw new ArgumentException("History start time must be before or equal to the end time.", nameof(query));
        }

        var category = NormalizeText(query.Category, 100, nameof(query.Category));
        var search = NormalizeText(query.SearchText, 200, nameof(query.SearchText));
        if (query.ServerProfileId == Guid.Empty)
        {
            throw new ArgumentException("Server profile filter cannot be an empty GUID.", nameof(query));
        }

        return query with
        {
            FromUtc = from,
            ToUtc = to,
            Category = category,
            SearchText = search,
        };
    }

    private static string? NormalizeText(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(parameterName, $"Value may not exceed {maximumLength} characters.");
        }

        return normalized;
    }
}

public static class OperationAuditMetadata
{
    private const string ServerPrefix = "server:";
    private const string VerificationMarker = " verification:";

    public static string ForServer(
        ServerProfile profile,
        string targetIdentity,
        OperationOutcome outcome)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetIdentity);
        var identity = targetIdentity
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (identity.Length > 500)
        {
            throw new ArgumentOutOfRangeException(nameof(targetIdentity), "Audit target identity may not exceed 500 characters.");
        }

        return $"{ServerPrefix}{profile.Id:D} {identity}{VerificationMarker}{VerificationFor(outcome)}";
    }

    public static Guid? TryGetServerProfileId(string? target)
    {
        if (string.IsNullOrWhiteSpace(target) || !target.StartsWith(ServerPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var start = ServerPrefix.Length;
        var end = target.IndexOf(' ', start);
        var value = end < 0 ? target[start..] : target[start..end];
        return Guid.TryParseExact(value, "D", out var id) ? id : null;
    }

    public static string? TryGetVerification(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        var marker = target.LastIndexOf(VerificationMarker, StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var value = target[(marker + VerificationMarker.Length)..].Trim();
        return value.Length == 0 ? null : value;
    }

    public static string VerificationFor(OperationOutcome outcome) => outcome switch
    {
        OperationOutcome.Succeeded => "post-state-verified",
        OperationOutcome.Failed => "failed-known",
        OperationOutcome.Cancelled => "cancelled",
        OperationOutcome.Unknown => "ambiguous-unknown",
        _ => "unknown",
    };
}
