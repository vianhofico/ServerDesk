using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Backups;

public sealed class BackupRestoreService : IBackupRestoreService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C", ["LANG"] = "C" };

    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly BackupRestoreOptions _options;
    private readonly ConcurrentDictionary<Guid, string> _restoreCapabilities = new();

    public BackupRestoreService(IRemoteCommandExecutorFactory commandFactory, BackupRestoreOptions options)
    {
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<BackupCreateResult> CreateBackupAsync(
        ServerProfile profile,
        BackupCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        BackupCreateRequest normalized;
        try
        {
            normalized = NormalizeCreateRequest(request);
        }
        catch (ArgumentException exception)
        {
            return BackupFailure(RemoteErrorCode.InvalidEndpoint, exception.Message);
        }

        await using var executor = _commandFactory.Create(profile);
        var source = RemotePath.Parse(normalized.TargetPath);
        var destination = RemotePath.Parse(normalized.DestinationDirectory);
        var sourceState = await ReadFileStateAsync(executor, source, cancellationToken).ConfigureAwait(false);
        if (sourceState.Error is not null)
        {
            return new BackupCreateResult(null, sourceState.Error.Message, sourceState.Error);
        }

        if (sourceState.State is not { Kind: "regular file" } sourceFile)
        {
            return BackupFailure(
                RemoteErrorCode.CapabilityUnavailable,
                "M5.5 currently certifies regular-file backup targets only; directory/application adapters must be explicitly implemented before use.");
        }

        if (sourceFile.Size > _options.MaximumFileBytes)
        {
            return BackupFailure(RemoteErrorCode.CapabilityUnavailable, "The selected file exceeds the configured backup safety bound.");
        }

        var directoryState = await ReadStatAsync(executor, destination, cancellationToken).ConfigureAwait(false);
        if (directoryState.Error is not null)
        {
            return new BackupCreateResult(null, directoryState.Error.Message, directoryState.Error);
        }

        if (directoryState.State is not { Kind: "directory" })
        {
            return BackupFailure(RemoteErrorCode.PathConflict, "The selected backup destination must be an existing normalized directory.");
        }

        var backupId = Guid.NewGuid();
        var backupPath = destination.Combine($"serverdesk-backup-{backupId:N}.bak");
        RemoteExecutionResult execution;
        try
        {
            execution = await ExecuteAsync(
                executor,
                [
                    "-n", "install",
                    "-m", sourceFile.Permissions.ToString(),
                    "-o", sourceFile.UserId.ToString(CultureInfo.InvariantCulture),
                    "-g", sourceFile.GroupId.ToString(CultureInfo.InvariantCulture),
                    "--", source.Value, backupPath.Value,
                ],
                OperationRisk.Mutating,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return BackupAmbiguous("Backup creation was cancelled after copy dispatch began. Inspect the destination before retrying.");
        }

        if (execution.Error is not null)
        {
            return IsAmbiguous(execution.Error.Code)
                ? BackupAmbiguous("Backup copy completion is unknown. Inspect the destination before retrying.", execution.Error.TechnicalDetails)
                : new BackupCreateResult(null, execution.Error.Message, execution.Error);
        }

        if (execution.Command!.ExitCode != 0)
        {
            var detail = FirstUseful(execution.Command.StandardError, execution.Command.StandardOutput, "Backup copy failed.");
            return BackupFailure(ClassifyFailure(detail), detail);
        }

        var verification = await ReadFileStateAsync(executor, backupPath, CancellationToken.None).ConfigureAwait(false);
        if (verification.Error is not null || verification.State is null || !Equivalent(sourceFile, verification.State))
        {
            return BackupAmbiguous(
                "Backup copy returned success, but size/checksum/UID/GID/mode could not be verified exactly. Restore will not be offered as safe.",
                verification.Error?.TechnicalDetails);
        }

        var manifest = new BackupManifest(
            backupId,
            BackupTargetKind.File,
            source,
            backupPath,
            sourceFile.Size,
            sourceFile.Sha256,
            sourceFile.UserId,
            sourceFile.GroupId,
            sourceFile.Permissions,
            DateTimeOffset.UtcNow,
            true,
            DateTimeOffset.UtcNow);
        return new BackupCreateResult(
            manifest,
            "Backup was created and verified by size, SHA-256, UID/GID and mode before being marked usable for restore.");
    }

    public async Task<RestorePreviewResult> PreviewRestoreAsync(
        ServerProfile profile,
        BackupManifest manifest,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(manifest);
        if (!manifest.IsVerified || manifest.TargetKind != BackupTargetKind.File)
        {
            return PreviewFailure(RemoteErrorCode.CapabilityUnavailable, "Only a verified regular-file backup manifest can be previewed for restore.");
        }

        if (!IsCanonicalAbsolute(manifest.TargetPath) || !IsCanonicalAbsolute(manifest.BackupPath))
        {
            return PreviewFailure(RemoteErrorCode.InvalidEndpoint, "Backup manifest paths are not normalized absolute paths.");
        }

        await using var executor = _commandFactory.Create(profile);
        var backup = await ReadFileStateAsync(executor, manifest.BackupPath, cancellationToken).ConfigureAwait(false);
        if (backup.Error is not null || backup.State is null || !MatchesManifest(manifest, backup.State))
        {
            return new RestorePreviewResult(
                null,
                backup.Error ?? new RemoteError(RemoteErrorCode.PathConflict, "Backup content or metadata no longer matches its verified manifest."));
        }

        var target = await ReadFileStateAsync(executor, manifest.TargetPath, cancellationToken).ConfigureAwait(false);
        if (target.Error is not null || target.State is not { Kind: "regular file" } targetState)
        {
            return new RestorePreviewResult(
                null,
                target.Error ?? new RemoteError(RemoteErrorCode.PathConflict, "The exact restore target is no longer a regular file."));
        }

        var planId = Guid.NewGuid();
        var impact = new RestoreImpact(
            manifest.TargetPath,
            false,
            $"Restore will overwrite exactly '{manifest.TargetPath.Value}'. No alternate/broader target is permitted. Deterministic rollback is unavailable because no verified pre-restore rollback copy has been captured by this plan.");
        var provisional = new RestorePreview(
            planId,
            string.Empty,
            manifest,
            FileFingerprint(targetState),
            impact,
            OperationRisk.Destructive,
            $"Restore verified backup {manifest.BackupId} to exact target {manifest.TargetPath.Value}");
        var fingerprint = PreviewFingerprint(provisional);
        var preview = provisional with { Fingerprint = fingerprint };
        _restoreCapabilities[planId] = fingerprint;
        return new RestorePreviewResult(preview, null);
    }

    public async Task<RestoreResult> ExecuteRestoreAsync(
        ServerProfile profile,
        RestorePreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preview);
        var actualFingerprint = PreviewFingerprint(preview with { Fingerprint = string.Empty });
        if (!_restoreCapabilities.TryRemove(preview.PlanId, out var expectedFingerprint) ||
            !FixedTimeEquals(preview.Fingerprint, expectedFingerprint) ||
            !FixedTimeEquals(preview.Fingerprint, actualFingerprint))
        {
            return RestoreFailure(RemoteErrorCode.PathConflict, "Restore Preview is missing, replayed or modified. Re-verify the backup and preview again.");
        }

        if (!preview.Manifest.IsVerified ||
            !preview.Impact.ExactOverwriteTarget.Equals(preview.Manifest.TargetPath))
        {
            return RestoreFailure(RemoteErrorCode.PathConflict, "Restore Preview target identity no longer matches the verified manifest.");
        }

        await using var executor = _commandFactory.Create(profile);
        var backup = await ReadFileStateAsync(executor, preview.Manifest.BackupPath, cancellationToken).ConfigureAwait(false);
        if (backup.Error is not null || backup.State is null || !MatchesManifest(preview.Manifest, backup.State))
        {
            return RestoreFailure(
                backup.Error ?? new RemoteError(RemoteErrorCode.PathConflict, "Verified backup drifted after Preview."));
        }

        var target = await ReadFileStateAsync(executor, preview.Manifest.TargetPath, cancellationToken).ConfigureAwait(false);
        if (target.Error is not null || target.State is null ||
            !string.Equals(FileFingerprint(target.State), preview.BeforeTargetFingerprint, StringComparison.Ordinal))
        {
            return RestoreFailure(
                target.Error ?? new RemoteError(RemoteErrorCode.PathConflict, "Exact restore target changed after Preview."));
        }

        var stage = preview.Manifest.TargetPath.Parent.Combine($".serverdesk-restore-{Guid.NewGuid():N}.new");
        var stageCreated = false;
        var replaceDispatched = false;
        try
        {
            var install = await ExecuteAsync(
                executor,
                [
                    "-n", "install",
                    "-m", preview.Manifest.Permissions.ToString(),
                    "-o", preview.Manifest.UserId.ToString(CultureInfo.InvariantCulture),
                    "-g", preview.Manifest.GroupId.ToString(CultureInfo.InvariantCulture),
                    "--", preview.Manifest.BackupPath.Value, stage.Value,
                ],
                OperationRisk.Destructive,
                cancellationToken).ConfigureAwait(false);
            if (install.Error is not null)
            {
                return IsAmbiguous(install.Error.Code)
                    ? RestoreAmbiguous("Restore staging completion is unknown. Refresh target and backup state before any retry.", install.Error.TechnicalDetails)
                    : new RestoreResult(false, false, install.Error.Message, install.Error);
            }

            if (install.Command!.ExitCode != 0)
            {
                var detail = FirstUseful(install.Command.StandardError, install.Command.StandardOutput, "Restore staging failed.");
                return RestoreFailure(ClassifyFailure(detail), detail);
            }

            stageCreated = true;
            var staged = await ReadFileStateAsync(executor, stage, cancellationToken).ConfigureAwait(false);
            if (staged.Error is not null || staged.State is null || !MatchesManifest(preview.Manifest, staged.State))
            {
                return RestoreAmbiguous(
                    "Restore stage was created but failed exact size/checksum/UID/GID/mode validation. Live target was not intentionally replaced.",
                    staged.Error?.TechnicalDetails);
            }

            replaceDispatched = true;
            var replace = await ExecuteAsync(
                executor,
                ["-n", "mv", "-f", "--", stage.Value, preview.Manifest.TargetPath.Value],
                OperationRisk.Destructive,
                cancellationToken).ConfigureAwait(false);
            if (replace.Error is not null)
            {
                return IsAmbiguous(replace.Error.Code)
                    ? RestoreAmbiguous("Atomic restore replace completion is unknown. Do not retry until the exact target is re-read.", replace.Error.TechnicalDetails)
                    : await VerifyDeterministicReplaceFailureAsync(executor, preview, replace.Error).ConfigureAwait(false);
            }

            if (replace.Command!.ExitCode != 0)
            {
                var detail = FirstUseful(replace.Command.StandardError, replace.Command.StandardOutput, "Atomic restore replace failed.");
                return await VerifyDeterministicReplaceFailureAsync(
                    executor,
                    preview,
                    new RemoteError(ClassifyFailure(detail), detail)).ConfigureAwait(false);
            }

            stageCreated = false;
            var after = await ReadFileStateAsync(executor, preview.Manifest.TargetPath, CancellationToken.None).ConfigureAwait(false);
            if (after.Error is not null || after.State is null || !MatchesManifest(preview.Manifest, after.State))
            {
                return RestoreAmbiguous(
                    "Restore replace returned success, but exact target checksum/metadata verification did not match the manifest.",
                    after.Error?.TechnicalDetails);
            }

            return new RestoreResult(
                true,
                false,
                "Verified backup was restored to the exact original target and checksum/UID/GID/mode were verified after atomic replacement.",
                null,
                preview.Manifest);
        }
        catch (OperationCanceledException) when (stageCreated || replaceDispatched)
        {
            return RestoreAmbiguous("Restore was cancelled after destructive staging began. Re-read exact target state before any retry.");
        }
        finally
        {
            if (stageCreated && !replaceDispatched)
            {
                await BestEffortRemoveAsync(executor, stage).ConfigureAwait(false);
            }
        }
    }

    internal static BackupCreateRequest NormalizeCreateRequest(BackupCreateRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = NormalizeAbsolute(request.TargetPath, nameof(request.TargetPath));
        var destination = NormalizeAbsolute(request.DestinationDirectory, nameof(request.DestinationDirectory));
        if (target.Value == "/")
        {
            throw new ArgumentException("Root is not a supported regular-file backup target.", nameof(request));
        }

        return new BackupCreateRequest(target.Value, destination.Value);
    }

    private async Task<RestoreResult> VerifyDeterministicReplaceFailureAsync(
        IRemoteCommandExecutor executor,
        RestorePreview preview,
        RemoteError error)
    {
        var state = await ReadFileStateAsync(executor, preview.Manifest.TargetPath, CancellationToken.None).ConfigureAwait(false);
        if (state.Error is not null || state.State is null)
        {
            return RestoreAmbiguous(
                "Restore replace reported failure, but exact target state could not be re-read.",
                state.Error?.TechnicalDetails ?? error.TechnicalDetails);
        }

        if (!string.Equals(FileFingerprint(state.State), preview.BeforeTargetFingerprint, StringComparison.Ordinal))
        {
            return RestoreAmbiguous(
                "Restore replace reported failure, but exact target state changed. Completion is ambiguous; do not blindly retry.",
                error.TechnicalDetails);
        }

        return new RestoreResult(false, false, error.Message, error);
    }

    private async Task<StateRead> ReadFileStateAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        var stat = await ReadStatAsync(executor, path, cancellationToken).ConfigureAwait(false);
        if (stat.Error is not null || stat.State is null)
        {
            return new StateRead(null, stat.Error);
        }

        if (stat.State.Kind != "regular file")
        {
            return new StateRead(new FileState(
                stat.State.Kind,
                stat.State.Size,
                stat.State.UserId,
                stat.State.GroupId,
                stat.State.Permissions,
                string.Empty), null);
        }

        var hash = await ExecuteReadAsync(
            executor,
            ["-n", "sha256sum", "--", path.Value],
            cancellationToken).ConfigureAwait(false);
        if (hash.Error is not null)
        {
            return new StateRead(null, hash.Error);
        }

        var token = hash.Output!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (token is null || token.Length != 64 || token.Any(character => !Uri.IsHexDigit(character)))
        {
            return new StateRead(null, new RemoteError(RemoteErrorCode.ParseFailed, "sha256sum returned an unrecognized checksum."));
        }

        return new StateRead(new FileState(
            stat.State.Kind,
            stat.State.Size,
            stat.State.UserId,
            stat.State.GroupId,
            stat.State.Permissions,
            token.ToUpperInvariant()), null);
    }

    private async Task<StatRead> ReadStatAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteReadAsync(
            executor,
            ["-n", "stat", "--printf=%F\t%s\t%u\t%g\t%a", "--", path.Value],
            cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new StatRead(null, result.Error);
        }

        var fields = result.Output!.Trim().Split('\t');
        if (fields.Length != 5 ||
            !long.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size) ||
            !int.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ||
            !int.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var gid) ||
            !short.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out var mode))
        {
            return new StatRead(null, new RemoteError(RemoteErrorCode.ParseFailed, "stat returned an unrecognized backup metadata row."));
        }

        RemoteUnixPermissions permissions;
        try
        {
            permissions = RemoteUnixPermissions.FromMode(mode);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            return new StatRead(null, new RemoteError(RemoteErrorCode.ParseFailed, exception.Message));
        }

        return new StatRead(new StatState(fields[0].Trim(), size, uid, gid, permissions), null);
    }

    private async Task<ReadResult> ExecuteReadAsync(
        IRemoteCommandExecutor executor,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await ExecuteAsync(executor, arguments, OperationRisk.ReadOnly, cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new ReadResult(null, result.Error);
        }

        if (OutputTooLarge(result.Command!))
        {
            return new ReadResult(null, new RemoteError(RemoteErrorCode.CapabilityUnavailable, "Backup command output exceeded the configured safety bound."));
        }

        if (result.Command!.ExitCode != 0)
        {
            var detail = FirstUseful(result.Command.StandardError, result.Command.StandardOutput, "Backup inspection command failed.");
            return new ReadResult(null, new RemoteError(ClassifyFailure(detail), detail));
        }

        return new ReadResult(result.Command.StandardOutput, null);
    }

    private Task<RemoteExecutionResult> ExecuteAsync(
        IRemoteCommandExecutor executor,
        IReadOnlyList<string> arguments,
        OperationRisk risk,
        CancellationToken cancellationToken) =>
        executor.ExecuteAsync(
            new RemoteCommandSpec(
                _options.PrivilegeExecutable,
                arguments,
                _options.CommandTimeout,
                risk,
                StableEnvironment),
            cancellationToken);

    private async Task BestEffortRemoveAsync(IRemoteCommandExecutor executor, RemotePath path)
    {
        try
        {
            _ = await ExecuteAsync(
                executor,
                ["-n", "rm", "-f", "--", path.Value],
                OperationRisk.Mutating,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private static RemotePath NormalizeAbsolute(string value, string field)
    {
        var raw = value?.Trim() ?? string.Empty;
        if (!raw.StartsWith('/', StringComparison.Ordinal) || raw.Any(char.IsControl))
        {
            throw new ArgumentException($"{field} must be an absolute printable remote path.", field);
        }

        var path = RemotePath.Parse(raw);
        var expected = raw.Length > 1 ? raw.TrimEnd('/') : raw;
        if (!path.IsAbsolute || !string.Equals(path.Value, expected, StringComparison.Ordinal))
        {
            throw new ArgumentException($"{field} must already be a normalized absolute path without traversal segments.", field);
        }

        return path;
    }

    private static bool IsCanonicalAbsolute(RemotePath path) =>
        path.IsAbsolute && string.Equals(RemotePath.Parse(path.Value).Value, path.Value, StringComparison.Ordinal);

    private static bool Equivalent(FileState left, FileState right) =>
        left.Kind == right.Kind && left.Size == right.Size && left.UserId == right.UserId &&
        left.GroupId == right.GroupId && left.Permissions == right.Permissions &&
        string.Equals(left.Sha256, right.Sha256, StringComparison.Ordinal);

    private static bool MatchesManifest(BackupManifest manifest, FileState state) =>
        state.Kind == "regular file" && state.Size == manifest.Size && state.UserId == manifest.UserId &&
        state.GroupId == manifest.GroupId && state.Permissions == manifest.Permissions &&
        string.Equals(state.Sha256, manifest.Sha256, StringComparison.Ordinal);

    private static string FileFingerprint(FileState state) =>
        Sha256(string.Join('\u001f', state.Kind, state.Size, state.UserId, state.GroupId, state.Permissions.Mode, state.Sha256));

    private static string PreviewFingerprint(RestorePreview preview) =>
        Sha256(string.Join(
            "\u001f",
            preview.PlanId,
            preview.Manifest.BackupId,
            preview.Manifest.TargetPath.Value,
            preview.Manifest.BackupPath.Value,
            preview.Manifest.Size,
            preview.Manifest.Sha256,
            preview.Manifest.UserId,
            preview.Manifest.GroupId,
            preview.Manifest.Permissions.Mode,
            preview.Manifest.IsVerified,
            preview.BeforeTargetFingerprint,
            preview.Impact.ExactOverwriteTarget.Value,
            preview.Impact.RollbackAvailable,
            preview.Impact.Message,
            preview.Risk,
            preview.Summary));

    private bool OutputTooLarge(RemoteCommandResult command) =>
        command.StandardOutput.Length > _options.MaximumOutputCharacters ||
        command.StandardError.Length > _options.MaximumOutputCharacters;

    private static RemoteErrorCode ClassifyFailure(string detail)
    {
        if (detail.Contains("password is required", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.SudoRequired;
        }

        if (detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PermissionDenied;
        }

        if (detail.Contains("No such file", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathNotFound;
        }

        return RemoteErrorCode.CommandFailed;
    }

    private static bool IsAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or
            RemoteErrorCode.OperationCancelled or RemoteErrorCode.ConnectionFailed;

    private static string FirstUseful(string first, string second, string fallback) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : !string.IsNullOrWhiteSpace(second) ? second.Trim() : fallback;

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string left, string right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static BackupCreateResult BackupFailure(RemoteErrorCode code, string message) =>
        new(null, message, new RemoteError(code, message));

    private static BackupCreateResult BackupAmbiguous(string message, string? details = null) =>
        new(null, message, new RemoteError(RemoteErrorCode.AmbiguousState, message, details));

    private static RestorePreviewResult PreviewFailure(RemoteErrorCode code, string message) =>
        new(null, new RemoteError(code, message));

    private static RestoreResult RestoreFailure(RemoteError error) =>
        new(false, error.Code == RemoteErrorCode.AmbiguousState, error.Message, error);

    private static RestoreResult RestoreFailure(RemoteErrorCode code, string message) =>
        RestoreFailure(new RemoteError(code, message));

    private static RestoreResult RestoreAmbiguous(string message, string? details = null) =>
        new(false, true, message, new RemoteError(RemoteErrorCode.AmbiguousState, message, details));

    private sealed record ReadResult(string? Output, RemoteError? Error);
    private sealed record StatRead(StatState? State, RemoteError? Error);
    private sealed record StateRead(FileState? State, RemoteError? Error);
    private sealed record StatState(string Kind, long Size, int UserId, int GroupId, RemoteUnixPermissions Permissions);
    private sealed record FileState(string Kind, long Size, int UserId, int GroupId, RemoteUnixPermissions Permissions, string Sha256);
}

public sealed class AuditedBackupRestoreService : IBackupRestoreService
{
    private readonly IBackupRestoreService _inner;
    private readonly IOperationAudit _audit;

    public AuditedBackupRestoreService(IBackupRestoreService inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public async Task<BackupCreateResult> CreateBackupAsync(
        ServerProfile profile,
        BackupCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.CreateBackupAsync(profile, request, cancellationToken).ConfigureAwait(false);
        var outcome = result.IsSuccess
            ? OperationOutcome.Succeeded
            : result.Error?.Code == RemoteErrorCode.AmbiguousState ? OperationOutcome.Unknown : OperationOutcome.Failed;
        var id = result.Manifest?.BackupId.ToString() ?? "unverified";
        var persisted = await TryAuditAsync(
            OperationAuditEntry.Create(
                "backup-create",
                $"Regular-file backup creation verification outcome: {outcome}; backup-id={id}",
                OperationRisk.Mutating,
                outcome,
                $"{profile.Username}@{profile.Host}:{profile.Port} file:{request.TargetPath} backup-id:{id}"),
            cancellationToken).ConfigureAwait(false);
        return persisted ? result : result with { Message = result.Message + " Audit persistence failed; do not repeat backup creation solely for audit." };
    }

    public Task<RestorePreviewResult> PreviewRestoreAsync(
        ServerProfile profile,
        BackupManifest manifest,
        CancellationToken cancellationToken = default) =>
        _inner.PreviewRestoreAsync(profile, manifest, cancellationToken);

    public async Task<RestoreResult> ExecuteRestoreAsync(
        ServerProfile profile,
        RestorePreview preview,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.ExecuteRestoreAsync(profile, preview, cancellationToken).ConfigureAwait(false);
        var outcome = result.IsSuccess
            ? OperationOutcome.Succeeded
            : result.AmbiguousState ? OperationOutcome.Unknown : OperationOutcome.Failed;
        var persisted = await TryAuditAsync(
            OperationAuditEntry.Create(
                "backup-restore",
                $"Exact-target restore verification outcome: {outcome}; backup-id={preview.Manifest.BackupId}",
                OperationRisk.Destructive,
                outcome,
                $"{profile.Username}@{profile.Host}:{profile.Port} file:{preview.Manifest.TargetPath.Value} backup-id:{preview.Manifest.BackupId}"),
            cancellationToken).ConfigureAwait(false);
        return persisted ? result : result with { Message = result.Message + " Audit persistence failed; do not repeat restore solely for audit." };
    }

    private async ValueTask<bool> TryAuditAsync(OperationAuditEntry entry, CancellationToken cancellationToken)
    {
        try
        {
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
