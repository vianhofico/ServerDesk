namespace ServerDesk.Application.Profiles;

public enum BulkProfileMetadataOperation
{
    AddTag,
    RemoveTag,
    MarkFavorite,
    UnmarkFavorite,
}

public enum BulkProfileMetadataUpdateState
{
    Applying,
    Succeeded,
    Failed,
    Cancelled,
}

public sealed record BulkProfileMetadataTarget(
    Guid ServerProfileId,
    string Name,
    string Endpoint);

public sealed record BulkProfileMetadataRequest(
    IReadOnlyList<BulkProfileMetadataTarget> Targets,
    BulkProfileMetadataOperation Operation,
    string? Tag = null);

public sealed record BulkProfileMetadataUpdate(
    Guid ServerProfileId,
    BulkProfileMetadataUpdateState State,
    ServerProfileOrganization? Organization = null,
    string? FailureKind = null);

public interface IBulkProfileMetadataMutationService
{
    Task ExecuteAsync(
        BulkProfileMetadataRequest request,
        Func<BulkProfileMetadataUpdate, ValueTask> publishAsync,
        CancellationToken cancellationToken = default);
}

public sealed class BulkProfileMetadataMutationService : IBulkProfileMetadataMutationService
{
    private readonly IServerProfileOrganizationService _organizationService;

    public BulkProfileMetadataMutationService(IServerProfileOrganizationService organizationService)
    {
        _organizationService = organizationService ?? throw new ArgumentNullException(nameof(organizationService));
    }

    public async Task ExecuteAsync(
        BulkProfileMetadataRequest request,
        Func<BulkProfileMetadataUpdate, ValueTask> publishAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(publishAsync);
        var targets = ValidateTargets(request.Targets);
        var normalizedTag = ValidateOperation(request.Operation, request.Tag);

        for (var index = 0; index < targets.Length; index++)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await PublishCancelledAsync(targets, index, publishAsync).ConfigureAwait(false);
                return;
            }

            var target = targets[index];
            await publishAsync(new BulkProfileMetadataUpdate(
                target.ServerProfileId,
                BulkProfileMetadataUpdateState.Applying)).ConfigureAwait(false);

            try
            {
                var current = await _organizationService
                    .GetAsync(target.ServerProfileId, cancellationToken)
                    .ConfigureAwait(false);
                var desired = ApplyOperation(current, request.Operation, normalizedTag);
                var saved = await _organizationService
                    .SaveAsync(
                        target.ServerProfileId,
                        desired.GroupName,
                        string.Join(", ", desired.Tags),
                        desired.IsFavorite,
                        cancellationToken)
                    .ConfigureAwait(false);

                await publishAsync(new BulkProfileMetadataUpdate(
                    target.ServerProfileId,
                    BulkProfileMetadataUpdateState.Succeeded,
                    saved)).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await publishAsync(new BulkProfileMetadataUpdate(
                    target.ServerProfileId,
                    BulkProfileMetadataUpdateState.Cancelled)).ConfigureAwait(false);
                await PublishCancelledAsync(targets, index + 1, publishAsync).ConfigureAwait(false);
                return;
            }
            catch (Exception exception)
            {
                await publishAsync(new BulkProfileMetadataUpdate(
                    target.ServerProfileId,
                    BulkProfileMetadataUpdateState.Failed,
                    FailureKind: exception.GetType().Name)).ConfigureAwait(false);
            }
        }
    }

    private static BulkProfileMetadataTarget[] ValidateTargets(IReadOnlyList<BulkProfileMetadataTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        if (targets.Count == 0)
        {
            throw new ArgumentException("At least one target is required.", nameof(targets));
        }

        var result = targets.ToArray();
        foreach (var target in result)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (target.ServerProfileId == Guid.Empty)
            {
                throw new ArgumentException("Target server profile id cannot be empty.", nameof(targets));
            }

            ArgumentException.ThrowIfNullOrWhiteSpace(target.Name);
            ArgumentException.ThrowIfNullOrWhiteSpace(target.Endpoint);
        }

        if (result.Select(target => target.ServerProfileId).Distinct().Count() != result.Length)
        {
            throw new ArgumentException("Bulk metadata targets must have unique profile ids.", nameof(targets));
        }

        return result;
    }

    private static string? ValidateOperation(BulkProfileMetadataOperation operation, string? tag)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(nameof(operation));
        }

        if (operation is not BulkProfileMetadataOperation.AddTag and not BulkProfileMetadataOperation.RemoveTag)
        {
            return null;
        }

        var normalized = tag?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new ArgumentException("A tag is required for this operation.", nameof(tag));
        }

        if (normalized.Length > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(tag), "Tag must be 32 characters or fewer.");
        }

        if (normalized.Contains(',', StringComparison.Ordinal))
        {
            throw new ArgumentException("Tag cannot contain commas.", nameof(tag));
        }

        return normalized;
    }

    private static ServerProfileOrganization ApplyOperation(
        ServerProfileOrganization current,
        BulkProfileMetadataOperation operation,
        string? tag)
    {
        var tags = current.Tags.ToList();
        var isFavorite = current.IsFavorite;
        switch (operation)
        {
            case BulkProfileMetadataOperation.AddTag:
                if (!tags.Contains(tag!, StringComparer.OrdinalIgnoreCase))
                {
                    tags.Add(tag!);
                }

                break;
            case BulkProfileMetadataOperation.RemoveTag:
                tags.RemoveAll(value => string.Equals(value, tag, StringComparison.OrdinalIgnoreCase));
                break;
            case BulkProfileMetadataOperation.MarkFavorite:
                isFavorite = true;
                break;
            case BulkProfileMetadataOperation.UnmarkFavorite:
                isFavorite = false;
                break;
        }

        return ServerProfileOrganization.Create(
            current.ServerProfileId,
            current.GroupName,
            tags,
            isFavorite);
    }

    private static async Task PublishCancelledAsync(
        IReadOnlyList<BulkProfileMetadataTarget> targets,
        int startIndex,
        Func<BulkProfileMetadataUpdate, ValueTask> publishAsync)
    {
        for (var index = startIndex; index < targets.Count; index++)
        {
            await publishAsync(new BulkProfileMetadataUpdate(
                targets[index].ServerProfileId,
                BulkProfileMetadataUpdateState.Cancelled)).ConfigureAwait(false);
        }
    }
}
