using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Backups;

public enum BackupTargetKind
{
    File,
}

public sealed record BackupCreateRequest(string TargetPath, string DestinationDirectory);

public sealed record BackupManifest(
    Guid BackupId,
    BackupTargetKind TargetKind,
    RemotePath TargetPath,
    RemotePath BackupPath,
    long Size,
    string Sha256,
    int UserId,
    int GroupId,
    RemoteUnixPermissions Permissions,
    DateTimeOffset CreatedAtUtc,
    bool IsVerified,
    DateTimeOffset? VerifiedAtUtc);

public sealed record BackupCreateResult(BackupManifest? Manifest, string Message, RemoteError? Error = null)
{
    public bool IsSuccess => Manifest is { IsVerified: true } && Error is null;
}

public sealed record RestoreImpact(RemotePath ExactOverwriteTarget, bool RollbackAvailable, string Message);

public sealed record RestorePreview(
    Guid PlanId,
    string Fingerprint,
    BackupManifest Manifest,
    string BeforeTargetFingerprint,
    RestoreImpact Impact,
    OperationRisk Risk,
    string Summary);

public sealed record RestorePreviewResult(RestorePreview? Preview, RemoteError? Error = null)
{
    public bool IsSuccess => Preview is not null && Error is null;
}

public sealed record RestoreResult(
    bool IsSuccess,
    bool AmbiguousState,
    string Message,
    RemoteError? Error = null,
    BackupManifest? VerifiedManifest = null);

public sealed record BackupRestoreOptions(
    TimeSpan CommandTimeout,
    int MaximumOutputCharacters,
    long MaximumFileBytes,
    string PrivilegeExecutable)
{
    public static BackupRestoreOptions Default { get; } = new(
        TimeSpan.FromMinutes(2),
        256 * 1024,
        4L * 1024 * 1024 * 1024,
        "sudo");

    public void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }

        if (MaximumOutputCharacters < 4096)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOutputCharacters));
        }

        if (MaximumFileBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileBytes));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(PrivilegeExecutable);
    }
}

public interface IBackupRestoreService
{
    Task<BackupCreateResult> CreateBackupAsync(
        ServerProfile profile,
        BackupCreateRequest request,
        CancellationToken cancellationToken = default);

    Task<RestorePreviewResult> PreviewRestoreAsync(
        ServerProfile profile,
        BackupManifest manifest,
        CancellationToken cancellationToken = default);

    Task<RestoreResult> ExecuteRestoreAsync(
        ServerProfile profile,
        RestorePreview preview,
        CancellationToken cancellationToken = default);
}
