using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Databases;

public sealed class DatabaseBackupService : IDatabaseBackupService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C",
            ["LANG"] = "C",
        };

    private const string PostgreSqlSecretScript =
        "IFS= read -r SERVERDESK_SECRET_B64; " +
        "SERVERDESK_SECRET=$(printf '%s' \"$SERVERDESK_SECRET_B64\" | base64 --decode; printf x); " +
        "PGPASSWORD=${SERVERDESK_SECRET%x}; " +
        "unset SERVERDESK_SECRET SERVERDESK_SECRET_B64; export PGPASSWORD; exec \"$@\"";

    private const string MySqlSecretScript =
        "IFS= read -r SERVERDESK_SECRET_B64; " +
        "SERVERDESK_SECRET=$(printf '%s' \"$SERVERDESK_SECRET_B64\" | base64 --decode; printf x); " +
        "MYSQL_PWD=${SERVERDESK_SECRET%x}; " +
        "unset SERVERDESK_SECRET SERVERDESK_SECRET_B64; export MYSQL_PWD; exec \"$@\"";

    private readonly IDatabaseProfileRepository _profiles;
    private readonly ISecretStore _secrets;
    private readonly IRemoteCommandExecutorFactory _commands;
    private readonly IDatabaseBackupManifestRepository _manifests;
    private readonly DatabaseBackupOptions _options;

    public DatabaseBackupService(
        IDatabaseProfileRepository profiles,
        ISecretStore secrets,
        IRemoteCommandExecutorFactory commands,
        IDatabaseBackupManifestRepository manifests,
        DatabaseBackupOptions options)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public ValueTask<IReadOnlyList<DatabaseBackupManifest>> ListHistoryAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default) =>
        _manifests.ListForServerAsync(serverProfileId, cancellationToken);

    public async Task<DatabaseBackupCreateResult> CreateAsync(
        ServerProfile serverProfile,
        DatabaseBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverProfile);
        ArgumentNullException.ThrowIfNull(request);

        DatabaseConnectionProfile? databaseProfile;
        RemotePath destination;
        string databaseName;
        try
        {
            if (request.DatabaseProfileId == Guid.Empty)
            {
                throw new ArgumentException("Database profile id cannot be empty.", nameof(request));
            }

            databaseName = NormalizeDatabaseName(request.DatabaseName);
            destination = NormalizeAbsoluteDirectory(request.DestinationDirectory);
            databaseProfile = await _profiles.GetAsync(request.DatabaseProfileId, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Failure(RemoteErrorCode.InvalidEndpoint, exception.Message);
        }

        if (databaseProfile is null)
        {
            return Failure(RemoteErrorCode.PathNotFound, "The selected database profile no longer exists.");
        }

        if (databaseProfile.ServerProfileId != serverProfile.Id)
        {
            return Failure(RemoteErrorCode.PathConflict, "The selected database profile does not belong to this server.");
        }

        if (databaseProfile.Engine == DatabaseEngineKind.Redis)
        {
            return Unsupported(
                "Redis backup is not certified in M6.4 because ServerDesk cannot yet prove deterministic persistence-copy semantics for the selected Redis runtime. No backup command was executed.");
        }

        if (databaseProfile.Engine is not DatabaseEngineKind.PostgreSql and
            not DatabaseEngineKind.MySql and
            not DatabaseEngineKind.MariaDb)
        {
            return Unsupported($"Database backup is not supported for {databaseProfile.Engine}.");
        }

        var secretResult = await ResolveSecretAsync(databaseProfile, cancellationToken).ConfigureAwait(false);
        if (secretResult.Error is not null)
        {
            return new DatabaseBackupCreateResult(
                null,
                false,
                false,
                false,
                secretResult.Error.Message,
                secretResult.Error);
        }

        await using var executor = _commands.Create(serverProfile);
        var directory = await ReadStatAsync(executor, destination, cancellationToken).ConfigureAwait(false);
        if (directory.Error is not null)
        {
            return new DatabaseBackupCreateResult(null, false, false, false, directory.Error.Message, directory.Error);
        }

        if (!string.Equals(directory.Kind, "directory", StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict, "The selected backup destination must be an existing directory.");
        }

        var capability = await InspectToolCapabilityAsync(
                executor,
                databaseProfile.Engine,
                cancellationToken)
            .ConfigureAwait(false);
        if (capability.Error is not null)
        {
            return new DatabaseBackupCreateResult(null, false, false, false, capability.Error.Message, capability.Error);
        }

        var backupId = Guid.NewGuid();
        var format = FormatFor(databaseProfile.Engine);
        var extension = format == DatabaseBackupFormat.PostgreSqlCustom ? "dump" : "sql";
        var backupPath = destination.Combine($"serverdesk-db-backup-{backupId:N}.{extension}");
        var dump = BuildDumpCommand(databaseProfile, databaseName, backupPath, secretResult.Secret);

        RemoteExecutionResult execution;
        try
        {
            execution = await executor.ExecuteAsync(dump, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await AmbiguousAfterDispatchAsync(
                    executor,
                    backupPath,
                    "Database backup was cancelled after dump dispatch began. The exact output path was re-inspected; do not blindly retry.")
                .ConfigureAwait(false);
        }

        if (execution.Error is not null)
        {
            if (IsAmbiguous(execution.Error.Code))
            {
                return await AmbiguousAfterDispatchAsync(
                        executor,
                        backupPath,
                        "Database dump completion is unknown after a transport/timeout/cancellation failure. The exact output path was re-inspected; do not blindly retry.")
                    .ConfigureAwait(false);
            }

            var observed = await ReadStatAllowMissingAsync(executor, backupPath, CancellationToken.None).ConfigureAwait(false);
            if (observed.Exists || observed.Error is not null)
            {
                return Ambiguous(
                    "Database dump reported a deterministic execution failure but the exact output path is present or could not be verified absent. Treat it as unverified and do not blindly retry.");
            }

            return new DatabaseBackupCreateResult(null, false, false, false, execution.Error.Message, execution.Error);
        }

        if (execution.Command!.ExitCode != 0)
        {
            var failure = ClassifyDumpFailure(databaseProfile.Engine, execution.Command.StandardError);
            var observed = await ReadStatAllowMissingAsync(executor, backupPath, CancellationToken.None).ConfigureAwait(false);
            if (observed.Exists || observed.Error is not null)
            {
                return Ambiguous(
                    "Database dump exited with failure but left output or the output state could not be proven absent. The artifact is unverified and must not be treated as restorable.",
                    failure);
            }

            return new DatabaseBackupCreateResult(null, false, false, false, failure.Message, failure);
        }

        var verification = await VerifyBackupAsync(
                executor,
                databaseProfile.Engine,
                backupPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (verification.Error is not null || verification.Evidence is null)
        {
            return Ambiguous(
                "The dump command returned success, but deterministic backup verification failed. The output remains unverified and must not be treated as restorable.",
                verification.Error);
        }

        var now = DateTimeOffset.UtcNow;
        var manifest = new DatabaseBackupManifest(
            backupId,
            serverProfile.Id,
            databaseProfile.Id,
            databaseProfile.Engine,
            databaseName,
            databaseProfile.Username,
            backupPath,
            format,
            capability.ToolName!,
            capability.Version!,
            now,
            verification.Evidence,
            true);

        var historyPersisted = true;
        try
        {
            await _manifests.AddAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            historyPersisted = false;
        }
        catch
        {
            historyPersisted = false;
        }

        var message = historyPersisted
            ? "Database backup was created and deterministically verified before being marked usable."
            : "Database backup was created and deterministically verified, but local history persistence failed. Do not repeat the backup solely to repair history.";
        return new DatabaseBackupCreateResult(manifest, false, false, historyPersisted, message);
    }

    private async Task<SecretRead> ResolveSecretAsync(
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.AuthenticationKind == DatabaseAuthenticationKind.None)
        {
            return new SecretRead(null, null);
        }

        if (profile.CredentialReference is not { } reference)
        {
            return new SecretRead(
                null,
                new RemoteError(RemoteErrorCode.AuthenticationFailed, "The database profile has no credential reference."));
        }

        try
        {
            var secret = await _secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(secret))
            {
                return new SecretRead(
                    null,
                    new RemoteError(RemoteErrorCode.AuthenticationFailed, "The stored database credential is unavailable."));
            }

            if (secret.Contains('\0'))
            {
                return new SecretRead(
                    null,
                    new RemoteError(RemoteErrorCode.CapabilityUnavailable, "The stored database credential contains NUL data that cannot be represented safely in a process environment."));
            }

            return new SecretRead(secret, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new SecretRead(
                null,
                new RemoteError(RemoteErrorCode.AuthenticationFailed, "The stored database credential could not be read securely."));
        }
    }

    private async Task<ToolCapability> InspectToolCapabilityAsync(
        IRemoteCommandExecutor executor,
        DatabaseEngineKind engine,
        CancellationToken cancellationToken)
    {
        var toolName = DumpToolFor(engine);
        var version = await ExecuteReadAsync(
                executor,
                new RemoteCommandSpec(
                    toolName,
                    ["--version"],
                    _options.InspectionTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (version.Error is not null)
        {
            return new ToolCapability(null, null, version.Error);
        }

        var normalizedVersion = FirstLine(version.Output!);
        if (string.IsNullOrWhiteSpace(normalizedVersion))
        {
            return new ToolCapability(
                null,
                null,
                new RemoteError(RemoteErrorCode.ParseFailed, $"{toolName} returned an empty version string."));
        }

        if (engine == DatabaseEngineKind.PostgreSql)
        {
            var verifier = await ExecuteReadAsync(
                    executor,
                    new RemoteCommandSpec(
                        "pg_restore",
                        ["--version"],
                        _options.InspectionTimeout,
                        OperationRisk.ReadOnly,
                        StableEnvironment),
                    cancellationToken)
                .ConfigureAwait(false);
            if (verifier.Error is not null)
            {
                return new ToolCapability(
                    null,
                    null,
                    new RemoteError(RemoteErrorCode.CapabilityUnavailable, "pg_restore is required to verify PostgreSQL custom-format backups."));
            }
        }

        return new ToolCapability(toolName, Bound(normalizedVersion), null);
    }

    private RemoteCommandSpec BuildDumpCommand(
        DatabaseConnectionProfile profile,
        string databaseName,
        RemotePath backupPath,
        string? secret)
    {
        var tool = DumpToolFor(profile.Engine);
        var arguments = BuildDumpArguments(profile, databaseName, backupPath);
        if (secret is null)
        {
            return new RemoteCommandSpec(
                tool,
                arguments,
                _options.CommandTimeout,
                OperationRisk.Mutating,
                NonSecretEnvironment(profile.Engine));
        }

        var encodedSecret = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)) + "\n";
        var script = profile.Engine == DatabaseEngineKind.PostgreSql
            ? PostgreSqlSecretScript
            : MySqlSecretScript;
        var shellArguments = new List<string>(arguments.Count + 4)
        {
            "-c",
            script,
            "serverdesk-database-backup",
            tool,
        };
        shellArguments.AddRange(arguments);
        return new RemoteCommandSpec(
            "sh",
            shellArguments,
            _options.CommandTimeout,
            OperationRisk.Mutating,
            NonSecretEnvironment(profile.Engine),
            StandardInput: new SensitiveCommandInput(encodedSecret));
    }

    private static IReadOnlyDictionary<string, string> NonSecretEnvironment(DatabaseEngineKind engine)
    {
        var values = new Dictionary<string, string>(StableEnvironment, StringComparer.Ordinal);
        if (engine == DatabaseEngineKind.PostgreSql)
        {
            values["PGPASSFILE"] = "/dev/null";
            values["PGCONNECT_TIMEOUT"] = "10";
        }

        return values;
    }

    private static IReadOnlyList<string> BuildDumpArguments(
        DatabaseConnectionProfile profile,
        string databaseName,
        RemotePath backupPath)
    {
        if (profile.Engine == DatabaseEngineKind.PostgreSql)
        {
            var arguments = new List<string>
            {
                "--host", profile.RemoteHost,
                "--port", profile.RemotePort.ToString(CultureInfo.InvariantCulture),
                "--no-password",
                "--format=custom",
                "--file", backupPath.Value,
            };
            if (!string.IsNullOrWhiteSpace(profile.Username))
            {
                arguments.Add("--username");
                arguments.Add(profile.Username);
            }

            arguments.Add(databaseName);
            return arguments;
        }

        var mysqlArguments = new List<string>
        {
            "--no-defaults",
            "--protocol=TCP",
            $"--host={profile.RemoteHost}",
            $"--port={profile.RemotePort.ToString(CultureInfo.InvariantCulture)}",
            "--single-transaction",
            "--skip-lock-tables",
            $"--result-file={backupPath.Value}",
        };
        if (!string.IsNullOrWhiteSpace(profile.Username))
        {
            mysqlArguments.Add($"--user={profile.Username}");
        }

        if (profile.AuthenticationKind == DatabaseAuthenticationKind.None)
        {
            mysqlArguments.Add("--skip-password");
        }

        mysqlArguments.Add("--databases");
        mysqlArguments.Add(databaseName);
        return mysqlArguments;
    }

    private async Task<VerificationRead> VerifyBackupAsync(
        IRemoteCommandExecutor executor,
        DatabaseEngineKind engine,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        var stat = await ReadStatAsync(executor, path, cancellationToken).ConfigureAwait(false);
        if (stat.Error is not null)
        {
            return new VerificationRead(null, stat.Error);
        }

        if (!string.Equals(stat.Kind, "regular file", StringComparison.Ordinal) || stat.Size is null || stat.Size <= 0)
        {
            return new VerificationRead(
                null,
                new RemoteError(RemoteErrorCode.ParseFailed, "Database backup output is not a non-empty regular file."));
        }

        if (stat.Size > _options.MaximumBackupBytes)
        {
            return new VerificationRead(
                null,
                new RemoteError(RemoteErrorCode.CapabilityUnavailable, "Database backup exceeds the configured verification size bound."));
        }

        var checksum = await ExecuteReadAsync(
                executor,
                new RemoteCommandSpec(
                    "sha256sum",
                    ["--", path.Value],
                    _options.InspectionTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (checksum.Error is not null)
        {
            return new VerificationRead(null, checksum.Error);
        }

        var sha = checksum.Output!
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (sha is null || sha.Length != 64 || sha.Any(character => !Uri.IsHexDigit(character)))
        {
            return new VerificationRead(
                null,
                new RemoteError(RemoteErrorCode.ParseFailed, "sha256sum returned an unrecognized database backup checksum."));
        }

        string structuralCheck;
        if (engine == DatabaseEngineKind.PostgreSql)
        {
            var structural = await ExecuteReadAsync(
                    executor,
                    new RemoteCommandSpec(
                        "sh",
                        ["-c", "pg_restore --list \"$1\" >/dev/null", "serverdesk-pg-verify", path.Value],
                        _options.InspectionTimeout,
                        OperationRisk.ReadOnly,
                        StableEnvironment),
                    cancellationToken)
                .ConfigureAwait(false);
            if (structural.Error is not null)
            {
                return new VerificationRead(null, structural.Error);
            }

            structuralCheck = "pg_restore --list parsed the PostgreSQL custom archive successfully";
        }
        else
        {
            var header = await ExecuteReadAsync(
                    executor,
                    new RemoteCommandSpec(
                        "head",
                        ["-c", "8192", "--", path.Value],
                        _options.InspectionTimeout,
                        OperationRisk.ReadOnly,
                        StableEnvironment),
                    cancellationToken)
                .ConfigureAwait(false);
            if (header.Error is not null)
            {
                return new VerificationRead(null, header.Error);
            }

            var expected = engine == DatabaseEngineKind.MariaDb ? "MariaDB dump" : "MySQL dump";
            if (!header.Output!.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                return new VerificationRead(
                    null,
                    new RemoteError(RemoteErrorCode.ParseFailed, $"{expected} structural header was not found in the bounded backup prefix."));
            }

            structuralCheck = $"bounded SQL prefix contains the expected {expected} structural header";
        }

        return new VerificationRead(
            new DatabaseBackupVerificationEvidence(
                stat.Size.Value,
                sha.ToUpperInvariant(),
                structuralCheck,
                DateTimeOffset.UtcNow),
            null);
    }

    private async Task<DatabaseBackupCreateResult> AmbiguousAfterDispatchAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        string message)
    {
        var state = await ReadStatAllowMissingAsync(executor, path, CancellationToken.None).ConfigureAwait(false);
        var suffix = state.Error is not null
            ? " The exact output state could not be read."
            : state.Exists
                ? " An output artifact exists but is unverified."
                : " No output artifact was observed at re-inspection time, but remote completion is still unknown.";
        return Ambiguous(message + suffix);
    }

    private async Task<StatRead> ReadStatAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteReadAsync(
                executor,
                new RemoteCommandSpec(
                    "stat",
                    ["--printf=%F\t%s", "--", path.Value],
                    _options.InspectionTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new StatRead(null, null, result.Error);
        }

        var fields = result.Output!.Trim().Split('\t');
        if (fields.Length != 2 || !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
        {
            return new StatRead(null, null, new RemoteError(RemoteErrorCode.ParseFailed, "stat returned an unrecognized database backup metadata row."));
        }

        return new StatRead(fields[0].Trim(), size, null);
    }

    private async Task<OptionalStatRead> ReadStatAllowMissingAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "stat",
                    ["--printf=%F\t%s", "--", path.Value],
                    _options.InspectionTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return new OptionalStatRead(false, execution.Error);
        }

        if (execution.Command!.ExitCode != 0)
        {
            var missing = execution.Command.StandardError.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
                execution.Command.StandardError.Contains("cannot stat", StringComparison.OrdinalIgnoreCase);
            return missing
                ? new OptionalStatRead(false, null)
                : new OptionalStatRead(false, new RemoteError(RemoteErrorCode.CommandFailed, "Database backup output state could not be inspected."));
        }

        return new OptionalStatRead(true, null);
    }

    private async Task<ReadResult> ExecuteReadAsync(
        IRemoteCommandExecutor executor,
        RemoteCommandSpec command,
        CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return new ReadResult(null, execution.Error);
        }

        if (execution.Command!.StandardOutput.Length > _options.MaximumDiagnosticCharacters ||
            execution.Command.StandardError.Length > _options.MaximumDiagnosticCharacters)
        {
            return new ReadResult(
                null,
                new RemoteError(RemoteErrorCode.CapabilityUnavailable, "Database backup inspection output exceeded the configured safety bound."));
        }

        if (execution.Command.ExitCode != 0)
        {
            return new ReadResult(
                null,
                new RemoteError(ClassifyInspectionFailure(execution.Command.StandardError), "Database backup inspection command failed."));
        }

        return new ReadResult(execution.Command.StandardOutput, null);
    }

    private static string NormalizeDatabaseName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("An explicit database/schema name is required.", nameof(value));
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > 128 || value.Any(char.IsControl))
        {
            throw new ArgumentException("Database/schema identity must be 1-128 printable characters without leading/trailing whitespace.", nameof(value));
        }

        return value;
    }

    private static RemotePath NormalizeAbsoluteDirectory(string value)
    {
        var raw = value?.Trim() ?? string.Empty;
        if (!raw.StartsWith("/", StringComparison.Ordinal) || raw.Any(char.IsControl))
        {
            throw new ArgumentException("Backup destination must be an absolute printable remote path.", nameof(value));
        }

        var path = RemotePath.Parse(raw);
        var expected = raw.Length > 1 ? raw.TrimEnd('/') : raw;
        if (!path.IsAbsolute || !string.Equals(path.Value, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException("Backup destination must already be a normalized absolute path without traversal segments.", nameof(value));
        }

        return path;
    }

    private static DatabaseBackupFormat FormatFor(DatabaseEngineKind engine) => engine switch
    {
        DatabaseEngineKind.PostgreSql => DatabaseBackupFormat.PostgreSqlCustom,
        DatabaseEngineKind.MySql => DatabaseBackupFormat.MySqlSql,
        DatabaseEngineKind.MariaDb => DatabaseBackupFormat.MariaDbSql,
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    private static string DumpToolFor(DatabaseEngineKind engine) => engine switch
    {
        DatabaseEngineKind.PostgreSql => "pg_dump",
        DatabaseEngineKind.MySql => "mysqldump",
        DatabaseEngineKind.MariaDb => "mariadb-dump",
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    private static RemoteError ClassifyDumpFailure(DatabaseEngineKind engine, string standardError)
    {
        var detail = standardError ?? string.Empty;
        if (detail.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Access denied for user", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteError(RemoteErrorCode.AuthenticationFailed, $"{engine} rejected the stored database credential.");
        }

        if (detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("command denied", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteError(RemoteErrorCode.PermissionDenied, $"{engine} denied a required backup operation.");
        }

        if (detail.Contains("could not connect", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("can't connect", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("unknown server host", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteError(RemoteErrorCode.ConnectionFailed, $"{engine} could not be reached from the SSH server for backup.");
        }

        return new RemoteError(RemoteErrorCode.CommandFailed, $"{engine} backup command failed without producing a verified artifact.");
    }

    private static RemoteErrorCode ClassifyInspectionFailure(string standardError)
    {
        var detail = standardError ?? string.Empty;
        if (detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PermissionDenied;
        }

        if (detail.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.CapabilityUnavailable;
        }

        return RemoteErrorCode.CommandFailed;
    }

    private static bool IsAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or
            RemoteErrorCode.OperationCancelled or RemoteErrorCode.ConnectionFailed or RemoteErrorCode.AmbiguousState;

    private string Bound(string value) =>
        value.Length <= _options.MaximumDiagnosticCharacters
            ? value
            : value[.._options.MaximumDiagnosticCharacters];

    private static string FirstLine(string value) =>
        value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

    private static DatabaseBackupCreateResult Failure(RemoteErrorCode code, string message) =>
        new(null, false, false, false, message, new RemoteError(code, message));

    private static DatabaseBackupCreateResult Unsupported(string message) =>
        new(null, false, true, false, message, new RemoteError(RemoteErrorCode.CapabilityUnavailable, message));

    private static DatabaseBackupCreateResult Ambiguous(string message, RemoteError? cause = null) =>
        new(
            null,
            true,
            false,
            false,
            message,
            new RemoteError(RemoteErrorCode.AmbiguousState, message, cause?.TechnicalDetails));

    private sealed record SecretRead(string? Secret, RemoteError? Error);
    private sealed record ToolCapability(string? ToolName, string? Version, RemoteError? Error);
    private sealed record StatRead(string? Kind, long? Size, RemoteError? Error);
    private sealed record OptionalStatRead(bool Exists, RemoteError? Error);
    private sealed record ReadResult(string? Output, RemoteError? Error);
    private sealed record VerificationRead(DatabaseBackupVerificationEvidence? Evidence, RemoteError? Error);
}

public sealed class AuditedDatabaseBackupService : IDatabaseBackupService
{
    private readonly IDatabaseBackupService _inner;
    private readonly IOperationAudit _audit;

    public AuditedDatabaseBackupService(IDatabaseBackupService inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
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
        var result = await _inner.CreateAsync(serverProfile, request, cancellationToken).ConfigureAwait(false);
        var outcome = result.IsSuccess
            ? OperationOutcome.Succeeded
            : result.AmbiguousState ? OperationOutcome.Unknown : OperationOutcome.Failed;
        var backupId = result.Manifest?.BackupId.ToString() ?? "unverified";
        var target =
            $"{serverProfile.Username}@{serverProfile.Host}:{serverProfile.Port} database-profile:{request.DatabaseProfileId} database:{SafeAuditToken(request.DatabaseName)} backup-id:{backupId}";
        var entry = OperationAuditEntry.Create(
            "database-backup",
            $"Database backup verification outcome: {outcome}; backup-id={backupId}; history-persisted={result.HistoryPersisted}",
            OperationRisk.Mutating,
            outcome,
            target);

        try
        {
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch
        {
            return result with
            {
                Message = result.Message + " Audit persistence failed; do not repeat database backup solely for audit.",
            };
        }
    }

    private static string SafeAuditToken(string value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > 128)
        {
            normalized = normalized[..128];
        }

        return string.Concat(normalized.Select(character => char.IsControl(character) ? '?' : character));
    }
}
