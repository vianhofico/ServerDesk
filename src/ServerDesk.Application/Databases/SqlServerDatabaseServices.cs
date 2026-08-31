using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Databases;

public sealed class DatabaseBackupServiceRouter : IDatabaseBackupService
{
    private readonly IDatabaseProfileRepository _profiles;
    private readonly IDatabaseBackupService _defaultService;
    private readonly IDatabaseBackupService _sqlServerService;

    public DatabaseBackupServiceRouter(
        IDatabaseProfileRepository profiles,
        IDatabaseBackupService defaultService,
        IDatabaseBackupService sqlServerService)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _defaultService = defaultService ?? throw new ArgumentNullException(nameof(defaultService));
        _sqlServerService = sqlServerService ?? throw new ArgumentNullException(nameof(sqlServerService));
    }

    public async Task<DatabaseBackupCreateResult> CreateAsync(
        ServerProfile serverProfile,
        DatabaseBackupRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.DatabaseProfileId == Guid.Empty
            ? null
            : await _profiles.GetAsync(request.DatabaseProfileId, cancellationToken).ConfigureAwait(false);
        return profile?.Engine == DatabaseEngineKind.SqlServer
            ? await _sqlServerService.CreateAsync(serverProfile, request, cancellationToken).ConfigureAwait(false)
            : await _defaultService.CreateAsync(serverProfile, request, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<DatabaseBackupManifest>> ListHistoryAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default) =>
        _defaultService.ListHistoryAsync(serverProfileId, cancellationToken);
}

public sealed class DatabaseRestoreServiceRouter : IDatabaseRestoreService
{
    private readonly IDatabaseProfileRepository _profiles;
    private readonly IDatabaseRestoreService _defaultService;
    private readonly IDatabaseRestoreService _sqlServerService;

    public DatabaseRestoreServiceRouter(
        IDatabaseProfileRepository profiles,
        IDatabaseRestoreService defaultService,
        IDatabaseRestoreService sqlServerService)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _defaultService = defaultService ?? throw new ArgumentNullException(nameof(defaultService));
        _sqlServerService = sqlServerService ?? throw new ArgumentNullException(nameof(sqlServerService));
    }

    public async Task<DatabaseRestorePreviewResult> PreviewAsync(
        ServerProfile serverProfile,
        DatabaseRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var profile = request.DatabaseProfileId == Guid.Empty
            ? null
            : await _profiles.GetAsync(request.DatabaseProfileId, cancellationToken).ConfigureAwait(false);
        return profile?.Engine == DatabaseEngineKind.SqlServer
            ? await _sqlServerService.PreviewAsync(serverProfile, request, cancellationToken).ConfigureAwait(false)
            : await _defaultService.PreviewAsync(serverProfile, request, cancellationToken).ConfigureAwait(false);
    }

    public Task<DatabaseRestoreResult> ExecuteAsync(
        ServerProfile serverProfile,
        DatabaseRestorePreview preview,
        CancellationToken cancellationToken = default) =>
        preview?.Engine == DatabaseEngineKind.SqlServer
            ? _sqlServerService.ExecuteAsync(serverProfile, preview, cancellationToken)
            : _defaultService.ExecuteAsync(serverProfile, preview!, cancellationToken);
}

public sealed class SqlServerDatabaseBackupService : IDatabaseBackupService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C",
            ["LANG"] = "C",
        };

    private const string SqlCmdSecretScript =
        "IFS= read -r SERVERDESK_SECRET_B64; " +
        "SERVERDESK_SECRET=$(printf '%s' \"$SERVERDESK_SECRET_B64\" | base64 --decode; printf x); " +
        "SQLCMDPASSWORD=${SERVERDESK_SECRET%x}; " +
        "unset SERVERDESK_SECRET SERVERDESK_SECRET_B64; export SQLCMDPASSWORD; exec \"$@\"";

    private readonly IDatabaseProfileRepository _profiles;
    private readonly ISecretStore _secrets;
    private readonly IRemoteCommandExecutorFactory _commands;
    private readonly IDatabaseBackupManifestRepository _manifests;
    private readonly DatabaseBackupOptions _options;

    public SqlServerDatabaseBackupService(
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
        if (request.DatabaseProfileId == Guid.Empty)
        {
            return Failure(RemoteErrorCode.InvalidEndpoint, "Database profile id cannot be empty.");
        }

        string databaseName;
        RemotePath destination;
        try
        {
            databaseName = NormalizeDatabaseName(request.DatabaseName);
            destination = NormalizeAbsoluteDirectory(request.DestinationDirectory);
        }
        catch (ArgumentException exception)
        {
            return Failure(RemoteErrorCode.InvalidEndpoint, exception.Message);
        }

        var profile = await _profiles.GetAsync(request.DatabaseProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return Failure(RemoteErrorCode.PathNotFound, "The selected database profile no longer exists.");
        }

        if (profile.Engine != DatabaseEngineKind.SqlServer)
        {
            return Unsupported("The SQL Server backup service refuses non-SQL-Server profiles.");
        }

        if (profile.ServerProfileId != serverProfile.Id)
        {
            return Failure(RemoteErrorCode.PathConflict, "The selected database profile does not belong to this server.");
        }

        if (string.IsNullOrWhiteSpace(profile.DatabaseName) ||
            !string.Equals(profile.DatabaseName, databaseName, StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict, "SQL Server backup requires the exact database identity saved in the selected profile.");
        }

        var secret = await ResolveSecretAsync(profile, cancellationToken).ConfigureAwait(false);
        if (secret.Error is not null)
        {
            return new DatabaseBackupCreateResult(null, false, false, false, secret.Error.Message, secret.Error);
        }

        await using var executor = _commands.Create(serverProfile);
        var directory = await ReadStatAsync(executor, destination, cancellationToken).ConfigureAwait(false);
        if (directory.Error is not null)
        {
            return new DatabaseBackupCreateResult(null, false, false, false, directory.Error.Message, directory.Error);
        }

        if (!string.Equals(directory.Kind, "directory", StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict, "The selected SQL Server backup destination must be an existing directory.");
        }

        var serverVersion = await ReadServerVersionAsync(executor, profile, secret.Secret, cancellationToken).ConfigureAwait(false);
        if (serverVersion.Error is not null)
        {
            return new DatabaseBackupCreateResult(null, false, false, false, serverVersion.Error.Message, serverVersion.Error);
        }

        if (DatabaseCertificationMatrix.LevelFor(
                DatabaseEngineKind.SqlServer,
                serverVersion.Version!,
                DatabaseCapabilityKind.Backup) != DatabaseCertificationLevel.Certified)
        {
            return Unsupported($"SQL Server {serverVersion.Version} backup is not certified. No backup command was executed.");
        }

        var toolVersion = await ReadSqlCmdVersionAsync(executor, cancellationToken).ConfigureAwait(false);
        if (toolVersion.Error is not null)
        {
            return new DatabaseBackupCreateResult(null, false, false, false, toolVersion.Error.Message, toolVersion.Error);
        }

        var backupId = Guid.NewGuid();
        var backupPath = destination.Combine($"serverdesk-db-backup-{backupId:N}.bak");
        var backupQuery =
            $"BACKUP DATABASE {SqlIdentifier(databaseName)} TO DISK = {SqlString(backupPath.Value)} " +
            "WITH COPY_ONLY, INIT, CHECKSUM, STATS = 10;";
        var command = BuildSqlCmd(
            profile,
            "master",
            backupQuery,
            OperationRisk.Mutating,
            secret.Secret,
            _options.CommandTimeout);

        RemoteExecutionResult execution;
        try
        {
            execution = await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return await AmbiguousAfterDispatchAsync(
                    executor,
                    backupPath,
                    "SQL Server backup was cancelled after BACKUP DATABASE dispatch began. Do not blindly retry.")
                .ConfigureAwait(false);
        }

        if (execution.Error is not null)
        {
            if (IsAmbiguous(execution.Error.Code))
            {
                return await AmbiguousAfterDispatchAsync(
                        executor,
                        backupPath,
                        "SQL Server backup completion is unknown after transport/timeout/cancellation failure. Do not blindly retry.")
                    .ConfigureAwait(false);
            }

            var state = await ReadStatAllowMissingAsync(executor, backupPath, CancellationToken.None).ConfigureAwait(false);
            if (state.Exists || state.Error is not null)
            {
                return Ambiguous("SQL Server backup reported failure but output exists or could not be proven absent. The artifact is unverified.");
            }

            return new DatabaseBackupCreateResult(null, false, false, false, execution.Error.Message, execution.Error);
        }

        if (execution.Command!.ExitCode != 0)
        {
            var state = await ReadStatAllowMissingAsync(executor, backupPath, CancellationToken.None).ConfigureAwait(false);
            if (state.Exists || state.Error is not null)
            {
                return Ambiguous("sqlcmd returned failure after BACKUP DATABASE dispatch and the exact output state is not safely retryable.");
            }

            var failure = ClassifySqlFailure(execution.Command.StandardError + "\n" + execution.Command.StandardOutput);
            return new DatabaseBackupCreateResult(null, false, false, false, failure.Message, failure);
        }

        var verification = await VerifyBackupAsync(
                executor,
                profile,
                backupPath,
                secret.Secret,
                cancellationToken)
            .ConfigureAwait(false);
        if (verification.Error is not null || verification.Evidence is null)
        {
            return Ambiguous(
                "BACKUP DATABASE returned success, but deterministic SQL Server backup verification failed. The .bak file remains unverified.",
                verification.Error);
        }

        var manifest = new DatabaseBackupManifest(
            backupId,
            serverProfile.Id,
            profile.Id,
            DatabaseEngineKind.SqlServer,
            databaseName,
            profile.Username,
            backupPath,
            DatabaseBackupFormat.SqlServerNative,
            "sqlcmd",
            toolVersion.Version!,
            DateTimeOffset.UtcNow,
            verification.Evidence,
            true);
        var historyPersisted = true;
        try
        {
            await _manifests.AddAsync(manifest, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            historyPersisted = false;
        }

        var message = historyPersisted
            ? "SQL Server native backup was created and verified with RESTORE VERIFYONLY before being marked usable."
            : "SQL Server backup was verified, but local history persistence failed. Do not repeat the backup solely to repair history.";
        return new DatabaseBackupCreateResult(manifest, false, false, historyPersisted, message);
    }

    private async Task<VerificationRead> VerifyBackupAsync(
        IRemoteCommandExecutor executor,
        DatabaseConnectionProfile profile,
        RemotePath path,
        string? secret,
        CancellationToken cancellationToken)
    {
        var stat = await ReadStatAsync(executor, path, cancellationToken).ConfigureAwait(false);
        if (stat.Error is not null)
        {
            return new VerificationRead(null, stat.Error);
        }

        if (!string.Equals(stat.Kind, "regular file", StringComparison.Ordinal) || stat.Size is null || stat.Size <= 0)
        {
            return new VerificationRead(null, new RemoteError(RemoteErrorCode.ParseFailed, "SQL Server backup output is not a non-empty regular file."));
        }

        if (stat.Size > _options.MaximumBackupBytes)
        {
            return new VerificationRead(null, new RemoteError(RemoteErrorCode.CapabilityUnavailable, "SQL Server backup exceeds the configured verification size bound."));
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
            return new VerificationRead(null, new RemoteError(RemoteErrorCode.ParseFailed, "sha256sum returned an unrecognized SQL Server backup checksum."));
        }

        var verify = await executor.ExecuteAsync(
            BuildSqlCmd(
                profile,
                "master",
                $"RESTORE VERIFYONLY FROM DISK = {SqlString(path.Value)} WITH CHECKSUM;",
                OperationRisk.ReadOnly,
                secret,
                _options.InspectionTimeout),
            cancellationToken).ConfigureAwait(false);
        if (verify.Error is not null)
        {
            return new VerificationRead(null, verify.Error);
        }

        if (verify.Command!.ExitCode != 0)
        {
            return new VerificationRead(null, new RemoteError(
                ClassifySqlFailure(verify.Command.StandardError + "\n" + verify.Command.StandardOutput).Code,
                "SQL Server RESTORE VERIFYONLY rejected the backup artifact."));
        }

        return new VerificationRead(
            new DatabaseBackupVerificationEvidence(
                stat.Size.Value,
                sha.ToUpperInvariant(),
                "RESTORE VERIFYONLY WITH CHECKSUM accepted the SQL Server native backup",
                DateTimeOffset.UtcNow),
            null);
    }

    private async Task<VersionRead> ReadServerVersionAsync(
        IRemoteCommandExecutor executor,
        DatabaseConnectionProfile profile,
        string? secret,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
            BuildSqlCmd(
                profile,
                "master",
                "SET NOCOUNT ON; SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128));",
                OperationRisk.ReadOnly,
                secret,
                _options.InspectionTimeout,
                compactOutput: true),
            cancellationToken).ConfigureAwait(false);
        return ParseSingleValue(result, "SQL Server version could not be verified.");
    }

    private async Task<VersionRead> ReadSqlCmdVersionAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
            new RemoteCommandSpec("sqlcmd", ["-?"], _options.InspectionTimeout, OperationRisk.ReadOnly, StableEnvironment),
            cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new VersionRead(null, result.Error);
        }

        var value = FirstUsefulLine(result.Command!.StandardOutput, result.Command.StandardError);
        return result.Command.ExitCode == 0 && !string.IsNullOrWhiteSpace(value)
            ? new VersionRead(Bound(value), null)
            : new VersionRead(null, new RemoteError(RemoteErrorCode.CapabilityUnavailable, "sqlcmd client tooling is unavailable or returned an unrecognized version response."));
    }

    private static VersionRead ParseSingleValue(RemoteExecutionResult result, string failureMessage)
    {
        if (result.Error is not null)
        {
            return new VersionRead(null, result.Error);
        }

        if (result.Command!.ExitCode != 0)
        {
            return new VersionRead(null, ClassifySqlFailure(result.Command.StandardError + "\n" + result.Command.StandardOutput));
        }

        var value = FirstUsefulLine(result.Command.StandardOutput, string.Empty);
        return string.IsNullOrWhiteSpace(value)
            ? new VersionRead(null, new RemoteError(RemoteErrorCode.ParseFailed, failureMessage))
            : new VersionRead(value.Trim(), null);
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
            return new SecretRead(null, new RemoteError(RemoteErrorCode.AuthenticationFailed, "The SQL Server profile has no credential reference."));
        }

        try
        {
            var value = await _secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(value) || value.Contains('\0'))
            {
                return new SecretRead(null, new RemoteError(RemoteErrorCode.AuthenticationFailed, "The stored SQL Server credential is unavailable or cannot be represented safely."));
            }

            return new SecretRead(value, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new SecretRead(null, new RemoteError(RemoteErrorCode.AuthenticationFailed, "The stored SQL Server credential could not be read securely."));
        }
    }

    private RemoteCommandSpec BuildSqlCmd(
        DatabaseConnectionProfile profile,
        string database,
        string query,
        OperationRisk risk,
        string? secret,
        TimeSpan timeout,
        bool compactOutput = false)
    {
        var arguments = SqlCmdArguments(profile, database, query, compactOutput);
        if (secret is null)
        {
            return new RemoteCommandSpec("sqlcmd", arguments, timeout, risk, StableEnvironment);
        }

        var shell = new List<string>
        {
            "-c",
            SqlCmdSecretScript,
            "serverdesk-sqlserver",
            "sqlcmd",
        };
        shell.AddRange(arguments);
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)) + "\n";
        return new RemoteCommandSpec(
            "sh",
            shell,
            timeout,
            risk,
            StableEnvironment,
            StandardInput: new SensitiveCommandInput(encoded));
    }

    private static IReadOnlyList<string> SqlCmdArguments(
        DatabaseConnectionProfile profile,
        string database,
        string query,
        bool compactOutput)
    {
        var arguments = new List<string>
        {
            "-S", $"tcp:{profile.RemoteHost},{profile.RemotePort.ToString(CultureInfo.InvariantCulture)}",
            "-d", database,
            "-C",
            "-b",
            "-r", "1",
        };
        if (profile.AuthenticationKind == DatabaseAuthenticationKind.Password)
        {
            arguments.Add("-U");
            arguments.Add(profile.Username ?? string.Empty);
        }
        else
        {
            arguments.Add("-E");
        }

        if (compactOutput)
        {
            arguments.AddRange(["-h", "-1", "-W"]);
        }

        arguments.Add("-Q");
        arguments.Add(query);
        return arguments;
    }

    private async Task<StatRead> ReadStatAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        var read = await ExecuteReadAsync(
                executor,
                new RemoteCommandSpec(
                    "stat",
                    ["--printf=%F\t%s", "--", path.Value],
                    _options.InspectionTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (read.Error is not null)
        {
            return new StatRead(null, null, read.Error);
        }

        var fields = read.Output!.Trim().Split('\t');
        return fields.Length == 2 && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            ? new StatRead(fields[0].Trim(), size, null)
            : new StatRead(null, null, new RemoteError(RemoteErrorCode.ParseFailed, "stat returned an unrecognized SQL Server backup metadata row."));
    }

    private async Task<OptionalStatRead> ReadStatAllowMissingAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
            new RemoteCommandSpec(
                "stat",
                ["--printf=%F\t%s", "--", path.Value],
                _options.InspectionTimeout,
                OperationRisk.ReadOnly,
                StableEnvironment),
            cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new OptionalStatRead(false, result.Error);
        }

        if (result.Command!.ExitCode == 0)
        {
            return new OptionalStatRead(true, null);
        }

        var detail = result.Command.StandardError;
        return detail.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
               detail.Contains("cannot stat", StringComparison.OrdinalIgnoreCase)
            ? new OptionalStatRead(false, null)
            : new OptionalStatRead(false, new RemoteError(RemoteErrorCode.CommandFailed, "SQL Server backup output state could not be inspected."));
    }

    private async Task<ReadResult> ExecuteReadAsync(
        IRemoteCommandExecutor executor,
        RemoteCommandSpec command,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new ReadResult(null, result.Error);
        }

        if (result.Command!.StandardOutput.Length > _options.MaximumDiagnosticCharacters ||
            result.Command.StandardError.Length > _options.MaximumDiagnosticCharacters)
        {
            return new ReadResult(null, new RemoteError(RemoteErrorCode.CapabilityUnavailable, "SQL Server inspection output exceeded the configured safety bound."));
        }

        return result.Command.ExitCode == 0
            ? new ReadResult(result.Command.StandardOutput, null)
            : new ReadResult(null, ClassifySqlFailure(result.Command.StandardError + "\n" + result.Command.StandardOutput));
    }

    private async Task<DatabaseBackupCreateResult> AmbiguousAfterDispatchAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        string message)
    {
        var state = await ReadStatAllowMissingAsync(executor, path, CancellationToken.None).ConfigureAwait(false);
        var suffix = state.Error is not null
            ? " The exact .bak state could not be read."
            : state.Exists
                ? " A .bak artifact exists but is unverified."
                : " No .bak artifact was observed at re-inspection time, but remote completion is still unknown.";
        return Ambiguous(message + suffix);
    }

    private string Bound(string value) => value.Length <= _options.MaximumDiagnosticCharacters
        ? value
        : value[.._options.MaximumDiagnosticCharacters];

    private static string NormalizeDatabaseName(string value)
    {
        var normalized = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) ||
            !string.Equals(normalized, normalized.Trim(), StringComparison.Ordinal) ||
            normalized.Length > 128 || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("SQL Server database identity must be 1-128 printable characters without leading/trailing whitespace.", nameof(value));
        }

        return normalized;
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
            throw new ArgumentException("Backup destination must already be normalized and cannot contain traversal segments.", nameof(value));
        }

        return path;
    }

    internal static string SqlIdentifier(string value) => "[" + value.Replace("]", "]]", StringComparison.Ordinal) + "]";

    internal static string SqlString(string value) => "N'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    internal static RemoteError ClassifySqlFailure(string detail)
    {
        detail ??= string.Empty;
        if (detail.Contains("Login failed for user", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteError(RemoteErrorCode.AuthenticationFailed, "SQL Server rejected the stored database credential.");
        }

        if (detail.Contains("permission", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not have permission", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteError(RemoteErrorCode.PermissionDenied, "SQL Server denied a required database operation.");
        }

        if (detail.Contains("network-related", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("server was not found", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteError(RemoteErrorCode.ConnectionFailed, "SQL Server could not be reached from the SSH-controlled database operation path.");
        }

        return new RemoteError(RemoteErrorCode.CommandFailed, "SQL Server command failed without exposing credential-bearing diagnostics.");
    }

    internal static bool IsAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or
            RemoteErrorCode.OperationCancelled or RemoteErrorCode.ConnectionFailed or RemoteErrorCode.AmbiguousState;

    internal static string FirstUsefulLine(string first, string second) =>
        (string.IsNullOrWhiteSpace(first) ? second : first)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

    private static DatabaseBackupCreateResult Failure(RemoteErrorCode code, string message) =>
        new(null, false, false, false, message, new RemoteError(code, message));

    private static DatabaseBackupCreateResult Unsupported(string message) =>
        new(null, false, true, false, message, new RemoteError(RemoteErrorCode.CapabilityUnavailable, message));

    private static DatabaseBackupCreateResult Ambiguous(string message, RemoteError? cause = null) =>
        new(null, true, false, false, message, new RemoteError(RemoteErrorCode.AmbiguousState, message, cause?.TechnicalDetails));

    private sealed record SecretRead(string? Secret, RemoteError? Error);
    private sealed record StatRead(string? Kind, long? Size, RemoteError? Error);
    private sealed record OptionalStatRead(bool Exists, RemoteError? Error);
    private sealed record ReadResult(string? Output, RemoteError? Error);
    private sealed record VersionRead(string? Version, RemoteError? Error);
    private sealed record VerificationRead(DatabaseBackupVerificationEvidence? Evidence, RemoteError? Error);
}

public sealed class SqlServerDatabaseRestoreService : IDatabaseRestoreService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C",
            ["LANG"] = "C",
        };

    private const string SqlCmdSecretScript =
        "IFS= read -r SERVERDESK_SECRET_B64; " +
        "SERVERDESK_SECRET=$(printf '%s' \"$SERVERDESK_SECRET_B64\" | base64 --decode; printf x); " +
        "SQLCMDPASSWORD=${SERVERDESK_SECRET%x}; " +
        "unset SERVERDESK_SECRET SERVERDESK_SECRET_B64; export SQLCMDPASSWORD; exec \"$@\"";

    private const string TargetQuery =
        "SET NOCOUNT ON; " +
        "SELECT DB_NAME(); " +
        "SELECT SUSER_SNAME(); " +
        "SELECT CAST(SERVERPROPERTY('ProductVersion') AS nvarchar(128)); " +
        "SELECT CAST((SELECT COUNT_BIG(*) FROM sys.tables WHERE is_ms_shipped = 0) AS nvarchar(32));";

    private readonly IDatabaseProfileRepository _profiles;
    private readonly IDatabaseBackupManifestRepository _manifests;
    private readonly ISecretStore _secrets;
    private readonly IRemoteCommandExecutorFactory _commands;
    private readonly DatabaseRestoreOptions _options;
    private readonly ConcurrentDictionary<Guid, string> _capabilities = new();

    public SqlServerDatabaseRestoreService(
        IDatabaseProfileRepository profiles,
        IDatabaseBackupManifestRepository manifests,
        ISecretStore secrets,
        IRemoteCommandExecutorFactory commands,
        DatabaseRestoreOptions options)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<DatabaseRestorePreviewResult> PreviewAsync(
        ServerProfile serverProfile,
        DatabaseRestoreRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverProfile);
        ArgumentNullException.ThrowIfNull(request);
        DatabaseRestoreRequest normalized;
        try
        {
            normalized = NormalizeRequest(request);
        }
        catch (ArgumentException exception)
        {
            return PreviewFailure(RemoteErrorCode.InvalidEndpoint, exception.Message);
        }

        var binding = await LoadBindingAsync(serverProfile, normalized, cancellationToken).ConfigureAwait(false);
        if (binding.Error is not null || binding.Profile is null || binding.Manifest is null)
        {
            return new DatabaseRestorePreviewResult(null, binding.Error, binding.Unsupported);
        }

        var secret = await ResolveSecretAsync(binding.Profile, cancellationToken).ConfigureAwait(false);
        if (secret.Error is not null)
        {
            return new DatabaseRestorePreviewResult(null, secret.Error);
        }

        await using var executor = _commands.Create(serverProfile);
        var backup = await InspectBackupAsync(executor, binding.Profile, binding.Manifest, secret.Secret, cancellationToken)
            .ConfigureAwait(false);
        if (backup is not null)
        {
            return new DatabaseRestorePreviewResult(null, backup);
        }

        var target = await InspectTargetAsync(
                executor,
                binding.Profile,
                normalized.TargetDatabase,
                secret.Secret,
                cancellationToken)
            .ConfigureAwait(false);
        if (target.Error is not null || target.Snapshot is null)
        {
            return new DatabaseRestorePreviewResult(null, target.Error ?? new RemoteError(RemoteErrorCode.ParseFailed, "SQL Server restore target could not be inspected."));
        }

        if (DatabaseCertificationMatrix.LevelFor(
                DatabaseEngineKind.SqlServer,
                target.Snapshot.ServerVersion,
                DatabaseCapabilityKind.Restore) != DatabaseCertificationLevel.Certified)
        {
            return new DatabaseRestorePreviewResult(
                null,
                new RemoteError(RemoteErrorCode.CapabilityUnavailable, $"SQL Server {target.Snapshot.ServerVersion} restore is not certified."),
                Unsupported: true);
        }

        var toolVersion = await ReadSqlCmdVersionAsync(executor, cancellationToken).ConfigureAwait(false);
        if (toolVersion.Error is not null)
        {
            return new DatabaseRestorePreviewResult(null, toolVersion.Error);
        }

        var command = BuildRestorePlan(binding.Profile, binding.Manifest, normalized.TargetDatabase, secret.Secret is not null);
        var planId = Guid.NewGuid();
        var provisional = new DatabaseRestorePreview(
            planId,
            string.Empty,
            serverProfile.Id,
            ServerEndpoint(serverProfile),
            normalized,
            DatabaseEngineKind.SqlServer,
            DatabaseBackupFormat.SqlServerNative,
            binding.Manifest.BackupPath.Value,
            binding.Manifest.Verification.Sha256,
            binding.Manifest.Verification.SizeBytes,
            binding.Manifest.ToolName,
            binding.Manifest.ToolVersion,
            "sqlcmd",
            toolVersion.Version!,
            ManifestFingerprint(binding.Manifest),
            target.Snapshot,
            TargetFingerprint(target.Snapshot),
            command.Executable,
            command.Arguments,
            command.UsesSensitiveInput,
            OperationRisk.Destructive,
            DisplayCommand(binding.Profile, binding.Manifest, normalized.TargetDatabase),
            $"Destructive SQL Server native restore will replace the exact database '{normalized.TargetDatabase}' from the checksum-verified .bak file. Existing transactions are rolled back and no automatic rollback is claimed. Ambiguous completion must be re-inspected before any next action.",
            RollbackAvailable: false);
        var fingerprint = PreviewFingerprint(provisional);
        var preview = provisional with { Fingerprint = fingerprint };
        _capabilities[planId] = fingerprint;
        return new DatabaseRestorePreviewResult(preview, null);
    }

    public async Task<DatabaseRestoreResult> ExecuteAsync(
        ServerProfile serverProfile,
        DatabaseRestorePreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serverProfile);
        ArgumentNullException.ThrowIfNull(preview);
        var actualFingerprint = PreviewFingerprint(preview with { Fingerprint = string.Empty });
        if (!FixedEquals(preview.Fingerprint, actualFingerprint) ||
            !_capabilities.TryRemove(preview.PlanId, out var expectedFingerprint) ||
            !string.Equals(expectedFingerprint, preview.Fingerprint, StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict, "SQL Server restore preview is stale, replayed or modified. Preview the exact live target again.");
        }

        if (preview.Engine != DatabaseEngineKind.SqlServer ||
            preview.BackupFormat != DatabaseBackupFormat.SqlServerNative ||
            serverProfile.Id != preview.ServerProfileId ||
            !string.Equals(ServerEndpoint(serverProfile), preview.ServerEndpoint, StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict, "SQL Server restore server/engine identity changed after Preview.");
        }

        var binding = await LoadBindingAsync(serverProfile, preview.Request, cancellationToken).ConfigureAwait(false);
        if (binding.Error is not null || binding.Profile is null || binding.Manifest is null)
        {
            return Failure(binding.Error ?? new RemoteError(RemoteErrorCode.PathConflict, "SQL Server restore binding could not be revalidated."));
        }

        if (!string.Equals(ManifestFingerprint(binding.Manifest), preview.ManifestFingerprint, StringComparison.Ordinal) ||
            !string.Equals(binding.Manifest.BackupPath.Value, preview.BackupPath, StringComparison.Ordinal) ||
            !string.Equals(binding.Manifest.Verification.Sha256, preview.BackupSha256, StringComparison.OrdinalIgnoreCase) ||
            binding.Manifest.Verification.SizeBytes != preview.BackupSizeBytes)
        {
            return Failure(RemoteErrorCode.PathConflict, "The verified SQL Server backup manifest changed after Preview.");
        }

        var secret = await ResolveSecretAsync(binding.Profile, cancellationToken).ConfigureAwait(false);
        if (secret.Error is not null)
        {
            return Failure(secret.Error);
        }

        await using var executor = _commands.Create(serverProfile);
        var backupError = await InspectBackupAsync(executor, binding.Profile, binding.Manifest, secret.Secret, cancellationToken)
            .ConfigureAwait(false);
        if (backupError is not null)
        {
            return Failure(RemoteErrorCode.PathConflict, "The SQL Server backup artifact no longer matches its verified manifest. No restore command was sent.");
        }

        var toolVersion = await ReadSqlCmdVersionAsync(executor, cancellationToken).ConfigureAwait(false);
        if (toolVersion.Error is not null || !string.Equals(toolVersion.Version, preview.RestoreToolVersion, StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict, "sqlcmd capability/version changed after Preview. Preview again before restoring.");
        }

        var before = await InspectTargetAsync(
                executor,
                binding.Profile,
                preview.Request.TargetDatabase,
                secret.Secret,
                cancellationToken)
            .ConfigureAwait(false);
        if (before.Error is not null || before.Snapshot is null ||
            !string.Equals(TargetFingerprint(before.Snapshot), preview.TargetFingerprint, StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict, "The exact SQL Server target changed after Preview. No destructive command was sent.");
        }

        if (DatabaseCertificationMatrix.LevelFor(
                DatabaseEngineKind.SqlServer,
                before.Snapshot.ServerVersion,
                DatabaseCapabilityKind.Restore) != DatabaseCertificationLevel.Certified)
        {
            return Failure(RemoteErrorCode.CapabilityUnavailable, "The SQL Server version is no longer within the certified restore matrix.");
        }

        var plan = BuildRestorePlan(binding.Profile, binding.Manifest, preview.Request.TargetDatabase, secret.Secret is not null);
        if (!string.Equals(plan.Executable, preview.Executable, StringComparison.Ordinal) ||
            !plan.Arguments.SequenceEqual(preview.Arguments, StringComparer.Ordinal) ||
            plan.UsesSensitiveInput != preview.UsesSensitiveInput || preview.Risk != OperationRisk.Destructive)
        {
            return Failure(RemoteErrorCode.PathConflict, "The SQL Server restore command no longer matches the previewed exact target and backup identity.");
        }

        try
        {
            var execution = await executor.ExecuteAsync(
                    BuildExecutionSpec(plan, secret.Secret, _options.CommandTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (execution.Error is not null)
            {
                return SqlServerDatabaseBackupService.IsAmbiguous(execution.Error.Code)
                    ? Ambiguous("SQL Server restore completion is unknown after transport/timeout/cancellation failure. Do not retry before exact target inspection.")
                    : await FailedAfterDispatchAsync(executor, binding.Profile, preview.Request.TargetDatabase, secret.Secret).ConfigureAwait(false);
            }

            if (execution.Command!.ExitCode != 0)
            {
                return await FailedAfterDispatchAsync(executor, binding.Profile, preview.Request.TargetDatabase, secret.Secret).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return Ambiguous("SQL Server restore was cancelled after destructive dispatch began. Completion is unknown; do not blindly retry.");
        }

        TargetRead after;
        try
        {
            after = await InspectTargetAsync(
                    executor,
                    binding.Profile,
                    preview.Request.TargetDatabase,
                    secret.Secret,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Ambiguous("RESTORE DATABASE returned success, but post-restore verification was cancelled. Do not retry before exact target inspection.");
        }

        if (after.Error is not null || after.Snapshot is null)
        {
            return Ambiguous("RESTORE DATABASE returned success, but the exact SQL Server target could not be post-verified.");
        }

        var stableIdentity = string.Equals(after.Snapshot.DatabaseName, preview.Request.TargetDatabase, StringComparison.Ordinal) &&
            string.Equals(after.Snapshot.ConnectionIdentity, preview.TargetBefore.ConnectionIdentity, StringComparison.Ordinal) &&
            string.Equals(after.Snapshot.ServerVersion, preview.TargetBefore.ServerVersion, StringComparison.Ordinal);
        if (!stableIdentity)
        {
            return new DatabaseRestoreResult(
                false,
                true,
                false,
                "SQL Server restore returned success, but post-state identity differs from the previewed exact target.",
                new RemoteError(RemoteErrorCode.AmbiguousState, "SQL Server post-restore target identity verification failed."),
                after.Snapshot);
        }

        return new DatabaseRestoreResult(
            true,
            false,
            false,
            "SQL Server restore completed and the exact target identity was post-verified. No automatic rollback is claimed.",
            null,
            after.Snapshot);
    }

    private async Task<BindingRead> LoadBindingAsync(
        ServerProfile serverProfile,
        DatabaseRestoreRequest request,
        CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetAsync(request.DatabaseProfileId, cancellationToken).ConfigureAwait(false);
        var manifest = await _manifests.GetAsync(request.BackupId, cancellationToken).ConfigureAwait(false);
        if (profile is null || manifest is null)
        {
            return BindingRead.Fail(RemoteErrorCode.PathNotFound, "SQL Server database profile or verified backup manifest no longer exists.");
        }

        if (profile.Engine != DatabaseEngineKind.SqlServer || manifest.Engine != DatabaseEngineKind.SqlServer)
        {
            return BindingRead.UnsupportedMode("The SQL Server restore service refuses non-SQL-Server bindings.");
        }

        if (!manifest.IsVerified || manifest.Format != DatabaseBackupFormat.SqlServerNative ||
            manifest.Verification.SizeBytes <= 0 || manifest.Verification.Sha256.Length != 64)
        {
            return BindingRead.Fail(RemoteErrorCode.PathConflict, "Only verified SQL Server native backup manifests may be restored.");
        }

        if (profile.ServerProfileId != serverProfile.Id || manifest.ServerProfileId != serverProfile.Id ||
            manifest.DatabaseProfileId != profile.Id ||
            string.IsNullOrWhiteSpace(profile.DatabaseName) ||
            !string.Equals(profile.DatabaseName, request.TargetDatabase, StringComparison.Ordinal) ||
            !string.Equals(manifest.DatabaseName, request.TargetDatabase, StringComparison.Ordinal))
        {
            return BindingRead.Fail(RemoteErrorCode.PathConflict, "SQL Server restore requires exact server/profile/database/backup identity equality.");
        }

        return BindingRead.Success(profile, manifest);
    }

    private async Task<RemoteError?> InspectBackupAsync(
        IRemoteCommandExecutor executor,
        DatabaseConnectionProfile profile,
        DatabaseBackupManifest manifest,
        string? secret,
        CancellationToken cancellationToken)
    {
        var stat = await ExecuteReadAsync(
                executor,
                new RemoteCommandSpec(
                    "stat",
                    ["--printf=%F\t%s", "--", manifest.BackupPath.Value],
                    _options.InspectionTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (stat.Error is not null)
        {
            return stat.Error;
        }

        var fields = stat.Output!.Trim().Split('\t');
        if (fields.Length != 2 || !string.Equals(fields[0].Trim(), "regular file", StringComparison.Ordinal) ||
            !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size != manifest.Verification.SizeBytes)
        {
            return new RemoteError(RemoteErrorCode.PathConflict, "SQL Server backup file type/size no longer matches the verified manifest.");
        }

        var checksum = await ExecuteReadAsync(
                executor,
                new RemoteCommandSpec(
                    "sha256sum",
                    ["--", manifest.BackupPath.Value],
                    _options.InspectionTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (checksum.Error is not null)
        {
            return checksum.Error;
        }

        var actualSha = checksum.Output!
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (!string.Equals(actualSha, manifest.Verification.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteError(RemoteErrorCode.PathConflict, "SQL Server backup checksum no longer matches the verified manifest.");
        }

        var verify = await executor.ExecuteAsync(
            BuildSqlCmd(
                profile,
                "master",
                $"RESTORE VERIFYONLY FROM DISK = {SqlServerDatabaseBackupService.SqlString(manifest.BackupPath.Value)} WITH CHECKSUM;",
                OperationRisk.ReadOnly,
                secret,
                _options.InspectionTimeout),
            cancellationToken).ConfigureAwait(false);
        if (verify.Error is not null || verify.Command is null || verify.Command.ExitCode != 0)
        {
            return new RemoteError(RemoteErrorCode.PathConflict, "SQL Server backup no longer passes RESTORE VERIFYONLY.");
        }

        return null;
    }

    private async Task<TargetRead> InspectTargetAsync(
        IRemoteCommandExecutor executor,
        DatabaseConnectionProfile profile,
        string database,
        string? secret,
        CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteAsync(
            BuildSqlCmd(profile, database, TargetQuery, OperationRisk.ReadOnly, secret, _options.InspectionTimeout, compactOutput: true),
            cancellationToken).ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return new TargetRead(null, execution.Error);
        }

        if (execution.Command!.ExitCode != 0)
        {
            return new TargetRead(null, SqlServerDatabaseBackupService.ClassifySqlFailure(execution.Command.StandardError + "\n" + execution.Command.StandardOutput));
        }

        if (execution.Command.StandardOutput.Length > _options.MaximumDiagnosticCharacters)
        {
            return new TargetRead(null, new RemoteError(RemoteErrorCode.CapabilityUnavailable, "SQL Server target inspection output exceeded the configured safety bound."));
        }

        var fields = execution.Command.StandardOutput
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (fields.Length != 4 ||
            !long.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var objects))
        {
            return new TargetRead(null, new RemoteError(RemoteErrorCode.ParseFailed, "SQL Server restore target inspection returned an unrecognized identity payload."));
        }

        var databaseName = fields[0];
        var identity = fields[1];
        var version = fields[2];
        if (!string.Equals(databaseName, database, StringComparison.Ordinal) ||
            identity.Length is 0 or > 128 ||
            version.Length is 0 or > 128 ||
            objects < 0)
        {
            return new TargetRead(null, new RemoteError(RemoteErrorCode.PathConflict, "SQL Server reported a restore target identity different from the explicit target."));
        }

        return new TargetRead(new DatabaseRestoreTargetSnapshot(databaseName, Bound(identity), Bound(version), objects), null);
    }

    private CommandPlan BuildRestorePlan(
        DatabaseConnectionProfile profile,
        DatabaseBackupManifest manifest,
        string database,
        bool hasSecret)
    {
        var query =
            $"ALTER DATABASE {SqlServerDatabaseBackupService.SqlIdentifier(database)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE; " +
            $"RESTORE DATABASE {SqlServerDatabaseBackupService.SqlIdentifier(database)} FROM DISK = {SqlServerDatabaseBackupService.SqlString(manifest.BackupPath.Value)} WITH REPLACE, CHECKSUM, RECOVERY; " +
            $"ALTER DATABASE {SqlServerDatabaseBackupService.SqlIdentifier(database)} SET MULTI_USER;";
        var arguments = SqlCmdArguments(profile, "master", query, compactOutput: false);
        if (!hasSecret)
        {
            return new CommandPlan("sqlcmd", arguments, false);
        }

        var shell = new List<string> { "-c", SqlCmdSecretScript, "serverdesk-sqlserver-restore", "sqlcmd" };
        shell.AddRange(arguments);
        return new CommandPlan("sh", shell, true);
    }

    private RemoteCommandSpec BuildExecutionSpec(CommandPlan plan, string? secret, TimeSpan timeout)
    {
        SensitiveCommandInput? input = null;
        if (plan.UsesSensitiveInput)
        {
            if (secret is null)
            {
                throw new InvalidOperationException("A sensitive SQL Server restore command requires a database secret.");
            }

            input = new SensitiveCommandInput(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)) + "\n");
        }

        return new RemoteCommandSpec(plan.Executable, plan.Arguments, timeout, OperationRisk.Destructive, StableEnvironment, StandardInput: input);
    }

    private RemoteCommandSpec BuildSqlCmd(
        DatabaseConnectionProfile profile,
        string database,
        string query,
        OperationRisk risk,
        string? secret,
        TimeSpan timeout,
        bool compactOutput = false)
    {
        var arguments = SqlCmdArguments(profile, database, query, compactOutput);
        if (secret is null)
        {
            return new RemoteCommandSpec("sqlcmd", arguments, timeout, risk, StableEnvironment);
        }

        var shell = new List<string> { "-c", SqlCmdSecretScript, "serverdesk-sqlserver-inspect", "sqlcmd" };
        shell.AddRange(arguments);
        return new RemoteCommandSpec(
            "sh",
            shell,
            timeout,
            risk,
            StableEnvironment,
            StandardInput: new SensitiveCommandInput(Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)) + "\n"));
    }

    private static IReadOnlyList<string> SqlCmdArguments(
        DatabaseConnectionProfile profile,
        string database,
        string query,
        bool compactOutput)
    {
        var arguments = new List<string>
        {
            "-S", $"tcp:{profile.RemoteHost},{profile.RemotePort.ToString(CultureInfo.InvariantCulture)}",
            "-d", database,
            "-C",
            "-b",
            "-r", "1",
        };
        if (profile.AuthenticationKind == DatabaseAuthenticationKind.Password)
        {
            arguments.AddRange(["-U", profile.Username ?? string.Empty]);
        }
        else
        {
            arguments.Add("-E");
        }

        if (compactOutput)
        {
            arguments.AddRange(["-h", "-1", "-W"]);
        }

        arguments.AddRange(["-Q", query]);
        return arguments;
    }

    private async Task<VersionRead> ReadSqlCmdVersionAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
            new RemoteCommandSpec("sqlcmd", ["-?"], _options.InspectionTimeout, OperationRisk.ReadOnly, StableEnvironment),
            cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new VersionRead(null, result.Error);
        }

        var value = SqlServerDatabaseBackupService.FirstUsefulLine(result.Command!.StandardOutput, result.Command.StandardError);
        return result.Command.ExitCode == 0 && !string.IsNullOrWhiteSpace(value)
            ? new VersionRead(Bound(value), null)
            : new VersionRead(null, new RemoteError(RemoteErrorCode.CapabilityUnavailable, "sqlcmd client tooling is unavailable."));
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
            return new SecretRead(null, new RemoteError(RemoteErrorCode.AuthenticationFailed, "The SQL Server profile has no credential reference."));
        }

        try
        {
            var value = await _secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(value) || value.Contains('\0')
                ? new SecretRead(null, new RemoteError(RemoteErrorCode.AuthenticationFailed, "The stored SQL Server credential is unavailable or unsafe."))
                : new SecretRead(value, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new SecretRead(null, new RemoteError(RemoteErrorCode.AuthenticationFailed, "The stored SQL Server credential could not be read securely."));
        }
    }

    private async Task<DatabaseRestoreResult> FailedAfterDispatchAsync(
        IRemoteCommandExecutor executor,
        DatabaseConnectionProfile profile,
        string database,
        string? secret)
    {
        DatabaseRestoreTargetSnapshot? observed = null;
        try
        {
            observed = (await InspectTargetAsync(executor, profile, database, secret, CancellationToken.None).ConfigureAwait(false)).Snapshot;
        }
        catch
        {
        }

        const string message = "SQL Server restore reported failure after destructive dispatch. Partial database changes cannot be ruled out; no automatic retry or rollback is claimed.";
        return new DatabaseRestoreResult(
            false,
            true,
            false,
            message,
            new RemoteError(RemoteErrorCode.AmbiguousState, message),
            observed);
    }

    private async Task<ReadResult> ExecuteReadAsync(
        IRemoteCommandExecutor executor,
        RemoteCommandSpec command,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(command, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new ReadResult(null, result.Error);
        }

        if (result.Command!.StandardOutput.Length > _options.MaximumDiagnosticCharacters ||
            result.Command.StandardError.Length > _options.MaximumDiagnosticCharacters)
        {
            return new ReadResult(null, new RemoteError(RemoteErrorCode.CapabilityUnavailable, "SQL Server restore inspection output exceeded the configured safety bound."));
        }

        return result.Command.ExitCode == 0
            ? new ReadResult(result.Command.StandardOutput, null)
            : new ReadResult(null, SqlServerDatabaseBackupService.ClassifySqlFailure(result.Command.StandardError + "\n" + result.Command.StandardOutput));
    }

    private static DatabaseRestoreRequest NormalizeRequest(DatabaseRestoreRequest request)
    {
        if (request.DatabaseProfileId == Guid.Empty || request.BackupId == Guid.Empty)
        {
            throw new ArgumentException("SQL Server restore requires non-empty database profile and verified backup ids.", nameof(request));
        }

        var database = request.TargetDatabase ?? string.Empty;
        if (string.IsNullOrWhiteSpace(database) || !string.Equals(database, database.Trim(), StringComparison.Ordinal) ||
            database.Length > 128 || database.Any(char.IsControl))
        {
            throw new ArgumentException("SQL Server restore target database must be 1-128 printable characters without leading/trailing whitespace.", nameof(request));
        }

        return request with { TargetDatabase = database };
    }

    private string Bound(string value) => value.Length <= _options.MaximumDiagnosticCharacters
        ? value
        : value[.._options.MaximumDiagnosticCharacters];

    private static string DisplayCommand(
        DatabaseConnectionProfile profile,
        DatabaseBackupManifest manifest,
        string database) =>
        $"sqlcmd -S tcp:{Token(profile.RemoteHost)},{profile.RemotePort.ToString(CultureInfo.InvariantCulture)} -d master -C -b -Q \"ALTER DATABASE {SqlServerDatabaseBackupService.SqlIdentifier(database)} ...; RESTORE DATABASE {SqlServerDatabaseBackupService.SqlIdentifier(database)} FROM DISK = {SqlServerDatabaseBackupService.SqlString(manifest.BackupPath.Value)} WITH REPLACE, CHECKSUM, RECOVERY; ...\" [credential via sensitive stdin when configured]";

    private static string Token(string value) =>
        value.Length > 0 && value.All(character => char.IsLetterOrDigit(character) || "-._/:=@".Contains(character))
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string ManifestFingerprint(DatabaseBackupManifest manifest) => Hash(
        string.Join("\u001f",
            manifest.BackupId,
            manifest.ServerProfileId,
            manifest.DatabaseProfileId,
            manifest.Engine,
            manifest.DatabaseName,
            manifest.BackupPath.Value,
            manifest.Format,
            manifest.ToolName,
            manifest.ToolVersion,
            manifest.Verification.SizeBytes.ToString(CultureInfo.InvariantCulture),
            manifest.Verification.Sha256.ToUpperInvariant(),
            manifest.Verification.StructuralCheck,
            manifest.Verification.VerifiedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            manifest.IsVerified));

    private static string TargetFingerprint(DatabaseRestoreTargetSnapshot snapshot) => Hash(
        string.Join("\u001f",
            snapshot.DatabaseName,
            snapshot.ConnectionIdentity,
            snapshot.ServerVersion,
            snapshot.UserObjectCount.ToString(CultureInfo.InvariantCulture)));

    private static string PreviewFingerprint(DatabaseRestorePreview preview) => Hash(
        string.Join("\u001e",
            preview.PlanId,
            preview.ServerProfileId,
            preview.ServerEndpoint,
            preview.Request.DatabaseProfileId,
            preview.Request.BackupId,
            preview.Request.TargetDatabase,
            preview.Engine,
            preview.BackupFormat,
            preview.BackupPath,
            preview.BackupSha256,
            preview.BackupSizeBytes.ToString(CultureInfo.InvariantCulture),
            preview.BackupTool,
            preview.BackupToolVersion,
            preview.RestoreTool,
            preview.RestoreToolVersion,
            preview.ManifestFingerprint,
            preview.TargetFingerprint,
            preview.Executable,
            string.Join("\u001f", preview.Arguments),
            preview.UsesSensitiveInput,
            preview.Risk,
            preview.DisplayCommand,
            preview.DataLossWarning,
            preview.RollbackAvailable));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left ?? string.Empty),
            Encoding.UTF8.GetBytes(right ?? string.Empty));

    private static string ServerEndpoint(ServerProfile profile) =>
        $"{profile.Username}@{profile.Host}:{profile.Port.ToString(CultureInfo.InvariantCulture)}";

    private static DatabaseRestorePreviewResult PreviewFailure(RemoteErrorCode code, string message) =>
        new(null, new RemoteError(code, message));

    private static DatabaseRestoreResult Failure(RemoteErrorCode code, string message) =>
        new(false, false, false, message, new RemoteError(code, message));

    private static DatabaseRestoreResult Failure(RemoteError error) =>
        new(false, false, false, error.Message, error);

    private static DatabaseRestoreResult Ambiguous(string message) =>
        new(false, true, false, message, new RemoteError(RemoteErrorCode.AmbiguousState, message));

    private sealed record BindingRead(
        DatabaseConnectionProfile? Profile,
        DatabaseBackupManifest? Manifest,
        RemoteError? Error,
        bool Unsupported)
    {
        public static BindingRead Success(DatabaseConnectionProfile profile, DatabaseBackupManifest manifest) =>
            new(profile, manifest, null, false);

        public static BindingRead Fail(RemoteErrorCode code, string message) =>
            new(null, null, new RemoteError(code, message), false);

        public static BindingRead UnsupportedMode(string message) =>
            new(null, null, new RemoteError(RemoteErrorCode.CapabilityUnavailable, message), true);
    }

    private sealed record CommandPlan(string Executable, IReadOnlyList<string> Arguments, bool UsesSensitiveInput);
    private sealed record SecretRead(string? Secret, RemoteError? Error);
    private sealed record VersionRead(string? Version, RemoteError? Error);
    private sealed record TargetRead(DatabaseRestoreTargetSnapshot? Snapshot, RemoteError? Error);
    private sealed record ReadResult(string? Output, RemoteError? Error);
}
