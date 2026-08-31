using System.Diagnostics;
using ServerDesk.Application.Audit;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Databases;

public sealed record DatabaseOperationAuditContext(
    Guid DatabaseProfileId,
    DatabaseEngineKind? Engine,
    string DatabaseName,
    Guid? BackupId,
    string Operation,
    string Verification);

public static class DatabaseOperationAuditMetadata
{
    private const string Marker = "dbmeta:v1";

    public static string ForOperation(
        ServerProfile serverProfile,
        Guid databaseProfileId,
        DatabaseEngineKind? engine,
        string databaseName,
        Guid? backupId,
        string operation,
        string verification)
    {
        ArgumentNullException.ThrowIfNull(serverProfile);
        if (databaseProfileId == Guid.Empty)
        {
            throw new ArgumentException("Database profile id cannot be empty.", nameof(databaseProfileId));
        }

        databaseName = NormalizeDatabaseName(databaseName);
        operation = NormalizeToken(operation, nameof(operation));
        verification = NormalizeToken(verification, nameof(verification));
        var engineToken = engine?.ToString() ?? "unknown";
        var backupToken = backupId?.ToString("D") ?? "none";
        return $"server:{serverProfile.Id:D} {Marker} database-profile:{databaseProfileId:D} engine:{engineToken} database:{Uri.EscapeDataString(databaseName)} backup-id:{backupToken} operation:{operation} verification:{verification}";
    }

    public static DatabaseOperationAuditContext? TryParse(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }

        var marker = target.IndexOf($" {Marker} ", StringComparison.Ordinal);
        if (marker < 0)
        {
            return null;
        }

        var tokens = target[(marker + Marker.Length + 2)..]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var token in tokens)
        {
            var separator = token.IndexOf(':');
            if (separator <= 0 || separator == token.Length - 1)
            {
                continue;
            }

            values[token[..separator]] = token[(separator + 1)..];
        }

        if (!values.TryGetValue("database-profile", out var profileValue) ||
            !Guid.TryParseExact(profileValue, "D", out var profileId) ||
            !values.TryGetValue("database", out var databaseValue) ||
            !values.TryGetValue("operation", out var operation) ||
            !values.TryGetValue("verification", out var verification))
        {
            return null;
        }

        DatabaseEngineKind? engine = null;
        if (values.TryGetValue("engine", out var engineValue) &&
            !string.Equals(engineValue, "unknown", StringComparison.OrdinalIgnoreCase) &&
            Enum.TryParse<DatabaseEngineKind>(engineValue, ignoreCase: true, out var parsedEngine) &&
            Enum.IsDefined(parsedEngine))
        {
            engine = parsedEngine;
        }

        Guid? backupId = null;
        if (values.TryGetValue("backup-id", out var backupValue) &&
            !string.Equals(backupValue, "none", StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParseExact(backupValue, "D", out var parsedBackupId))
        {
            backupId = parsedBackupId;
        }

        string databaseName;
        try
        {
            databaseName = Uri.UnescapeDataString(databaseValue);
        }
        catch (UriFormatException)
        {
            return null;
        }

        if (databaseName.Length is < 1 or > 128 || databaseName.Any(char.IsControl))
        {
            return null;
        }

        return new DatabaseOperationAuditContext(
            profileId,
            engine,
            databaseName,
            backupId,
            operation,
            verification);
    }

    private static string NormalizeDatabaseName(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 128 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Database audit identity must be 1-128 printable characters.", nameof(value));
        }

        return normalized;
    }

    private static string NormalizeToken(string value, string parameterName)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 64 ||
            normalized.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Audit metadata token contains unsupported characters.", parameterName);
        }

        return normalized;
    }
}

public sealed class HistoryDatabaseBackupService : IDatabaseBackupService
{
    private readonly IDatabaseBackupService _inner;
    private readonly IDatabaseProfileRepository _profiles;
    private readonly IOperationAudit _audit;

    public HistoryDatabaseBackupService(
        IDatabaseBackupService inner,
        IDatabaseProfileRepository profiles,
        IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public ValueTask<IReadOnlyList<DatabaseBackupManifest>> ListHistoryAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default) =>
        _inner.ListHistoryAsync(serverProfileId, cancellationToken);

    public async Task<DatabaseBackupCreateResult> CreateAsync(
        ServerProfile serverProfile,
        DatabaseBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverProfile);
        ArgumentNullException.ThrowIfNull(request);
        var profile = await _profiles.GetAsync(request.DatabaseProfileId, cancellationToken).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        var result = await _inner.CreateAsync(serverProfile, request, cancellationToken).ConfigureAwait(false);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var outcome = result.IsSuccess
            ? OperationOutcome.Succeeded
            : result.AmbiguousState ? OperationOutcome.Unknown : OperationOutcome.Failed;
        var verification = result.IsSuccess
            ? "backup-verified"
            : result.AmbiguousState ? "ambiguous-unknown" : result.Unsupported ? "unsupported" : "failed-known";
        var target = DatabaseOperationAuditMetadata.ForOperation(
            serverProfile,
            request.DatabaseProfileId,
            profile?.Engine,
            request.DatabaseName,
            result.Manifest?.BackupId,
            "backup",
            verification);
        var summary =
            $"Database backup outcome={outcome}; failure-class={FailureClass(result.Error)}; duration-ms={(long)elapsed.TotalMilliseconds}; artifact={(result.Manifest is null ? "unverified" : "verified-manifest")}.";
        var entry = OperationAuditEntry.Create(
            "database-backup",
            summary,
            OperationRisk.Mutating,
            outcome,
            target);

        try
        {
            await _audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch
        {
            return result with
            {
                Message = result.Message + " Database operation history persistence failed; do not repeat the backup solely to repair history.",
            };
        }
    }

    private static string FailureClass(RemoteError? error) => error?.Code.ToString() ?? "None";
}

public sealed class HistoryDatabaseRestoreService : IDatabaseRestoreService
{
    private readonly IDatabaseRestoreService _inner;
    private readonly IOperationAudit _audit;

    public HistoryDatabaseRestoreService(IDatabaseRestoreService inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public Task<DatabaseRestorePreviewResult> PreviewAsync(
        ServerProfile serverProfile,
        DatabaseRestoreRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.PreviewAsync(serverProfile, request, cancellationToken);

    public async Task<DatabaseRestoreResult> ExecuteAsync(
        ServerProfile serverProfile,
        DatabaseRestorePreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverProfile);
        ArgumentNullException.ThrowIfNull(preview);
        var started = Stopwatch.GetTimestamp();
        DatabaseRestoreResult result;
        try
        {
            result = await _inner.ExecuteAsync(serverProfile, preview, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await AppendAsync(
                    serverProfile,
                    preview,
                    OperationOutcome.Cancelled,
                    "cancelled",
                    RemoteErrorCode.OperationCancelled.ToString(),
                    Stopwatch.GetElapsedTime(started))
                .ConfigureAwait(false);
            throw;
        }

        var outcome = result.IsSuccess
            ? OperationOutcome.Succeeded
            : result.AmbiguousState ? OperationOutcome.Unknown : OperationOutcome.Failed;
        var verification = result.IsSuccess
            ? "restore-target-verified"
            : result.AmbiguousState ? "ambiguous-unknown" : "failed-known";
        try
        {
            await AppendAsync(
                    serverProfile,
                    preview,
                    outcome,
                    verification,
                    result.Error?.Code.ToString() ?? "None",
                    Stopwatch.GetElapsedTime(started))
                .ConfigureAwait(false);
            return result;
        }
        catch
        {
            return result with
            {
                Message = result.Message + " Database operation history persistence failed; do not repeat the restore solely to repair history.",
            };
        }
    }

    private async ValueTask AppendAsync(
        ServerProfile serverProfile,
        DatabaseRestorePreview preview,
        OperationOutcome outcome,
        string verification,
        string failureClass,
        TimeSpan elapsed)
    {
        var target = DatabaseOperationAuditMetadata.ForOperation(
            serverProfile,
            preview.Request.DatabaseProfileId,
            preview.Engine,
            preview.Request.TargetDatabase,
            preview.Request.BackupId,
            "restore",
            verification);
        var summary =
            $"Database restore outcome={outcome}; failure-class={failureClass}; duration-ms={(long)elapsed.TotalMilliseconds}; rollback-claimed={preview.RollbackAvailable}.";
        var entry = OperationAuditEntry.Create(
            "database-restore",
            summary,
            OperationRisk.Destructive,
            outcome,
            target);
        await _audit.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
    }
}
