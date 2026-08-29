using ServerDesk.Application.Profiles;
using ServerDesk.Domain.Audit;

namespace ServerDesk.Application.Audit;

public sealed class M5EnrichedOperationAudit : IOperationAudit
{
    private static readonly HashSet<string> EnrichedCategories = new(StringComparer.Ordinal)
    {
        "firewall-mutation",
        "user-administration",
        "authorized-key-administration",
        "package-administration",
        "backup-create",
        "backup-restore",
    };

    private readonly IOperationAudit _inner;
    private readonly IProfileRepository _profiles;

    public M5EnrichedOperationAudit(IOperationAudit inner, IProfileRepository profiles)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    public async ValueTask AppendAsync(
        OperationAuditEntry entry,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var persistedEntry = entry;
        if (EnrichedCategories.Contains(entry.Category))
        {
            try
            {
                persistedEntry = await EnrichAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Audit enrichment is best effort. Never turn metadata enrichment into a reason
                // for the caller to repeat a remote mutation.
                persistedEntry = entry;
            }
        }

        await _inner.AppendAsync(persistedEntry, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default) =>
        _inner.ListRecentAsync(limit, cancellationToken);

    private async ValueTask<OperationAuditEntry> EnrichAsync(
        OperationAuditEntry entry,
        CancellationToken cancellationToken)
    {
        if (OperationAuditMetadata.TryGetServerProfileId(entry.Target) is not null)
        {
            return EnsureVerification(entry);
        }

        if (string.IsNullOrWhiteSpace(entry.Target))
        {
            return entry;
        }

        var profiles = await _profiles.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var profile in profiles)
        {
            var endpointPrefix = $"{profile.Username}@{profile.Host}:{profile.Port}";
            if (!entry.Target.StartsWith(endpointPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            if (entry.Target.Length > endpointPrefix.Length && entry.Target[endpointPrefix.Length] != ' ')
            {
                continue;
            }

            var identity = entry.Target[endpointPrefix.Length..].Trim();
            if (identity.Length == 0)
            {
                identity = $"category:{entry.Category}";
            }

            return entry with
            {
                Target = OperationAuditMetadata.ForServer(profile, identity, entry.Outcome),
            };
        }

        return entry;
    }

    private static OperationAuditEntry EnsureVerification(OperationAuditEntry entry)
    {
        if (OperationAuditMetadata.TryGetVerification(entry.Target) is not null)
        {
            return entry;
        }

        var serverId = OperationAuditMetadata.TryGetServerProfileId(entry.Target);
        if (serverId is null || string.IsNullOrWhiteSpace(entry.Target))
        {
            return entry;
        }

        return entry with
        {
            Target = entry.Target.TrimEnd() + " verification:" + OperationAuditMetadata.VerificationFor(entry.Outcome),
        };
    }
}
