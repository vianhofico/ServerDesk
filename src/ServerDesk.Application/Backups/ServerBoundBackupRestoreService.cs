using System.Collections.Concurrent;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Backups;

public sealed class ServerBoundBackupRestoreService : IBackupRestoreService
{
    private readonly IBackupRestoreService _inner;
    private readonly ConcurrentDictionary<Guid, Guid> _serverBindings = new();

    public ServerBoundBackupRestoreService(IBackupRestoreService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public async Task<BackupCreateResult> CreateBackupAsync(
        ServerProfile profile,
        BackupCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.CreateBackupAsync(profile, request, cancellationToken).ConfigureAwait(false);
        if (result.Manifest is { IsVerified: true } manifest)
        {
            _serverBindings[manifest.BackupId] = profile.Id;
        }

        return result;
    }

    public Task<RestorePreviewResult> PreviewRestoreAsync(
        ServerProfile profile,
        BackupManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!_serverBindings.TryGetValue(manifest.BackupId, out var serverProfileId) || serverProfileId != profile.Id)
        {
            return Task.FromResult(new RestorePreviewResult(
                null,
                new RemoteError(
                    RemoteErrorCode.PathConflict,
                    "Verified backup manifest is not bound to this server profile/session. Create and verify the backup on the selected server first.")));
        }

        return _inner.PreviewRestoreAsync(profile, manifest, cancellationToken);
    }

    public Task<RestoreResult> ExecuteRestoreAsync(
        ServerProfile profile,
        RestorePreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preview);
        if (!_serverBindings.TryGetValue(preview.Manifest.BackupId, out var serverProfileId) || serverProfileId != profile.Id)
        {
            return Task.FromResult(new RestoreResult(
                false,
                false,
                "Verified backup manifest is not bound to this server profile/session.",
                new RemoteError(RemoteErrorCode.PathConflict, "Cross-server restore binding mismatch.")));
        }

        return _inner.ExecuteRestoreAsync(profile, preview, cancellationToken);
    }
}
