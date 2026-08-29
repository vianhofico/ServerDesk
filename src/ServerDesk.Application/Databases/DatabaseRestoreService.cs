using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Databases;

public sealed class DatabaseRestoreService : IDatabaseRestoreService
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

    private const string MySqlRestoreSecretScript =
        "IFS= read -r SERVERDESK_SECRET_B64; " +
        "SERVERDESK_SECRET=$(printf '%s' \"$SERVERDESK_SECRET_B64\" | base64 --decode; printf x); " +
        "MYSQL_PWD=${SERVERDESK_SECRET%x}; " +
        "unset SERVERDESK_SECRET SERVERDESK_SECRET_B64; " +
        "SERVERDESK_BACKUP=$1; shift; export MYSQL_PWD; exec \"$@\" < \"$SERVERDESK_BACKUP\"";

    private const string MySqlRestoreNoSecretScript =
        "SERVERDESK_BACKUP=$1; shift; exec \"$@\" < \"$SERVERDESK_BACKUP\"";

    private const string PostgreSqlTargetQuery =
        "SELECT json_build_object(" +
        "'database', current_database(), " +
        "'identity', current_user, " +
        "'version', current_setting('server_version'), " +
        "'objects', (SELECT count(*) FROM pg_catalog.pg_class c " +
        "JOIN pg_catalog.pg_namespace n ON n.oid=c.relnamespace " +
        "WHERE c.relkind IN ('r','p','v','m','S','f') " +
        "AND n.nspname NOT IN ('pg_catalog','information_schema') " +
        "AND n.nspname !~ '^pg_toast'))::text;";

    private const string MySqlTargetQuery =
        "SELECT JSON_OBJECT(" +
        "'database', DATABASE(), " +
        "'identity', CURRENT_USER(), " +
        "'version', VERSION(), " +
        "'objects', (SELECT COUNT(*) FROM information_schema.tables WHERE table_schema=DATABASE()));";

    private readonly IDatabaseProfileRepository _profiles;
    private readonly IDatabaseBackupManifestRepository _manifests;
    private readonly ISecretStore _secrets;
    private readonly IRemoteCommandExecutorFactory _commands;
    private readonly DatabaseRestoreOptions _options;
    private readonly ConcurrentDictionary<Guid, string> _capabilities = new();

    public DatabaseRestoreService(
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
        if (binding.Error is not null)
        {
            return new DatabaseRestorePreviewResult(null, binding.Error, binding.Unsupported);
        }

        var profile = binding.Profile!;
        var manifest = binding.Manifest!;
        var secret = await ResolveSecretAsync(profile, cancellationToken).ConfigureAwait(false);
        if (secret.Error is not null)
        {
            return new DatabaseRestorePreviewResult(null, secret.Error);
        }

        await using var executor = _commands.Create(serverProfile);
        var backup = await InspectBackupAsync(executor, manifest, cancellationToken).ConfigureAwait(false);
        if (backup.Error is not null)
        {
            return new DatabaseRestorePreviewResult(null, backup.Error);
        }

        var tool = RestoreToolFor(profile.Engine);
        var version = await InspectToolVersionAsync(executor, tool, cancellationToken).ConfigureAwait(false);
        if (version.Error is not null)
        {
            return new DatabaseRestorePreviewResult(null, version.Error);
        }

        var target = await InspectTargetAsync(
                executor,
                profile,
                normalized.TargetDatabase,
                secret.Secret,
                cancellationToken)
            .ConfigureAwait(false);
        if (target.Error is not null || target.Snapshot is null)
        {
            return new DatabaseRestorePreviewResult(null, target.Error ?? new RemoteError(
                RemoteErrorCode.ParseFailed,
                "Database restore target identity could not be inspected."));
        }

        var command = BuildRestoreCommand(profile, manifest, normalized.TargetDatabase, secret.Secret is not null);
        var planId = Guid.NewGuid();
        var provisional = new DatabaseRestorePreview(
            planId,
            string.Empty,
            serverProfile.Id,
            ServerEndpoint(serverProfile),
            normalized,
            profile.Engine,
            manifest.Format,
            manifest.BackupPath.Value,
            manifest.Verification.Sha256,
            manifest.Verification.SizeBytes,
            manifest.ToolName,
            manifest.ToolVersion,
            tool,
            version.Version!,
            ManifestFingerprint(manifest),
            target.Snapshot,
            TargetFingerprint(target.Snapshot),
            command.Executable,
            command.Arguments,
            command.UsesSensitiveInput,
            OperationRisk.Destructive,
            DisplayCommand(profile, manifest, normalized.TargetDatabase),
            DataLossWarning(profile.Engine, normalized.TargetDatabase),
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
            return Failure(
                RemoteErrorCode.PathConflict,
                "Database restore preview is stale, replayed or modified. Preview the exact live target again before restoring.");
        }

        if (serverProfile.Id != preview.ServerProfileId ||
            !string.Equals(ServerEndpoint(serverProfile), preview.ServerEndpoint, StringComparison.Ordinal))
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "The server identity changed after Preview. Create a new restore preview before any destructive action.");
        }

        var binding = await LoadBindingAsync(serverProfile, preview.Request, cancellationToken).ConfigureAwait(false);
        if (binding.Error is not null || binding.Profile is null || binding.Manifest is null)
        {
            return Failure(binding.Error ?? new RemoteError(
                RemoteErrorCode.PathConflict,
                "The database restore binding could not be revalidated."));
        }

        var profile = binding.Profile;
        var manifest = binding.Manifest;
        if (!string.Equals(ManifestFingerprint(manifest), preview.ManifestFingerprint, StringComparison.Ordinal) ||
            profile.Engine != preview.Engine || manifest.Format != preview.BackupFormat ||
            !string.Equals(manifest.BackupPath.Value, preview.BackupPath, StringComparison.Ordinal) ||
            !string.Equals(manifest.Verification.Sha256, preview.BackupSha256, StringComparison.OrdinalIgnoreCase) ||
            manifest.Verification.SizeBytes != preview.BackupSizeBytes)
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "The verified backup manifest changed after Preview. Refresh history and preview again.");
        }

        var secret = await ResolveSecretAsync(profile, cancellationToken).ConfigureAwait(false);
        if (secret.Error is not null)
        {
            return Failure(secret.Error);
        }

        await using var executor = _commands.Create(serverProfile);
        var backup = await InspectBackupAsync(executor, manifest, cancellationToken).ConfigureAwait(false);
        if (backup.Error is not null)
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "The backup artifact no longer matches its verified manifest. No restore command was sent.");
        }

        var version = await InspectToolVersionAsync(executor, preview.RestoreTool, cancellationToken).ConfigureAwait(false);
        if (version.Error is not null || !string.Equals(version.Version, preview.RestoreToolVersion, StringComparison.Ordinal))
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "The restore tool capability/version changed after Preview. Preview again before restoring.");
        }

        var targetBefore = await InspectTargetAsync(
                executor,
                profile,
                preview.Request.TargetDatabase,
                secret.Secret,
                cancellationToken)
            .ConfigureAwait(false);
        if (targetBefore.Error is not null || targetBefore.Snapshot is null ||
            !string.Equals(TargetFingerprint(targetBefore.Snapshot), preview.TargetFingerprint, StringComparison.Ordinal))
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "The exact database target changed after Preview. No restore command was sent.");
        }

        var command = BuildRestoreCommand(profile, manifest, preview.Request.TargetDatabase, secret.Secret is not null);
        if (!string.Equals(command.Executable, preview.Executable, StringComparison.Ordinal) ||
            !command.Arguments.SequenceEqual(preview.Arguments, StringComparer.Ordinal) ||
            command.UsesSensitiveInput != preview.UsesSensitiveInput ||
            preview.Risk != OperationRisk.Destructive)
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "The restore command no longer matches the previewed exact target and backup identity.");
        }

        var mutationStarted = false;
        try
        {
            mutationStarted = true;
            var execution = await executor.ExecuteAsync(
                    BuildExecutionSpec(command, secret.Secret),
                    cancellationToken)
                .ConfigureAwait(false);

            if (execution.Error is not null)
            {
                if (IsAmbiguousTransport(execution.Error.Code))
                {
                    return Ambiguous(
                        "Database restore completion is unknown after transport/timeout/cancellation failure. Do not retry. Re-inspect the exact target and verified backup first.",
                        execution.Error.TechnicalDetails);
                }

                return await VerifyFailedDispatchAsync(
                        executor,
                        profile,
                        preview.Request.TargetDatabase,
                        secret.Secret,
                        "The restore command reported a failure after destructive dispatch. Partial database changes cannot be ruled out; no automatic retry or rollback is claimed.")
                    .ConfigureAwait(false);
            }

            if (execution.Command!.ExitCode != 0)
            {
                return await VerifyFailedDispatchAsync(
                        executor,
                        profile,
                        preview.Request.TargetDatabase,
                        secret.Secret,
                        "The restore tool exited with failure after destructive dispatch. Partial database changes cannot be ruled out; inspect the exact target before any next action.")
                    .ConfigureAwait(false);
            }

            return await VerifySuccessAsync(
                    executor,
                    profile,
                    preview,
                    secret.Secret,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (mutationStarted)
        {
            return Ambiguous(
                "Database restore was cancelled after destructive dispatch began. Completion is unknown; do not retry until the exact target is re-inspected.");
        }
    }

    private async Task<DatabaseRestoreResult> VerifySuccessAsync(
        IRemoteCommandExecutor executor,
        DatabaseConnectionProfile profile,
        DatabaseRestorePreview preview,
        string? secret,
        CancellationToken cancellationToken)
    {
        TargetRead after;
        try
        {
            after = await InspectTargetAsync(
                    executor,
                    profile,
                    preview.Request.TargetDatabase,
                    secret,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Ambiguous(
                "The restore command returned success, but post-restore verification was cancelled. Do not retry; inspect the exact target first.");
        }

        if (after.Error is not null || after.Snapshot is null)
        {
            return Ambiguous(
                "The restore command returned success, but ServerDesk could not read the exact target for post-restore verification. No retry or rollback is claimed.");
        }

        var snapshot = after.Snapshot;
        var stableIdentityMatches =
            string.Equals(snapshot.DatabaseName, preview.Request.TargetDatabase, StringComparison.Ordinal) &&
            string.Equals(snapshot.ConnectionIdentity, preview.TargetBefore.ConnectionIdentity, StringComparison.Ordinal) &&
            string.Equals(snapshot.ServerVersion, preview.TargetBefore.ServerVersion, StringComparison.Ordinal);
        if (!stableIdentityMatches)
        {
            return new DatabaseRestoreResult(
                false,
                true,
                false,
                "Restore returned success, but post-state identity no longer matches the previewed exact database target. Treat completion as ambiguous.",
                new RemoteError(RemoteErrorCode.AmbiguousState, "Post-restore target identity verification failed."),
                snapshot);
        }

        return new DatabaseRestoreResult(
            true,
            false,
            false,
            "Database restore completed and the exact target identity was post-verified. ServerDesk does not claim byte-for-byte semantic equivalence or an automatic rollback.",
            null,
            snapshot);
    }

    private async Task<DatabaseRestoreResult> VerifyFailedDispatchAsync(
        IRemoteCommandExecutor executor,
        DatabaseConnectionProfile profile,
        string targetDatabase,
        string? secret,
        string message)
    {
        DatabaseRestoreTargetSnapshot? observed = null;
        try
        {
            var read = await InspectTargetAsync(
                    executor,
                    profile,
                    targetDatabase,
                    secret,
                    CancellationToken.None)
                .ConfigureAwait(false);
            observed = read.Snapshot;
        }
        catch
        {
            // The destructive command already ran; preserve the ambiguity result instead of masking it.
        }

        return new DatabaseRestoreResult(
            false,
            true,
            false,
            message,
            new RemoteError(RemoteErrorCode.AmbiguousState, message),
            observed);
    }

    private async Task<BindingRead> LoadBindingAsync(
        ServerProfile serverProfile,
        DatabaseRestoreRequest request,
        CancellationToken cancellationToken)
    {
        var profile = await _profiles.GetAsync(request.DatabaseProfileId, cancellationToken).ConfigureAwait(false);
        if (profile is null)
        {
            return BindingRead.Fail(RemoteErrorCode.PathNotFound, "The selected database profile no longer exists.");
        }

        var manifest = await _manifests.GetAsync(request.BackupId, cancellationToken).ConfigureAwait(false);
        if (manifest is null)
        {
            return BindingRead.Fail(RemoteErrorCode.PathNotFound, "The selected verified backup manifest no longer exists.");
        }

        if (!manifest.IsVerified || manifest.Verification.SizeBytes <= 0 || manifest.Verification.Sha256.Length != 64)
        {
            return BindingRead.Fail(RemoteErrorCode.PathConflict, "Only fully verified backup manifests may be restored.");
        }

        if (profile.ServerProfileId != serverProfile.Id || manifest.ServerProfileId != serverProfile.Id ||
            manifest.DatabaseProfileId != profile.Id)
        {
            return BindingRead.Fail(RemoteErrorCode.PathConflict, "Server, database profile and backup manifest identities do not match.");
        }

        if (profile.Engine != manifest.Engine)
        {
            return BindingRead.Fail(RemoteErrorCode.PathConflict, "Backup engine does not match the selected database profile.");
        }

        if (profile.Engine == DatabaseEngineKind.Redis)
        {
            return BindingRead.UnsupportedMode(
                "Redis restore is not certified because ServerDesk cannot yet prove deterministic safe recovery semantics. No restore command will be generated.");
        }

        if (!CompatibleFormat(profile.Engine, manifest.Format))
        {
            return BindingRead.Fail(RemoteErrorCode.PathConflict, "Backup format is not compatible with the selected database profile.");
        }

        if (profile.Engine is not DatabaseEngineKind.PostgreSql and
            not DatabaseEngineKind.MySql and
            not DatabaseEngineKind.MariaDb)
        {
            return BindingRead.UnsupportedMode($"Restore is not supported for {profile.Engine}.");
        }

        if (string.IsNullOrWhiteSpace(profile.DatabaseName) ||
            !string.Equals(profile.DatabaseName, request.TargetDatabase, StringComparison.Ordinal) ||
            !string.Equals(manifest.DatabaseName, request.TargetDatabase, StringComparison.Ordinal))
        {
            return BindingRead.Fail(
                RemoteErrorCode.PathConflict,
                "M6.5 restores only to the exact saved database identity captured by the verified manifest; alternate or implicit targets are refused.");
        }

        return BindingRead.Success(profile, manifest);
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
            return new SecretRead(null, new RemoteError(
                RemoteErrorCode.AuthenticationFailed,
                "The database profile has no credential reference."));
        }

        try
        {
            var secret = await _secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(secret))
            {
                return new SecretRead(null, new RemoteError(
                    RemoteErrorCode.AuthenticationFailed,
                    "The stored database credential is unavailable."));
            }

            if (secret.Contains('\0'))
            {
                return new SecretRead(null, new RemoteError(
                    RemoteErrorCode.CapabilityUnavailable,
                    "The stored database credential contains data that cannot be represented safely for restore."));
            }

            return new SecretRead(secret, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new SecretRead(null, new RemoteError(
                RemoteErrorCode.AuthenticationFailed,
                "The stored database credential could not be read securely."));
        }
    }

    private async Task<BackupRead> InspectBackupAsync(
        IRemoteCommandExecutor executor,
        DatabaseBackupManifest manifest,
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
            return new BackupRead(stat.Error);
        }

        var fields = stat.Output!.Trim().Split('\t');
        if (fields.Length != 2 || !string.Equals(fields[0].Trim(), "regular file", StringComparison.Ordinal) ||
            !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            size != manifest.Verification.SizeBytes)
        {
            return new BackupRead(new RemoteError(
                RemoteErrorCode.PathConflict,
                "Backup file type/size no longer matches the verified manifest."));
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
            return new BackupRead(checksum.Error);
        }

        var actualSha = checksum.Output!
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (actualSha is null || !string.Equals(actualSha, manifest.Verification.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new BackupRead(new RemoteError(
                RemoteErrorCode.PathConflict,
                "Backup checksum no longer matches the verified manifest."));
        }

        if (manifest.Engine == DatabaseEngineKind.PostgreSql)
        {
            var structural = await ExecuteReadAsync(
                    executor,
                    new RemoteCommandSpec(
                        "pg_restore",
                        ["--list", manifest.BackupPath.Value],
                        _options.InspectionTimeout,
                        OperationRisk.ReadOnly,
                        StableEnvironment),
                    cancellationToken)
                .ConfigureAwait(false);
            if (structural.Error is not null)
            {
                return new BackupRead(new RemoteError(
                    RemoteErrorCode.PathConflict,
                    "The PostgreSQL archive no longer passes structural verification."));
            }
        }
        else
        {
            var header = await ExecuteReadAsync(
                    executor,
                    new RemoteCommandSpec(
                        "head",
                        ["-c", "8192", "--", manifest.BackupPath.Value],
                        _options.InspectionTimeout,
                        OperationRisk.ReadOnly,
                        StableEnvironment),
                    cancellationToken)
                .ConfigureAwait(false);
            if (header.Error is not null)
            {
                return new BackupRead(header.Error);
            }

            var expected = manifest.Engine == DatabaseEngineKind.MariaDb ? "MariaDB dump" : "MySQL dump";
            if (!header.Output!.Contains(expected, StringComparison.OrdinalIgnoreCase))
            {
                return new BackupRead(new RemoteError(
                    RemoteErrorCode.PathConflict,
                    "The SQL backup no longer passes bounded structural-header verification."));
            }
        }

        return new BackupRead(null);
    }

    private async Task<ToolRead> InspectToolVersionAsync(
        IRemoteCommandExecutor executor,
        string tool,
        CancellationToken cancellationToken)
    {
        var version = await ExecuteReadAsync(
                executor,
                new RemoteCommandSpec(
                    tool,
                    ["--version"],
                    _options.InspectionTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (version.Error is not null)
        {
            return new ToolRead(null, version.Error);
        }

        var value = FirstLine(version.Output!);
        return string.IsNullOrWhiteSpace(value)
            ? new ToolRead(null, new RemoteError(RemoteErrorCode.ParseFailed, "Restore tool returned an empty version string."))
            : new ToolRead(Bound(value), null);
    }

    private async Task<TargetRead> InspectTargetAsync(
        IRemoteCommandExecutor executor,
        DatabaseConnectionProfile profile,
        string targetDatabase,
        string? secret,
        CancellationToken cancellationToken)
    {
        var command = BuildTargetInspection(profile, targetDatabase, secret is not null);
        var execution = await executor.ExecuteAsync(
                BuildExecutionSpec(command, secret),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return new TargetRead(null, execution.Error);
        }

        if (execution.Command!.StandardOutput.Length > _options.MaximumDiagnosticCharacters ||
            execution.Command.StandardError.Length > _options.MaximumDiagnosticCharacters)
        {
            return new TargetRead(null, new RemoteError(
                RemoteErrorCode.CapabilityUnavailable,
                "Restore target inspection output exceeded the configured safety bound."));
        }

        if (execution.Command.ExitCode != 0)
        {
            return new TargetRead(null, new RemoteError(
                ClassifyDatabaseFailure(execution.Command.StandardError),
                "The exact database restore target could not be inspected."));
        }

        var json = FirstLine(execution.Command.StandardOutput);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var database = root.GetProperty("database").GetString() ?? string.Empty;
            var identity = root.GetProperty("identity").GetString() ?? string.Empty;
            var version = root.GetProperty("version").GetString() ?? string.Empty;
            var objects = root.GetProperty("objects").GetInt64();
            if (!string.Equals(database, targetDatabase, StringComparison.Ordinal) ||
                identity.Length == 0 || version.Length == 0 || objects < 0)
            {
                return new TargetRead(null, new RemoteError(
                    RemoteErrorCode.PathConflict,
                    "The database engine reported a target identity different from the explicit restore target."));
            }

            return new TargetRead(new DatabaseRestoreTargetSnapshot(database, Bound(identity), Bound(version), objects), null);
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new TargetRead(null, new RemoteError(
                RemoteErrorCode.ParseFailed,
                "Database restore target inspection returned an unrecognized identity payload."));
        }
    }

    private CommandPlan BuildTargetInspection(
        DatabaseConnectionProfile profile,
        string targetDatabase,
        bool hasSecret)
    {
        if (profile.Engine == DatabaseEngineKind.PostgreSql)
        {
            var args = new List<string>
            {
                "--host", profile.RemoteHost,
                "--port", profile.RemotePort.ToString(CultureInfo.InvariantCulture),
                "--no-password",
                "--tuples-only",
                "--no-align",
                "--dbname", targetDatabase,
                "--command", PostgreSqlTargetQuery,
            };
            if (!string.IsNullOrWhiteSpace(profile.Username))
            {
                args.InsertRange(6, ["--username", profile.Username]);
            }

            return WrapCredentialCommand("psql", args, profile.Engine, hasSecret, OperationRisk.ReadOnly);
        }

        var tool = profile.Engine == DatabaseEngineKind.MariaDb ? "mariadb" : "mysql";
        var mysqlArgs = new List<string>
        {
            "--no-defaults",
            "--protocol=TCP",
            $"--host={profile.RemoteHost}",
            $"--port={profile.RemotePort.ToString(CultureInfo.InvariantCulture)}",
            "--batch",
            "--skip-column-names",
            "--raw",
            $"--database={targetDatabase}",
            $"--execute={MySqlTargetQuery}",
        };
        if (!string.IsNullOrWhiteSpace(profile.Username))
        {
            mysqlArgs.Add($"--user={profile.Username}");
        }

        if (!hasSecret)
        {
            mysqlArgs.Add("--skip-password");
        }

        return WrapCredentialCommand(tool, mysqlArgs, profile.Engine, hasSecret, OperationRisk.ReadOnly);
    }

    private CommandPlan BuildRestoreCommand(
        DatabaseConnectionProfile profile,
        DatabaseBackupManifest manifest,
        string targetDatabase,
        bool hasSecret)
    {
        if (profile.Engine == DatabaseEngineKind.PostgreSql)
        {
            var args = new List<string>
            {
                "--host", profile.RemoteHost,
                "--port", profile.RemotePort.ToString(CultureInfo.InvariantCulture),
                "--no-password",
                "--clean",
                "--if-exists",
                "--no-owner",
                "--no-privileges",
                "--exit-on-error",
                "--single-transaction",
                "--dbname", targetDatabase,
            };
            if (!string.IsNullOrWhiteSpace(profile.Username))n            {
                args.Add("--username");
                args.Add(profile.Username);
            }

            args.Add(manifest.BackupPath.Value);
            return WrapCredentialCommand("pg_restore", args, profile.Engine, hasSecret, OperationRisk.Destructive);
        }

        var tool = profile.Engine == DatabaseEngineKind.MariaDb ? "mariadb" : "mysql";
        var argsForClient = new List<string>
        {
            "--no-defaults",
            "--protocol=TCP",
            $"--host={profile.RemoteHost}",
            $"--port={profile.RemotePort.ToString(CultureInfo.InvariantCulture)}",
            "--batch",
            "--raw",
            "--one-database",
            $"--database={targetDatabase}",
        };
        if (!string.IsNullOrWhiteSpace(profile.Username))
        {
            argsForClient.Add($"--user={profile.Username}");
        }

        if (!hasSecret)
        {
            argsForClient.Add("--skip-password");
        }

        var shellArguments = new List<string>
        {
            "-c",
            hasSecret ? MySqlRestoreSecretScript : MySqlRestoreNoSecretScript,
            "serverdesk-database-restore",
            manifest.BackupPath.Value,
            tool,
        };
        shellArguments.AddRange(argsForClient);
        return new CommandPlan("sh", shellArguments, hasSecret, OperationRisk.Destructive);
    }

    private static CommandPlan WrapCredentialCommand(
        string tool,
        IReadOnlyList<string> toolArguments,
        DatabaseEngineKind engine,
        bool hasSecret,
        OperationRisk risk)
    {
        if (!hasSecret)
        {
            return new CommandPlan(tool, toolArguments, false, risk);
        }

        var script = engine == DatabaseEngineKind.PostgreSql
            ? PostgreSqlSecretScript
            : MySqlRestoreSecretScript;
        if (engine != DatabaseEngineKind.PostgreSql)
        {
            var args = new List<string>
            {
                "-c",
                "IFS= read -r SERVERDESK_SECRET_B64; SERVERDESK_SECRET=$(printf '%s' \"$SERVERDESK_SECRET_B64\" | base64 --decode; printf x); MYSQL_PWD=${SERVERDESK_SECRET%x}; unset SERVERDESK_SECRET SERVERDESK_SECRET_B64; export MYSQL_PWD; exec \"$@\"",
                "serverdesk-database-inspection",
                tool,
            };
            args.AddRange(toolArguments);
            return new CommandPlan("sh", args, true, risk);
        }

        var shell = new List<string> { "-c", script, "serverdesk-database-restore", tool };
        shell.AddRange(toolArguments);
        return new CommandPlan("sh", shell, true, risk);
    }

    private RemoteCommandSpec BuildExecutionSpec(CommandPlan command, string? secret)
    {
        SensitiveCommandInput? input = null;
        if (command.UsesSensitiveInput)
        {
            if (secret is null)
            {
                throw new InvalidOperationException("A sensitive-input restore command requires a database secret.");
            }

            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret)) + "\n";
            input = new SensitiveCommandInput(encoded);
        }

        return new RemoteCommandSpec(
            command.Executable,
            command.Arguments,
            command.Risk == OperationRisk.Destructive ? _options.CommandTimeout : _options.InspectionTimeout,
            command.Risk,
            NonSecretEnvironment(command),
            StandardInput: input);
    }

    private static IReadOnlyDictionary<string, string> NonSecretEnvironment(CommandPlan command)
    {
        var values = new Dictionary<string, string>(StableEnvironment, StringComparer.Ordinal);
        if (command.Arguments.Any(argument => argument.Contains("pg_", StringComparison.Ordinal) || argument == "psql") ||
            command.Executable is "pg_restore" or "psql")
        {
            values["PGPASSFILE"] = "/dev/null";
            values["PGCONNECT_TIMEOUT"] = "10";
        }

        return values;
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
            return new ReadResult(null, new RemoteError(
                RemoteErrorCode.CapabilityUnavailable,
                "Database restore inspection output exceeded the configured safety bound."));
        }

        if (execution.Command.ExitCode != 0)
        {
            return new ReadResult(null, new RemoteError(
                ClassifyDatabaseFailure(execution.Command.StandardError),
                "Database restore inspection command failed."));
        }

        return new ReadResult(execution.Command.StandardOutput, null);
    }

    private static DatabaseRestoreRequest NormalizeRequest(DatabaseRestoreRequest request)
    {
        if (request.DatabaseProfileId == Guid.Empty)
        {
            throw new ArgumentException("Database profile id cannot be empty.", nameof(request));
        }

        if (request.BackupId == Guid.Empty)
        {
            throw new ArgumentException("Verified backup id cannot be empty.", nameof(request));
        }

        var target = request.TargetDatabase ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target) ||
            !string.Equals(target, target.Trim(), StringComparison.Ordinal) ||
            target.Length > 128 || target.Any(char.IsControl) || target.StartsWith("-", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Target database identity must be 1-128 printable characters, cannot start with '-' and cannot contain leading/trailing whitespace.",
                nameof(request));
        }

        return request with { TargetDatabase = target };
    }

    private static bool CompatibleFormat(DatabaseEngineKind engine, DatabaseBackupFormat format) =>
        (engine == DatabaseEngineKind.PostgreSql && format == DatabaseBackupFormat.PostgreSqlCustom) ||
        (engine == DatabaseEngineKind.MySql && format == DatabaseBackupFormat.MySqlSql) ||
        (engine == DatabaseEngineKind.MariaDb && format == DatabaseBackupFormat.MariaDbSql);

    private static string RestoreToolFor(DatabaseEngineKind engine) => engine switch
    {
        DatabaseEngineKind.PostgreSql => "pg_restore",
        DatabaseEngineKind.MySql => "mysql",
        DatabaseEngineKind.MariaDb => "mariadb",
        _ => throw new ArgumentOutOfRangeException(nameof(engine)),
    };

    private static string DataLossWarning(DatabaseEngineKind engine, string targetDatabase) => engine switch
    {
        DatabaseEngineKind.PostgreSql =>
            $"Destructive data-loss-sensitive logical restore targets exactly '{targetDatabase}'. Objects represented in the archive may be dropped/recreated. Objects absent from the archive may remain. No automatic rollback is claimed, and ambiguous transport completion must be inspected before any next action.",
        DatabaseEngineKind.MySql or DatabaseEngineKind.MariaDb =>
            $"Destructive data-loss-sensitive logical restore is restricted to exactly '{targetDatabase}' with --one-database. DDL/DML in the verified dump may overwrite or drop represented objects; objects absent from the dump may remain. No automatic rollback is available.",
        _ => "Restore is unsupported.",
    };

    private static string DisplayCommand(
        DatabaseConnectionProfile profile,
        DatabaseBackupManifest manifest,
        string targetDatabase)
    {
        if (profile.Engine == DatabaseEngineKind.PostgreSql)
        {
            return $"pg_restore --host {Token(profile.RemoteHost)} --port {profile.RemotePort.ToString(CultureInfo.InvariantCulture)} --clean --if-exists --no-owner --no-privileges --single-transaction --dbname {Token(targetDatabase)} {Token(manifest.BackupPath.Value)} [credential via sensitive stdin]";
        }

        var tool = profile.Engine == DatabaseEngineKind.MariaDb ? "mariadb" : "mysql";
        return $"{tool} --host={Token(profile.RemoteHost)} --port={profile.RemotePort.ToString(CultureInfo.InvariantCulture)} --one-database --database={Token(targetDatabase)} < {Token(manifest.BackupPath.Value)} [credential via sensitive stdin when configured]";
    }

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

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedEquals(string left, string right)
    {
        var leftBytes = Encoding.UTF8.GetBytes(left ?? string.Empty);
        var rightBytes = Encoding.UTF8.GetBytes(right ?? string.Empty);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string ServerEndpoint(ServerProfile profile) =>
        $"{profile.Username}@{profile.Host}:{profile.Port.ToString(CultureInfo.InvariantCulture)}";

    private static RemoteErrorCode ClassifyDatabaseFailure(string detail)
    {
        detail ??= string.Empty;
        if (detail.Contains("password authentication failed", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Access denied for user", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.AuthenticationFailed;
        }

        if (detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("command denied", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PermissionDenied;
        }

        if (detail.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Unknown database", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathNotFound;
        }

        if (detail.Contains("could not connect", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("can't connect", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("connection refused", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("unknown server host", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.ConnectionFailed;
        }

        if (detail.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("No such file", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.CapabilityUnavailable;
        }

        return RemoteErrorCode.CommandFailed;
    }

    private static bool IsAmbiguousTransport(RemoteErrorCode code) =>
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

    private static DatabaseRestorePreviewResult PreviewFailure(RemoteErrorCode code, string message) =>
        new(null, new RemoteError(code, message));

    private static DatabaseRestoreResult Failure(RemoteError error) =>
        new(false, false, false, error.Message, error);

    private static DatabaseRestoreResult Failure(RemoteErrorCode code, string message) =>
        new(false, false, false, message, new RemoteError(code, message));

    private static DatabaseRestoreResult Ambiguous(string message, string? technicalDetails = null) =>
        new(
            false,
            true,
            false,
            message,
            new RemoteError(RemoteErrorCode.AmbiguousState, message, technicalDetails));

    private sealed record CommandPlan(
        string Executable,
        IReadOnlyList<string> Arguments,
        bool UsesSensitiveInput,
        OperationRisk Risk);

    private sealed record SecretRead(string? Secret, RemoteError? Error);
    private sealed record BackupRead(RemoteError? Error);
    private sealed record ToolRead(string? Version, RemoteError? Error);
    private sealed record TargetRead(DatabaseRestoreTargetSnapshot? Snapshot, RemoteError? Error);
    private sealed record ReadResult(string? Output, RemoteError? Error);

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
}

public sealed class AuditedDatabaseRestoreService : IDatabaseRestoreService
{
    private readonly IDatabaseRestoreService _inner;
    private readonly IOperationAudit _audit;

    public AuditedDatabaseRestoreService(IDatabaseRestoreService inner, IOperationAudit audit)
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
        var result = await _inner.ExecuteAsync(serverProfile, preview, cancellationToken).ConfigureAwait(false);
        var outcome = result.IsSuccess
            ? OperationOutcome.Succeeded
            : result.AmbiguousState ? OperationOutcome.Unknown : OperationOutcome.Failed;
        var target =
            $"{serverProfile.Username}@{serverProfile.Host}:{serverProfile.Port} database-profile:{preview.Request.DatabaseProfileId} database:{SafeAuditToken(preview.Request.TargetDatabase)} backup-id:{preview.Request.BackupId}";
        var entry = OperationAuditEntry.Create(
            "database-restore",
            $"Database restore verification outcome: {outcome}; backup-id={preview.Request.BackupId}; rollback-claimed={result.RollbackAvailable}",
            OperationRisk.Destructive,
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
                Message = result.Message + " Audit persistence failed; do not repeat a destructive restore solely to repair audit history.",
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
