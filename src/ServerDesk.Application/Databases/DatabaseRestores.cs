using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Databases;

public sealed record DatabaseRestoreRequest(
    Guid DatabaseProfileId,
    Guid BackupId,
    string TargetDatabase);

public sealed record DatabaseRestoreTargetSnapshot(
    string DatabaseName,
    string ConnectionIdentity,
    string ServerVersion,
    long UserObjectCount);

public sealed record DatabaseRestorePreview(
    Guid PlanId,
    string Fingerprint,
    Guid ServerProfileId,
    string ServerEndpoint,
    DatabaseRestoreRequest Request,
    DatabaseEngineKind Engine,
    DatabaseBackupFormat BackupFormat,
    string BackupPath,
    string BackupSha256,
    long BackupSizeBytes,
    string BackupTool,
    string BackupToolVersion,
    string RestoreTool,
    string RestoreToolVersion,
    string ManifestFingerprint,
    DatabaseRestoreTargetSnapshot TargetBefore,
    string TargetFingerprint,
    string Executable,
    IReadOnlyList<string> Arguments,
    bool UsesSensitiveInput,
    OperationRisk Risk,
    string DisplayCommand,
    string DataLossWarning,
    bool RollbackAvailable);

public sealed record DatabaseRestorePreviewResult(
    DatabaseRestorePreview? Preview,
    RemoteError? Error,
    bool Unsupported = false)
{
    public bool IsSuccess => Preview is not null && Error is null && !Unsupported;
}

public sealed record DatabaseRestoreResult(
    bool IsSuccess,
    bool AmbiguousState,
    bool RollbackAvailable,
    string Message,
    RemoteError? Error = null,
    DatabaseRestoreTargetSnapshot? VerifiedTarget = null);

public sealed record DatabaseRestoreOptions(
    TimeSpan CommandTimeout,
    TimeSpan InspectionTimeout,
    int MaximumDiagnosticCharacters)
{
    public static DatabaseRestoreOptions Default { get; } = new(
        TimeSpan.FromMinutes(5),
        TimeSpan.FromSeconds(20),
        64 * 1024);

    public void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }

        if (InspectionTimeout <= TimeSpan.Zero || InspectionTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(InspectionTimeout));
        }

        if (MaximumDiagnosticCharacters is < 4096 or > 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDiagnosticCharacters));
        }
    }
}

public interface IDatabaseRestoreService
{
    Task<DatabaseRestorePreviewResult> PreviewAsync(
        ServerProfile serverProfile,
        DatabaseRestoreRequest request,
        CancellationToken cancellationToken = default);

    Task<DatabaseRestoreResult> ExecuteAsync(
        ServerProfile serverProfile,
        DatabaseRestorePreview preview,
        CancellationToken cancellationToken = default);
}
