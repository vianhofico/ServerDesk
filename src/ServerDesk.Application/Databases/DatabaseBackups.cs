using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Databases;

public enum DatabaseBackupFormat
{
    PostgreSqlCustom,
    MySqlSql,
    MariaDbSql,
}

public sealed record DatabaseBackupRequest(
    Guid DatabaseProfileId,
    string DatabaseName,
    string DestinationDirectory);

public sealed record DatabaseBackupVerificationEvidence(
    long SizeBytes,
    string Sha256,
    string StructuralCheck,
    DateTimeOffset VerifiedAtUtc);

public sealed record DatabaseBackupManifest(
    Guid BackupId,
    Guid ServerProfileId,
    Guid DatabaseProfileId,
    DatabaseEngineKind Engine,
    string DatabaseName,
    string? Username,
    RemotePath BackupPath,
    DatabaseBackupFormat Format,
    string ToolName,
    string ToolVersion,
    DateTimeOffset CreatedAtUtc,
    DatabaseBackupVerificationEvidence Verification,
    bool IsVerified = true);

public sealed record DatabaseBackupCreateResult(
    DatabaseBackupManifest? Manifest,
    bool AmbiguousState,
    bool Unsupported,
    bool HistoryPersisted,
    string Message,
    RemoteError? Error = null)
{
    public bool IsSuccess => Manifest is { IsVerified: true } && !AmbiguousState && !Unsupported && Error is null;
}

public sealed record DatabaseBackupOptions(
    TimeSpan CommandTimeout,
    TimeSpan InspectionTimeout,
    int MaximumDiagnosticCharacters,
    long MaximumBackupBytes)
{
    public static DatabaseBackupOptions Default { get; } = new(
        TimeSpan.FromMinutes(5),
        TimeSpan.FromSeconds(20),
        64 * 1024,
        64L * 1024 * 1024 * 1024);

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

        if (MaximumBackupBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumBackupBytes));
        }
    }
}

public interface IDatabaseBackupManifestRepository
{
    ValueTask<IReadOnlyList<DatabaseBackupManifest>> ListForServerAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default);

    ValueTask<DatabaseBackupManifest?> GetAsync(
        Guid backupId,
        CancellationToken cancellationToken = default);

    ValueTask AddAsync(
        DatabaseBackupManifest manifest,
        CancellationToken cancellationToken = default);
}

public interface IDatabaseBackupService
{
    Task<DatabaseBackupCreateResult> CreateAsync(
        ServerProfile serverProfile,
        DatabaseBackupRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<DatabaseBackupManifest>> ListHistoryAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default);
}
