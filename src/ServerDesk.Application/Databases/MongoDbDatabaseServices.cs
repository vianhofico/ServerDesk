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

public sealed class MongoDbDatabaseBackupServiceRouter : IDatabaseBackupService
{
    private readonly IDatabaseProfileRepository _profiles;
    private readonly IDatabaseBackupService _defaultService;
    private readonly IDatabaseBackupService _mongoDbService;

    public MongoDbDatabaseBackupServiceRouter(
        IDatabaseProfileRepository profiles,
        IDatabaseBackupService defaultService,
        IDatabaseBackupService mongoDbService)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _defaultService = defaultService ?? throw new ArgumentNullException(nameof(defaultService));
        _mongoDbService = mongoDbService ?? throw new ArgumentNullException(nameof(mongoDbService));
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
        return profile?.Engine == DatabaseEngineKind.MongoDb
            ? await _mongoDbService.CreateAsync(serverProfile, request, cancellationToken).ConfigureAwait(false)
            : await _defaultService.CreateAsync(serverProfile, request, cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<IReadOnlyList<DatabaseBackupManifest>> ListHistoryAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default) =>
        _defaultService.ListHistoryAsync(serverProfileId, cancellationToken);
}

public sealed class MongoDbDatabaseRestoreServiceRouter : IDatabaseRestoreService
{
    private readonly IDatabaseProfileRepository _profiles;
    private readonly IDatabaseRestoreService _defaultService;
    private readonly IDatabaseRestoreService _mongoDbService;

    public MongoDbDatabaseRestoreServiceRouter(
        IDatabaseProfileRepository profiles,
        IDatabaseRestoreService defaultService,
        IDatabaseRestoreService mongoDbService)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _defaultService = defaultService ?? throw new ArgumentNullException(nameof(defaultService));
        _mongoDbService = mongoDbService ?? throw new ArgumentNullException(nameof(mongoDbService));
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
        return profile?.Engine == DatabaseEngineKind.MongoDb
            ? await _mongoDbService.PreviewAsync(serverProfile, request, cancellationToken).ConfigureAwait(false)
            : await _defaultService.PreviewAsync(serverProfile, request, cancellationToken).ConfigureAwait(false);
    }

    public Task<DatabaseRestoreResult> ExecuteAsync(
        ServerProfile serverProfile,
        DatabaseRestorePreview preview,
        CancellationToken cancellationToken = default) =>
        preview?.Engine == DatabaseEngineKind.MongoDb
            ? _mongoDbService.ExecuteAsync(serverProfile, preview, cancellationToken)
            : _defaultService.ExecuteAsync(serverProfile, preview!, cancellationToken);
}

public sealed class MongoDbDatabaseBackupService : IDatabaseBackupService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C",
            ["LANG"] = "C",
        };

    private readonly IDatabaseProfileRepository _profiles;
    private readonly ISecretStore _secrets;
    private readonly IRemoteCommandExecutorFactory _commands;
    private readonly IDatabaseBackupManifestRepository _manifests;
    private readonly IDatabaseDiagnosticService _diagnostics;
    private readonly DatabaseBackupOptions _options;

    public MongoDbDatabaseBackupService(
        IDatabaseProfileRepository profiles,
        ISecretStore secrets,
        IRemoteCommandExecutorFactory commands,
        IDatabaseBackupManifestRepository manifests,
        IDatabaseDiagnosticService diagnostics,
        DatabaseBackupOptions options)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
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
            return Failure(RemoteErrorCode.InvalidEndpoint, "MongoDB backup requires a database profile id.");
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
            return Failure(RemoteErrorCode.PathNotFound, "The selected MongoDB profile no longer exists.");
        }

        if (profile.Engine != DatabaseEngineKind.MongoDb)
        {
            return Unsupported("The MongoDB backup service refuses non-MongoDB profiles.");
        }

        if (!MongoDbServiceUtilities.IsLoopbackRemoteToolHost(profile.RemoteHost))
        {
            return Unsupported("MongoDB backup is certified only for a literal loopback database endpoint on the SSH host; direct/public remote-tool connections are refused.");
        }

        if (profile.ServerProfileId != serverProfile.Id ||
            !string.Equals(profile.DatabaseName, databaseName, StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict, "MongoDB backup requires exact server/profile/database identity equality.");
        }

        var gate = await MongoDbServiceUtilities.ReadCertifiedSnapshotAsync(
                _diagnostics,
                profile,
                DatabaseCapabilityKind.Backup,
                cancellationToken)
            .ConfigureAwait(false);
        if (gate.Error is not null)
        {
            return gate.Unsupported
                ? Unsupported(gate.Error.Message)
                : new DatabaseBackupCreateResult(null, false, false, false, gate.Error.Message, gate.Error);
        }

        var secret = await MongoDbServiceUtilities.ResolveSecretAsync(_secrets, profile, cancellationToken).ConfigureAwait(false);
        if (secret.Error is not null)
        {
            return new DatabaseBackupCreateResult(null, false, false, false, secret.Error.Message, secret.Error);
        }

        await using var executor = _commands.Create(serverProfile);
        var directory = await MongoDbServiceUtilities.ReadStatAsync(
                executor,
                destination,
                _options.InspectionTimeout,
                _options.MaximumDiagnosticCharacters,
                cancellationToken)
            .ConfigureAwait(false);
        if (directory.Error is not null)
        {
            return new DatabaseBackupCreateResult(null, false, false, false, directory.Error.Message, directory.Error);
        }

        if (!string.Equals(directory.Kind, "directory", StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict, "MongoDB backup destination must be an existing directory.");
        }

        var tools = await MongoDbServiceUtilities.ReadToolPairAsync(
                executor,
                _options.InspectionTimeout,
                _options.MaximumDiagnosticCharacters,
                cancellationToken)
            .ConfigureAwait(false);
        if (tools.Error is not null)
        {
            return new DatabaseBackupCreateResult(null, false, false, false, tools.Error.Message, tools.Error);
        }

        if (!string.Equals(tools.DumpVersion, tools.RestoreVersion, StringComparison.Ordinal))
        {
            return Unsupported("MongoDB backup requires matching mongodump/mongorestore versions for deterministic archive verification.");
        }

        var backupId = Guid.NewGuid();
        var backupPath = destination.Combine($"serverdesk-db-backup-{backupId:N}.archive.gz");
        var command = MongoDbServiceUtilities.BuildDumpCommand(
            profile,
            databaseName,
            backupPath,
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
                    "MongoDB backup was cancelled after mongodump dispatch began. Do not blindly retry.")
                .ConfigureAwait(false);
        }

        if (execution.Error is not null)
        {
            if (MongoDbServiceUtilities.IsAmbiguous(execution.Error.Code))
            {
                return await AmbiguousAfterDispatchAsync(
                        executor,
                        backupPath,
                        "MongoDB backup completion is unknown after transport/timeout/cancellation failure. Do not blindly retry.")
                    .ConfigureAwait(false);
            }

            return await DeterministicFailureAsync(executor, backupPath, execution.Error).ConfigureAwait(false);
        }

        if (execution.Command!.ExitCode != 0)
        {
            return await DeterministicFailureAsync(
                    executor,
                    backupPath,
                    MongoDbServiceUtilities.ClassifyToolFailure(execution.Command.StandardError))
                .ConfigureAwait(false);
        }

        var verification = await MongoDbServiceUtilities.VerifyArchiveAsync(
                executor,
                profile,
                backupPath,
                databaseName,
                secret.Secret,
                tools.RestoreVersion!,
                _options,
                cancellationToken)
            .ConfigureAwait(false);
        if (verification.Error is not null || verification.Evidence is null)
        {
            return Ambiguous(
                "mongodump returned success, but deterministic MongoDB archive verification failed. The artifact remains unverified.",
                verification.Error);
        }

        var manifest = new DatabaseBackupManifest(
            backupId,
            serverProfile.Id,
            profile.Id,
            DatabaseEngineKind.MongoDb,
            databaseName,
            profile.Username,
            backupPath,
            DatabaseBackupFormat.MongoDbArchive,
            "mongodump",
            tools.DumpVersion!,
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

        return new DatabaseBackupCreateResult(
            manifest,
            false,
            false,
            historyPersisted,
            historyPersisted
                ? "MongoDB archive backup was created and verified before being marked usable."
                : "MongoDB archive was verified, but local history persistence failed. Do not repeat the backup solely to repair history.");
    }

    private async Task<DatabaseBackupCreateResult> DeterministicFailureAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        RemoteError failure)
    {
        var state = await MongoDbServiceUtilities.ReadStatAllowMissingAsync(
                executor,
                path,
                _options.InspectionTimeout,
                CancellationToken.None)
            .ConfigureAwait(false);
        if (state.Exists || state.Error is not null)
        {
            return Ambiguous("MongoDB backup failed after dispatch, but output exists or cannot be proven absent. Treat it as unverified.", failure);
        }

        return new DatabaseBackupCreateResult(null, false, false, false, failure.Message, failure);
    }

    private async Task<DatabaseBackupCreateResult> AmbiguousAfterDispatchAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        string message)
    {
        var state = await MongoDbServiceUtilities.ReadStatAllowMissingAsync(
                executor,
                path,
                _options.InspectionTimeout,
                CancellationToken.None)
            .ConfigureAwait(false);
        var suffix = state.Error is not null
            ? " The exact archive state could not be read."
            : state.Exists
                ? " An archive exists but is unverified."
                : " No archive was observed at re-inspection time, but remote completion is still unknown.";
        return Ambiguous(message + suffix);
    }

    private static DatabaseBackupCreateResult Failure(RemoteErrorCode code, string message) =>
        new(null, false, false, false, message, new RemoteError(code, message));

    private static DatabaseBackupCreateResult Unsupported(string message) =>
        new(null, false, true, false, message, new RemoteError(RemoteErrorCode.CapabilityUnavailable, message));

    private static DatabaseBackupCreateResult Ambiguous(string message, RemoteError? cause = null) =>
        new(null, true, false, false, message, new RemoteError(RemoteErrorCode.AmbiguousState, message, cause?.TechnicalDetails));

    private static string NormalizeDatabaseName(string value) => MongoDbServiceUtilities.NormalizeDatabaseName(value);

    private static RemotePath NormalizeAbsoluteDirectory(string value) => MongoDbServiceUtilities.NormalizeAbsoluteDirectory(value);
}

public sealed class MongoDbDatabaseRestoreService : IDatabaseRestoreService
{
    private readonly IDatabaseProfileRepository _profiles;
    private readonly IDatabaseBackupManifestRepository _manifests;
    private readonly ISecretStore _secrets;
    private readonly IRemoteCommandExecutorFactory _commands;
    private readonly IDatabaseDiagnosticService _diagnostics;
    private readonly DatabaseRestoreOptions _options;
    private readonly ConcurrentDictionary<Guid, string> _capabilities = new();

    public MongoDbDatabaseRestoreService(
        IDatabaseProfileRepository profiles,
        IDatabaseBackupManifestRepository manifests,
        ISecretStore secrets,
        IRemoteCommandExecutorFactory commands,
        IDatabaseDiagnosticService diagnostics,
        DatabaseRestoreOptions options)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
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

        var gate = await MongoDbServiceUtilities.ReadCertifiedSnapshotAsync(
                _diagnostics,
                binding.Profile,
                DatabaseCapabilityKind.Restore,
                cancellationToken)
            .ConfigureAwait(false);
        if (gate.Error is not null || gate.Snapshot is null)
        {
            return new DatabaseRestorePreviewResult(null, gate.Error, gate.Unsupported);
        }

        var secret = await MongoDbServiceUtilities.ResolveSecretAsync(_secrets, binding.Profile, cancellationToken).ConfigureAwait(false);
        if (secret.Error is not null)
        {
            return new DatabaseRestorePreviewResult(null, secret.Error);
        }

        await using var executor = _commands.Create(serverProfile);
        var archive = await MongoDbServiceUtilities.InspectVerifiedArchiveAsync(
                executor,
                binding.Profile,
                binding.Manifest,
                secret.Secret,
                _options,
                cancellationToken)
            .ConfigureAwait(false);
        if (archive.Error is not null)
        {
            return new DatabaseRestorePreviewResult(null, archive.Error);
        }

        var tool = await MongoDbServiceUtilities.ReadToolPairAsync(
                executor,
                _options.InspectionTimeout,
                _options.MaximumDiagnosticCharacters,
                cancellationToken)
            .ConfigureAwait(false);
        if (tool.Error is not null)
        {
            return new DatabaseRestorePreviewResult(null, tool.Error);
        }

        if (!string.Equals(tool.RestoreVersion, binding.Manifest.ToolVersion, StringComparison.Ordinal))
        {
            return new DatabaseRestorePreviewResult(
                null,
                new RemoteError(RemoteErrorCode.CapabilityUnavailable, "mongorestore version must exactly match the mongodump version recorded by the verified manifest."),
                Unsupported: true);
        }

        var target = MongoDbServiceUtilities.TargetSnapshot(gate.Snapshot, normalized.TargetDatabase);
        if (target.Error is not null || target.Snapshot is null)
        {
            return new DatabaseRestorePreviewResult(null, target.Error);
        }

        var plan = MongoDbServiceUtilities.BuildRestorePlan(
            binding.Profile,
            binding.Manifest,
            normalized.TargetDatabase,
            secret.Secret is not null);
        var planId = Guid.NewGuid();
        var provisional = new DatabaseRestorePreview(
            planId,
            string.Empty,
            serverProfile.Id,
            MongoDbServiceUtilities.ServerEndpoint(serverProfile),
            normalized,
            DatabaseEngineKind.MongoDb,
            DatabaseBackupFormat.MongoDbArchive,
            binding.Manifest.BackupPath.Value,
            binding.Manifest.Verification.Sha256,
            binding.Manifest.Verification.SizeBytes,
            binding.Manifest.ToolName,
            binding.Manifest.ToolVersion,
            "mongorestore",
            tool.RestoreVersion!,
            MongoDbServiceUtilities.ManifestFingerprint(binding.Manifest),
            target.Snapshot,
            MongoDbServiceUtilities.TargetFingerprint(target.Snapshot),
            plan.Executable,
            plan.Arguments,
            plan.UsesSensitiveInput,
            OperationRisk.Destructive,
            MongoDbServiceUtilities.DisplayRestoreCommand(binding.Profile, binding.Manifest, normalized.TargetDatabase),
            $"Destructive MongoDB restore will drop and recreate collections present in the verified archive for database '{normalized.TargetDatabase}'. Collections absent from the archive are not claimed to be removed. No automatic rollback is available.",
            RollbackAvailable: false);
        var fingerprint = MongoDbServiceUtilities.PreviewFingerprint(provisional);
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
        var actualFingerprint = MongoDbServiceUtilities.PreviewFingerprint(preview with { Fingerprint = string.Empty });
        if (!MongoDbServiceUtilities.FixedEquals(preview.Fingerprint, actualFingerprint) ||
            !_capabilities.TryRemove(preview.PlanId, out var expectedFingerprint) ||
            !string.Equals(expectedFingerprint, preview.Fingerprint, StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict, "MongoDB restore preview is stale, replayed or modified. Preview the exact target again.");
        }

        if (preview.Engine != DatabaseEngineKind.MongoDb ||
            preview.BackupFormat != DatabaseBackupFormat.MongoDbArchive ||
            serverProfile.Id != preview.ServerProfileId ||
            !string.Equals(MongoDbServiceUtilities.ServerEndpoint(serverProfile), preview.ServerEndpoint, StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict, "MongoDB restore server/engine identity changed after Preview.");
        }

        var binding = await LoadBindingAsync(serverProfile, preview.Request, cancellationToken).ConfigureAwait(false);
        if (binding.Error is not null || binding.Profile is null || binding.Manifest is null)
        {
            return Failure(binding.Error ?? new RemoteError(RemoteErrorCode.PathConflict, "MongoDB restore binding could not be revalidated."));
        }

        if (!string.Equals(MongoDbServiceUtilities.ManifestFingerprint(binding.Manifest), preview.ManifestFingerprint, StringComparison.Ordinal) ||
            !string.Equals(binding.Manifest.BackupPath.Value, preview.BackupPath, StringComparison.Ordinal) ||
            !string.Equals(binding.Manifest.Verification.Sha256, preview.BackupSha256, StringComparison.OrdinalIgnoreCase) ||
            binding.Manifest.Verification.SizeBytes != preview.BackupSizeBytes)
        {
            return Failure(RemoteErrorCode.PathConflict, "The verified MongoDB backup manifest changed after Preview.");
        }

        var gate = await MongoDbServiceUtilities.ReadCertifiedSnapshotAsync(
                _diagnostics,
                binding.Profile,
                DatabaseCapabilityKind.Restore,
                cancellationToken)
            .ConfigureAwait(false);
        if (gate.Error is not null || gate.Snapshot is null)
        {
            return Failure(gate.Error ?? new RemoteError(RemoteErrorCode.CapabilityUnavailable, "MongoDB restore target is no longer certified."));
        }

        var targetBefore = MongoDbServiceUtilities.TargetSnapshot(gate.Snapshot, preview.Request.TargetDatabase);
        if (targetBefore.Error is not null || targetBefore.Snapshot is null ||
            !string.Equals(MongoDbServiceUtilities.TargetFingerprint(targetBefore.Snapshot), preview.TargetFingerprint, StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict, "The exact MongoDB target changed after Preview. No destructive command was sent.");
        }

        var secret = await MongoDbServiceUtilities.ResolveSecretAsync(_secrets, binding.Profile, cancellationToken).ConfigureAwait(false);
        if (secret.Error is not null)
        {
            return Failure(secret.Error);
        }

        await using var executor = _commands.Create(serverProfile);
        var archive = await MongoDbServiceUtilities.InspectVerifiedArchiveAsync(
                executor,
                binding.Profile,
                binding.Manifest,
                secret.Secret,
                _options,
                cancellationToken)
            .ConfigureAwait(false);
        if (archive.Error is not null)
        {
            return Failure(RemoteErrorCode.PathConflict, "The MongoDB archive no longer matches its verified manifest. No restore command was sent.");
        }

        var tools = await MongoDbServiceUtilities.ReadToolPairAsync(
                executor,
                _options.InspectionTimeout,
                _options.MaximumDiagnosticCharacters,
                cancellationToken)
            .ConfigureAwait(false);
        if (tools.Error is not null ||
            !string.Equals(tools.RestoreVersion, preview.RestoreToolVersion, StringComparison.Ordinal) ||
            !string.Equals(tools.RestoreVersion, binding.Manifest.ToolVersion, StringComparison.Ordinal))
        {
            return Failure(RemoteErrorCode.PathConflict, "mongorestore capability/version changed after Preview.");
        }

        var plan = MongoDbServiceUtilities.BuildRestorePlan(
            binding.Profile,
            binding.Manifest,
            preview.Request.TargetDatabase,
            secret.Secret is not null);
        if (!string.Equals(plan.Executable, preview.Executable, StringComparison.Ordinal) ||
            !plan.Arguments.SequenceEqual(preview.Arguments, StringComparer.Ordinal) ||
            plan.UsesSensitiveInput != preview.UsesSensitiveInput || preview.Risk != OperationRisk.Destructive)
        {
            return Failure(RemoteErrorCode.PathConflict, "MongoDB restore command no longer matches the previewed exact target and archive.");
        }

        try
        {
            var execution = await executor.ExecuteAsync(
                    MongoDbServiceUtilities.BuildExecutionSpec(plan, secret.Secret, _options.CommandTimeout),
                    cancellationToken)
                .ConfigureAwait(false);
            if (execution.Error is not null)
            {
                return MongoDbServiceUtilities.IsAmbiguous(execution.Error.Code)
                    ? Ambiguous("MongoDB restore completion is unknown after transport/timeout/cancellation failure. Do not retry before exact target inspection.")
                    : await FailedAfterDispatchAsync(binding.Profile, preview.Request.TargetDatabase).ConfigureAwait(false);
            }

            if (execution.Command!.ExitCode != 0)
            {
                return await FailedAfterDispatchAsync(binding.Profile, preview.Request.TargetDatabase).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            return Ambiguous("MongoDB restore was cancelled after destructive dispatch began. Completion is unknown; do not blindly retry.");
        }

        DatabaseDiagnosticResult post;
        try
        {
            post = await _diagnostics.InspectAsync(binding.Profile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Ambiguous("mongorestore returned success, but post-restore verification was cancelled. Do not retry before exact target inspection.");
        }

        if (!post.IsSuccess || post.Snapshot is null)
        {
            return Ambiguous("mongorestore returned success, but the exact MongoDB target could not be post-verified.");
        }

        var targetAfter = MongoDbServiceUtilities.TargetSnapshot(post.Snapshot, preview.Request.TargetDatabase);
        if (targetAfter.Error is not null || targetAfter.Snapshot is null)
        {
            return Ambiguous("mongorestore returned success, but MongoDB target identity could not be normalized after restore.");
        }

        var stableIdentity = string.Equals(targetAfter.Snapshot.DatabaseName, preview.Request.TargetDatabase, StringComparison.Ordinal) &&
            string.Equals(targetAfter.Snapshot.ConnectionIdentity, preview.TargetBefore.ConnectionIdentity, StringComparison.Ordinal) &&
            string.Equals(targetAfter.Snapshot.ServerVersion, preview.TargetBefore.ServerVersion, StringComparison.Ordinal);
        if (!stableIdentity)
        {
            return new DatabaseRestoreResult(
                false,
                true,
                false,
                "MongoDB restore returned success, but post-state identity differs from the previewed exact target.",
                new RemoteError(RemoteErrorCode.AmbiguousState, "MongoDB post-restore target identity verification failed."),
                targetAfter.Snapshot);
        }

        return new DatabaseRestoreResult(
            true,
            false,
            false,
            "MongoDB restore completed and the exact database identity was post-verified. No automatic rollback is claimed.",
            null,
            targetAfter.Snapshot);
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
            return BindingRead.Fail(RemoteErrorCode.PathNotFound, "MongoDB profile or verified backup manifest no longer exists.");
        }

        if (profile.Engine != DatabaseEngineKind.MongoDb || manifest.Engine != DatabaseEngineKind.MongoDb)
        {
            return BindingRead.UnsupportedMode("The MongoDB restore service refuses non-MongoDB bindings.");
        }

        if (!MongoDbServiceUtilities.IsLoopbackRemoteToolHost(profile.RemoteHost))
        {
            return BindingRead.UnsupportedMode("MongoDB restore is certified only for a literal loopback database endpoint on the SSH host; direct/public remote-tool connections are refused.");
        }

        if (!manifest.IsVerified || manifest.Format != DatabaseBackupFormat.MongoDbArchive ||
            manifest.Verification.SizeBytes <= 0 || manifest.Verification.Sha256.Length != 64)
        {
            return BindingRead.Fail(RemoteErrorCode.PathConflict, "Only verified MongoDB archive manifests may be restored.");
        }

        if (profile.ServerProfileId != serverProfile.Id || manifest.ServerProfileId != serverProfile.Id ||
            manifest.DatabaseProfileId != profile.Id ||
            string.IsNullOrWhiteSpace(profile.DatabaseName) ||
            !string.Equals(profile.DatabaseName, request.TargetDatabase, StringComparison.Ordinal) ||
            !string.Equals(manifest.DatabaseName, request.TargetDatabase, StringComparison.Ordinal))
        {
            return BindingRead.Fail(RemoteErrorCode.PathConflict, "MongoDB restore requires exact server/profile/database/backup identity equality.");
        }

        return BindingRead.Success(profile, manifest);
    }

    private async Task<DatabaseRestoreResult> FailedAfterDispatchAsync(
        DatabaseConnectionProfile profile,
        string databaseName)
    {
        DatabaseRestoreTargetSnapshot? observed = null;
        try
        {
            var diagnostics = await _diagnostics.InspectAsync(profile, CancellationToken.None).ConfigureAwait(false);
            if (diagnostics.Snapshot is not null)
            {
                observed = MongoDbServiceUtilities.TargetSnapshot(diagnostics.Snapshot, databaseName).Snapshot;
            }
        }
        catch
        {
        }

        const string message = "MongoDB restore reported failure after destructive dispatch. Partial collection changes cannot be ruled out; no automatic retry or rollback is claimed.";
        return new DatabaseRestoreResult(
            false,
            true,
            false,
            message,
            new RemoteError(RemoteErrorCode.AmbiguousState, message),
            observed);
    }

    private static DatabaseRestoreRequest NormalizeRequest(DatabaseRestoreRequest request)
    {
        if (request.DatabaseProfileId == Guid.Empty || request.BackupId == Guid.Empty)
        {
            throw new ArgumentException("MongoDB restore requires non-empty profile and verified backup ids.", nameof(request));
        }

        return request with { TargetDatabase = MongoDbServiceUtilities.NormalizeDatabaseName(request.TargetDatabase) };
    }

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
}

internal static class MongoDbServiceUtilities
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C",
            ["LANG"] = "C",
        };

    public static bool IsLoopbackRemoteToolHost(string host) =>
        System.Net.IPAddress.TryParse(host, out var address) && System.Net.IPAddress.IsLoopback(address);

    public static async Task<CertifiedRead> ReadCertifiedSnapshotAsync(
        IDatabaseDiagnosticService diagnostics,
        DatabaseConnectionProfile profile,
        DatabaseCapabilityKind capability,
        CancellationToken cancellationToken)
    {
        DatabaseDiagnosticResult result;
        try
        {
            result = await diagnostics.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return CertifiedRead.Fail(new RemoteError(RemoteErrorCode.ConnectionFailed, "MongoDB certified target diagnostics failed unexpectedly."));
        }

        if (!result.IsSuccess || result.Snapshot is null)
        {
            return CertifiedRead.Fail(
                result.Failure?.RemoteError ?? new RemoteError(RemoteErrorCode.ConnectionFailed, result.Failure?.Message ?? "MongoDB diagnostics failed."));
        }

        var snapshot = result.Snapshot;
        var topology = snapshot.Metadata.FirstOrDefault(item =>
            string.Equals(item.Name, "topology", StringComparison.Ordinal))?.Value;
        if (!string.Equals(topology, "standalone", StringComparison.Ordinal))
        {
            return CertifiedRead.UnsupportedMode($"MongoDB topology '{topology ?? "unknown"}' is not certified for {capability}. Only the exact standalone topology is certified.");
        }

        if (DatabaseCertificationMatrix.LevelFor(DatabaseEngineKind.MongoDb, snapshot.ServerVersion, capability) !=
            DatabaseCertificationLevel.Certified)
        {
            return CertifiedRead.UnsupportedMode($"MongoDB {snapshot.ServerVersion} {capability} is not certified for the standalone topology.");
        }

        return CertifiedRead.Success(snapshot);
    }

    public static async Task<SecretRead> ResolveSecretAsync(
        ISecretStore secrets,
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken)
    {
        if (profile.AuthenticationKind == DatabaseAuthenticationKind.None)
        {
            return new SecretRead(null, null);
        }

        if (profile.CredentialReference is not { } reference)
        {
            return new SecretRead(null, new RemoteError(RemoteErrorCode.AuthenticationFailed, "The MongoDB profile has no credential reference."));
        }

        try
        {
            var value = await secrets.GetAsync(reference, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(value) || value.Contains('\0')
                ? new SecretRead(null, new RemoteError(RemoteErrorCode.AuthenticationFailed, "The stored MongoDB credential is unavailable or unsafe."))
                : new SecretRead(value, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new SecretRead(null, new RemoteError(RemoteErrorCode.AuthenticationFailed, "The stored MongoDB credential could not be read securely."));
        }
    }

    public static async Task<ToolPairRead> ReadToolPairAsync(
        IRemoteCommandExecutor executor,
        TimeSpan timeout,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var dump = await ReadToolVersionAsync(executor, "mongodump", timeout, maximumCharacters, cancellationToken).ConfigureAwait(false);
        if (dump.Error is not null)
        {
            return new ToolPairRead(null, null, dump.Error);
        }

        var restore = await ReadToolVersionAsync(executor, "mongorestore", timeout, maximumCharacters, cancellationToken).ConfigureAwait(false);
        return restore.Error is null
            ? new ToolPairRead(dump.Version, restore.Version, null)
            : new ToolPairRead(null, null, restore.Error);
    }

    private static async Task<VersionRead> ReadToolVersionAsync(
        IRemoteCommandExecutor executor,
        string tool,
        TimeSpan timeout,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteAsync(
            new RemoteCommandSpec(tool, ["--version"], timeout, OperationRisk.ReadOnly, StableEnvironment),
            cancellationToken).ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return new VersionRead(null, execution.Error);
        }

        if (execution.Command!.ExitCode != 0 || execution.Command.StandardOutput.Length > maximumCharacters ||
            execution.Command.StandardError.Length > maximumCharacters)
        {
            return new VersionRead(null, new RemoteError(RemoteErrorCode.CapabilityUnavailable, $"{tool} is unavailable or returned an unsafe version response."));
        }

        var line = FirstUsefulLine(execution.Command.StandardOutput, execution.Command.StandardError);
        var separator = line.LastIndexOf(':');
        var version = (separator >= 0 ? line[(separator + 1)..] : line).Trim();
        return string.IsNullOrWhiteSpace(version)
            ? new VersionRead(null, new RemoteError(RemoteErrorCode.ParseFailed, $"{tool} returned an unrecognized version."))
            : new VersionRead(version, null);
    }

    public static RemoteCommandSpec BuildDumpCommand(
        DatabaseConnectionProfile profile,
        string databaseName,
        RemotePath path,
        string? secret,
        TimeSpan timeout)
    {
        var arguments = ConnectionArguments(profile);
        arguments.Add($"--db={databaseName}");
        arguments.Add($"--archive={path.Value}");
        arguments.Add("--gzip");
        arguments.Add("--quiet");
        return new RemoteCommandSpec(
            "mongodump",
            arguments,
            timeout,
            OperationRisk.Mutating,
            StableEnvironment,
            StandardInput: secret is null ? null : new SensitiveCommandInput(secret + "\n"));
    }

    public static CommandPlan BuildRestorePlan(
        DatabaseConnectionProfile profile,
        DatabaseBackupManifest manifest,
        string databaseName,
        bool hasSecret)
    {
        var arguments = ConnectionArguments(profile);
        arguments.Add($"--archive={manifest.BackupPath.Value}");
        arguments.Add("--gzip");
        arguments.Add($"--nsInclude={databaseName}.*");
        arguments.Add("--drop");
        arguments.Add("--stopOnError");
        arguments.Add("--quiet");
        return new CommandPlan("mongorestore", arguments, hasSecret);
    }

    public static RemoteCommandSpec BuildExecutionSpec(CommandPlan plan, string? secret, TimeSpan timeout)
    {
        if (plan.UsesSensitiveInput && secret is null)
        {
            throw new InvalidOperationException("A password-authenticated MongoDB command requires sensitive stdin.");
        }

        return new RemoteCommandSpec(
            plan.Executable,
            plan.Arguments,
            timeout,
            OperationRisk.Destructive,
            StableEnvironment,
            StandardInput: plan.UsesSensitiveInput ? new SensitiveCommandInput(secret! + "\n") : null);
    }

    public static async Task<VerificationRead> VerifyArchiveAsync(
        IRemoteCommandExecutor executor,
        DatabaseConnectionProfile profile,
        RemotePath path,
        string databaseName,
        string? secret,
        string restoreVersion,
        DatabaseBackupOptions options,
        CancellationToken cancellationToken)
    {
        var stat = await ReadStatAsync(
                executor,
                path,
                options.InspectionTimeout,
                options.MaximumDiagnosticCharacters,
                cancellationToken)
            .ConfigureAwait(false);
        if (stat.Error is not null)
        {
            return new VerificationRead(null, stat.Error);
        }

        if (!string.Equals(stat.Kind, "regular file", StringComparison.Ordinal) || stat.Size is null || stat.Size <= 0)
        {
            return new VerificationRead(null, new RemoteError(RemoteErrorCode.ParseFailed, "MongoDB backup is not a non-empty regular file."));
        }

        if (stat.Size > options.MaximumBackupBytes)
        {
            return new VerificationRead(null, new RemoteError(RemoteErrorCode.CapabilityUnavailable, "MongoDB backup exceeds the configured verification size bound."));
        }

        var sha = await ReadSha256Async(executor, path, options.InspectionTimeout, options.MaximumDiagnosticCharacters, cancellationToken).ConfigureAwait(false);
        if (sha.Error is not null)
        {
            return new VerificationRead(null, sha.Error);
        }

        var arguments = ConnectionArguments(profile);
        arguments.Add($"--archive={path.Value}");
        arguments.Add("--gzip");
        arguments.Add($"--nsInclude={databaseName}.*");
        arguments.Add("--dryRun");
        arguments.Add("--quiet");
        var dryRun = await executor.ExecuteAsync(
            new RemoteCommandSpec(
                "mongorestore",
                arguments,
                options.InspectionTimeout,
                OperationRisk.ReadOnly,
                StableEnvironment,
                StandardInput: secret is null ? null : new SensitiveCommandInput(secret + "\n")),
            cancellationToken).ConfigureAwait(false);
        if (dryRun.Error is not null)
        {
            return new VerificationRead(null, dryRun.Error);
        }

        if (dryRun.Command!.ExitCode != 0)
        {
            return new VerificationRead(null, ClassifyToolFailure(dryRun.Command.StandardError));
        }

        return new VerificationRead(
            new DatabaseBackupVerificationEvidence(
                stat.Size.Value,
                sha.Sha256!,
                $"mongorestore {restoreVersion} --dryRun parsed the gzip archive for exact namespace {databaseName}.*",
                DateTimeOffset.UtcNow),
            null);
    }

    public static async Task<ArchiveRead> InspectVerifiedArchiveAsync(
        IRemoteCommandExecutor executor,
        DatabaseConnectionProfile profile,
        DatabaseBackupManifest manifest,
        string? secret,
        DatabaseRestoreOptions options,
        CancellationToken cancellationToken)
    {
        var stat = await ReadStatAsync(
                executor,
                manifest.BackupPath,
                options.InspectionTimeout,
                options.MaximumDiagnosticCharacters,
                cancellationToken)
            .ConfigureAwait(false);
        if (stat.Error is not null || !string.Equals(stat.Kind, "regular file", StringComparison.Ordinal) ||
            stat.Size != manifest.Verification.SizeBytes)
        {
            return new ArchiveRead(new RemoteError(RemoteErrorCode.PathConflict, "MongoDB archive file type/size no longer matches its verified manifest."));
        }

        var sha = await ReadSha256Async(
                executor,
                manifest.BackupPath,
                options.InspectionTimeout,
                options.MaximumDiagnosticCharacters,
                cancellationToken)
            .ConfigureAwait(false);
        if (sha.Error is not null || !string.Equals(sha.Sha256, manifest.Verification.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return new ArchiveRead(new RemoteError(RemoteErrorCode.PathConflict, "MongoDB archive checksum no longer matches its verified manifest."));
        }

        var tools = await ReadToolPairAsync(executor, options.InspectionTimeout, options.MaximumDiagnosticCharacters, cancellationToken).ConfigureAwait(false);
        if (tools.Error is not null || !string.Equals(tools.RestoreVersion, manifest.ToolVersion, StringComparison.Ordinal))
        {
            return new ArchiveRead(new RemoteError(RemoteErrorCode.PathConflict, "MongoDB archive verifier version no longer matches its manifest."));
        }

        var verifyOptions = new DatabaseBackupOptions(
            options.CommandTimeout,
            options.InspectionTimeout,
            options.MaximumDiagnosticCharacters,
            long.MaxValue);
        var verification = await VerifyArchiveAsync(
                executor,
                profile,
                manifest.BackupPath,
                manifest.DatabaseName,
                secret,
                tools.RestoreVersion!,
                verifyOptions,
                cancellationToken)
            .ConfigureAwait(false);
        return verification.Error is null
            ? new ArchiveRead(null)
            : new ArchiveRead(new RemoteError(RemoteErrorCode.PathConflict, "MongoDB archive no longer passes deterministic dry-run verification."));
    }

    private static List<string> ConnectionArguments(DatabaseConnectionProfile profile)
    {
        var arguments = new List<string>
        {
            $"--host={profile.RemoteHost}",
            $"--port={profile.RemotePort.ToString(CultureInfo.InvariantCulture)}",
        };
        if (profile.TlsMode == DatabaseTlsMode.Required)
        {
            arguments.Add("--ssl");
        }

        if (profile.AuthenticationKind == DatabaseAuthenticationKind.Password)
        {
            arguments.Add($"--username={profile.Username}");
            arguments.Add($"--authenticationDatabase={profile.AuthenticationDatabase ?? "admin"}");
        }

        return arguments;
    }

    public static TargetRead TargetSnapshot(DatabaseDiagnosticSnapshot snapshot, string databaseName)
    {
        if (snapshot.Engine != DatabaseEngineKind.MongoDb)
        {
            return new TargetRead(null, new RemoteError(RemoteErrorCode.PathConflict, "MongoDB target diagnostics returned a different engine."));
        }

        var catalog = snapshot.Catalogs.FirstOrDefault(item => string.Equals(item.Name, databaseName, StringComparison.Ordinal));
        if (catalog is null)
        {
            return new TargetRead(null, new RemoteError(RemoteErrorCode.PathNotFound, "The exact MongoDB target database was not present in bounded diagnostics."));
        }

        return new TargetRead(
            new DatabaseRestoreTargetSnapshot(
                databaseName,
                snapshot.ConnectionIdentity ?? "unknown",
                snapshot.ServerVersion,
                catalog.ItemCount ?? 0),
            null);
    }

    public static async Task<StatRead> ReadStatAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        TimeSpan timeout,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteAsync(
            new RemoteCommandSpec(
                "stat",
                ["--printf=%F\t%s", "--", path.Value],
                timeout,
                OperationRisk.ReadOnly,
                StableEnvironment),
            cancellationToken).ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return new StatRead(null, null, execution.Error);
        }

        if (execution.Command!.StandardOutput.Length > maximumCharacters || execution.Command.StandardError.Length > maximumCharacters ||
            execution.Command.ExitCode != 0)
        {
            return new StatRead(null, null, new RemoteError(RemoteErrorCode.CommandFailed, "MongoDB archive metadata could not be inspected safely."));
        }

        var fields = execution.Command.StandardOutput.Trim().Split('\t');
        return fields.Length == 2 && long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size)
            ? new StatRead(fields[0].Trim(), size, null)
            : new StatRead(null, null, new RemoteError(RemoteErrorCode.ParseFailed, "stat returned an unrecognized MongoDB archive metadata row."));
    }

    public static async Task<OptionalStatRead> ReadStatAllowMissingAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteAsync(
            new RemoteCommandSpec(
                "stat",
                ["--printf=%F\t%s", "--", path.Value],
                timeout,
                OperationRisk.ReadOnly,
                StableEnvironment),
            cancellationToken).ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return new OptionalStatRead(false, execution.Error);
        }

        if (execution.Command!.ExitCode == 0)
        {
            return new OptionalStatRead(true, null);
        }

        var missing = execution.Command.StandardError.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
            execution.Command.StandardError.Contains("cannot stat", StringComparison.OrdinalIgnoreCase);
        return missing
            ? new OptionalStatRead(false, null)
            : new OptionalStatRead(false, new RemoteError(RemoteErrorCode.CommandFailed, "MongoDB archive state could not be inspected."));
    }

    private static async Task<ShaRead> ReadSha256Async(
        IRemoteCommandExecutor executor,
        RemotePath path,
        TimeSpan timeout,
        int maximumCharacters,
        CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteAsync(
            new RemoteCommandSpec("sha256sum", ["--", path.Value], timeout, OperationRisk.ReadOnly, StableEnvironment),
            cancellationToken).ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return new ShaRead(null, execution.Error);
        }

        if (execution.Command!.ExitCode != 0 || execution.Command.StandardOutput.Length > maximumCharacters)
        {
            return new ShaRead(null, new RemoteError(RemoteErrorCode.CommandFailed, "MongoDB archive checksum could not be read safely."));
        }

        var sha = execution.Command.StandardOutput
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return sha is not null && sha.Length == 64 && sha.All(Uri.IsHexDigit)
            ? new ShaRead(sha.ToUpperInvariant(), null)
            : new ShaRead(null, new RemoteError(RemoteErrorCode.ParseFailed, "sha256sum returned an unrecognized MongoDB archive checksum."));
    }

    public static RemoteError ClassifyToolFailure(string detail)
    {
        detail ??= string.Empty;
        if (detail.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("auth error", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteError(RemoteErrorCode.AuthenticationFailed, "MongoDB rejected the stored database credential.");
        }

        if (detail.Contains("not authorized", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("Unauthorized", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteError(RemoteErrorCode.PermissionDenied, "MongoDB denied a required backup/restore operation.");
        }

        if (detail.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("server selection", StringComparison.OrdinalIgnoreCase))
        {
            return new RemoteError(RemoteErrorCode.ConnectionFailed, "MongoDB could not be reached from the SSH-controlled operation path.");
        }

        return new RemoteError(RemoteErrorCode.CommandFailed, "MongoDB Database Tools command failed without exposing credential-bearing diagnostics.");
    }

    public static bool IsAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or
            RemoteErrorCode.OperationCancelled or RemoteErrorCode.ConnectionFailed or RemoteErrorCode.AmbiguousState;

    public static string NormalizeDatabaseName(string value)
    {
        var normalized = value ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized) || !string.Equals(normalized, normalized.Trim(), StringComparison.Ordinal) ||
            normalized.Length > 63 || normalized.Any(char.IsControl) ||
            normalized.Any(character => "/\\.\"$*<>:|?".Contains(character)))
        {
            throw new ArgumentException("MongoDB database identity must be 1-63 printable characters and cannot contain MongoDB-forbidden database-name characters.", nameof(value));
        }

        return normalized;
    }

    public static RemotePath NormalizeAbsoluteDirectory(string value)
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

    public static string ManifestFingerprint(DatabaseBackupManifest manifest) => Hash(
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

    public static string TargetFingerprint(DatabaseRestoreTargetSnapshot snapshot) => Hash(
        string.Join("\u001f",
            snapshot.DatabaseName,
            snapshot.ConnectionIdentity,
            snapshot.ServerVersion,
            snapshot.UserObjectCount.ToString(CultureInfo.InvariantCulture)));

    public static string PreviewFingerprint(DatabaseRestorePreview preview) => Hash(
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

    public static bool FixedEquals(string left, string right) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left ?? string.Empty),
            Encoding.UTF8.GetBytes(right ?? string.Empty));

    public static string ServerEndpoint(ServerProfile profile) =>
        $"{profile.Username}@{profile.Host}:{profile.Port.ToString(CultureInfo.InvariantCulture)}";

    public static string DisplayRestoreCommand(
        DatabaseConnectionProfile profile,
        DatabaseBackupManifest manifest,
        string databaseName) =>
        $"mongorestore --host={profile.RemoteHost} --port={profile.RemotePort.ToString(CultureInfo.InvariantCulture)} --archive={manifest.BackupPath.Value} --gzip --nsInclude={databaseName}.* --drop --stopOnError [credential via sensitive stdin when configured]";

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static string FirstUsefulLine(string first, string second) =>
        (string.IsNullOrWhiteSpace(first) ? second : first)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

    public sealed record CommandPlan(string Executable, IReadOnlyList<string> Arguments, bool UsesSensitiveInput);
    public sealed record CertifiedRead(DatabaseDiagnosticSnapshot? Snapshot, RemoteError? Error, bool Unsupported)
    {
        public static CertifiedRead Success(DatabaseDiagnosticSnapshot snapshot) => new(snapshot, null, false);
        public static CertifiedRead Fail(RemoteError error) => new(null, error, false);
        public static CertifiedRead UnsupportedMode(string message) => new(null, new RemoteError(RemoteErrorCode.CapabilityUnavailable, message), true);
    }

    public sealed record SecretRead(string? Secret, RemoteError? Error);
    public sealed record ToolPairRead(string? DumpVersion, string? RestoreVersion, RemoteError? Error);
    private sealed record VersionRead(string? Version, RemoteError? Error);
    public sealed record StatRead(string? Kind, long? Size, RemoteError? Error);
    public sealed record OptionalStatRead(bool Exists, RemoteError? Error);
    private sealed record ShaRead(string? Sha256, RemoteError? Error);
    public sealed record VerificationRead(DatabaseBackupVerificationEvidence? Evidence, RemoteError? Error);
    public sealed record ArchiveRead(RemoteError? Error);
    public sealed record TargetRead(DatabaseRestoreTargetSnapshot? Snapshot, RemoteError? Error);
}
