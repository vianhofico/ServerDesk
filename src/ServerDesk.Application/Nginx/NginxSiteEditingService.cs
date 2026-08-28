using System.Globalization;
using System.Text;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Nginx;

public sealed class NginxSiteEditingService : INginxSiteEditingService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private const string ValidatorScript = """
        #!/bin/sh
        set -eu
        candidate=$1
        target=$2
        nginx=$3
        mount --bind "$candidate" "$target"
        exec "$nginx" -t
        """;

    private readonly IRemoteCommandExecutorFactory _executorFactory;
    private readonly IRemoteFileSystemFactory _fileSystemFactory;
    private readonly IRemoteFileEditorService _editor;
    private readonly NginxSiteEditingOptions _options;

    public NginxSiteEditingService(
        IRemoteCommandExecutorFactory executorFactory,
        IRemoteFileSystemFactory fileSystemFactory,
        IRemoteFileEditorService editor,
        NginxSiteEditingOptions options)
    {
        _executorFactory = executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
        _fileSystemFactory = fileSystemFactory ?? throw new ArgumentNullException(nameof(fileSystemFactory));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async ValueTask<NginxSiteEditLoadResult> LoadAsync(
        ServerProfile profile,
        RemotePath requestedPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!requestedPath.IsAbsolute || requestedPath.Value is "/")
        {
            return LoadFailure(RemoteErrorCode.InvalidEndpoint, "nginx site editing requires an absolute file path.");
        }

        var canonical = await ResolveCanonicalPathAsync(profile, requestedPath, cancellationToken).ConfigureAwait(false);
        if (canonical.Error is not null || canonical.Path is null)
        {
            return new NginxSiteEditLoadResult(null, canonical.Error);
        }

        try
        {
            var document = await _editor.LoadAsync(profile, canonical.Path.Value, cancellationToken).ConfigureAwait(false);
            if (document.Metadata.Kind != RemoteFileKind.File)
            {
                return LoadFailure(
                    RemoteErrorCode.PathConflict,
                    "The resolved nginx site target must be a regular file.");
            }

            return new NginxSiteEditLoadResult(
                new NginxSiteEditDocument(requestedPath, canonical.Path.Value, document),
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RemoteFileSystemException exception)
        {
            return new NginxSiteEditLoadResult(null, exception.Error);
        }
    }

    public async Task<NginxSiteApplyResult> ApplyAsync(
        ServerProfile profile,
        NginxSiteEditDocument original,
        string candidateText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(original);
        candidateText ??= string.Empty;
        var candidateBytes = Encoding.UTF8.GetBytes(candidateText);
        if (candidateBytes.Length > _options.MaximumCandidateBytes)
        {
            return Failure(
                RemoteErrorCode.CapabilityUnavailable,
                $"nginx candidate exceeds the {_options.MaximumCandidateBytes} byte safety limit.");
        }

        if (string.Equals(original.Document.Text, candidateText, StringComparison.Ordinal))
        {
            return new NginxSiteApplyResult(
                true,
                false,
                false,
                false,
                "No nginx changes were detected; no mutation was sent.");
        }

        var reResolved = await ResolveCanonicalPathAsync(profile, original.RequestedPath, cancellationToken).ConfigureAwait(false);
        if (reResolved.Error is not null || reResolved.Path is null)
        {
            return Failure(reResolved.Error ?? new RemoteError(RemoteErrorCode.PathNotFound, "The nginx target can no longer be resolved."));
        }

        if (reResolved.Path.Value != original.CanonicalPath)
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                $"The nginx site link now resolves to '{reResolved.Path.Value.Value}' instead of '{original.CanonicalPath.Value}'. Reload before applying changes.");
        }

        var staleCheck = await ReloadForStaleCheckAsync(profile, original, cancellationToken).ConfigureAwait(false);
        if (staleCheck is not null)
        {
            return staleCheck;
        }

        var metadata = original.Document.Metadata;
        if (metadata.UserId is not { } userId || metadata.GroupId is not { } groupId)
        {
            return Failure(
                RemoteErrorCode.CapabilityUnavailable,
                "ServerDesk will not replace an nginx file when original UID/GID cannot be preserved.");
        }

        var token = Guid.NewGuid().ToString("N");
        var candidatePath = RemotePath.Parse($"/tmp/serverdesk-nginx-candidate-{token}.conf");
        var helperPath = RemotePath.Parse($"/tmp/serverdesk-nginx-helper-{token}.sh");
        var rootHelperPath = RemotePath.Parse($"/tmp/serverdesk-nginx-validator-{token}.sh");
        var backupPath = original.CanonicalPath.Parent.Combine($".serverdesk-nginx-backup-{token}");
        var rootHelperInstalled = false;
        var backupCreated = false;
        var liveReplaced = false;

        await using var fileSystem = _fileSystemFactory.Create(profile);
        await using var executor = _executorFactory.Create(profile);
        try
        {
            await fileSystem.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await UploadPrivateAsync(fileSystem, candidatePath, candidateBytes, cancellationToken).ConfigureAwait(false);
            await UploadPrivateAsync(
                    fileSystem,
                    helperPath,
                    Encoding.UTF8.GetBytes(ValidatorScript.Replace("\r\n", "\n", StringComparison.Ordinal)),
                    cancellationToken)
                .ConfigureAwait(false);

            var helperInstall = await ExecuteCheckedAsync(
                    executor,
                    _options.PrivilegeExecutable,
                    [
                        "-n",
                        "install",
                        "-m",
                        "700",
                        "-o",
                        "0",
                        "-g",
                        "0",
                        "--",
                        helperPath.Value,
                        rootHelperPath.Value,
                    ],
                    OperationRisk.Mutating,
                    "Could not install the temporary root-owned nginx validator.",
                    ambiguousOnTransport: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!helperInstall.IsSuccess)
            {
                return helperInstall.ToApplyResult(backupPath);
            }

            rootHelperInstalled = true;
            var validation = await ExecuteCheckedAsync(
                    executor,
                    _options.PrivilegeExecutable,
                    [
                        "-n",
                        _options.NamespaceExecutable,
                        "--mount",
                        "--propagation",
                        "private",
                        "--",
                        _options.ShellExecutable,
                        rootHelperPath.Value,
                        candidatePath.Value,
                        original.CanonicalPath.Value,
                        _options.NginxExecutable,
                    ],
                    OperationRisk.ReadOnly,
                    "nginx rejected the staged candidate.",
                    ambiguousOnTransport: false,
                    cancellationToken)
                .ConfigureAwait(false);

            var rootCleanup = await DeletePrivilegedTempAsync(executor, rootHelperPath, cancellationToken).ConfigureAwait(false);
            rootHelperInstalled = false;
            if (!rootCleanup.IsSuccess)
            {
                return rootCleanup.ToApplyResult(backupPath);
            }

            if (!validation.IsSuccess)
            {
                return validation.ToApplyResult(backupPath, validationFailed: validation.CommandRejected);
            }

            var backup = await ExecuteCheckedAsync(
                    executor,
                    _options.PrivilegeExecutable,
                    [
                        "-n",
                        "install",
                        "-m",
                        metadata.Permissions.ToString(),
                        "-o",
                        userId.ToString(CultureInfo.InvariantCulture),
                        "-g",
                        groupId.ToString(CultureInfo.InvariantCulture),
                        "--",
                        original.CanonicalPath.Value,
                        backupPath.Value,
                    ],
                    OperationRisk.Mutating,
                    "Could not create the nginx recovery backup.",
                    ambiguousOnTransport: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!backup.IsSuccess)
            {
                return backup.ToApplyResult(backupPath);
            }

            backupCreated = true;
            var save = await _editor.SavePrivilegedAsync(
                    profile,
                    original.Document,
                    candidateText,
                    validation: null,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!save.IsSuccess)
            {
                if (save.Error?.Code == RemoteErrorCode.AmbiguousState)
                {
                    return Ambiguous(
                        "The nginx live-file replace lost a reliable completion signal. Do not retry. Reload the site and inspect the recovery backup first.",
                        backupPath,
                        save.Error.TechnicalDetails);
                }

                return Failure(save.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, save.Message), backupPath);
            }

            liveReplaced = true;
            var liveValidation = await RunNginxTestAsync(executor, mutationHasStarted: true, cancellationToken).ConfigureAwait(false);
            if (!liveValidation.IsSuccess)
            {
                if (liveValidation.Ambiguous)
                {
                    return liveValidation.ToApplyResult(backupPath);
                }

                return await RollBackAsync(
                        profile,
                        executor,
                        original,
                        backupPath,
                        reloadRuntime: false,
                        "The candidate passed isolated validation but failed the real nginx configuration test after replacement.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var reload = await ExecuteCheckedAsync(
                    executor,
                    _options.PrivilegeExecutable,
                    ["-n", _options.NginxExecutable, "-s", "reload"],
                    OperationRisk.Destructive,
                    "nginx reload failed.",
                    ambiguousOnTransport: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!reload.IsSuccess)
            {
                if (reload.Ambiguous)
                {
                    return reload.ToApplyResult(backupPath);
                }

                return await RollBackAsync(
                        profile,
                        executor,
                        original,
                        backupPath,
                        reloadRuntime: false,
                        "nginx returned a deterministic reload failure; the live file was restored from the verified backup.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var postValidation = await RunNginxTestAsync(executor, mutationHasStarted: true, cancellationToken).ConfigureAwait(false);
            if (!postValidation.IsSuccess)
            {
                if (postValidation.Ambiguous)
                {
                    return postValidation.ToApplyResult(backupPath);
                }

                return await RollBackAsync(
                        profile,
                        executor,
                        original,
                        backupPath,
                        reloadRuntime: true,
                        "Post-reload nginx verification failed; ServerDesk restored and reloaded the previous configuration.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var contentVerification = await VerifyLiveContentAsync(profile, original, candidateText, cancellationToken)
                .ConfigureAwait(false);
            if (contentVerification is not null)
            {
                return Ambiguous(
                    contentVerification,
                    backupPath);
            }

            var cleanup = await DeletePrivilegedTempAsync(executor, backupPath, cancellationToken).ConfigureAwait(false);
            backupCreated = !cleanup.IsSuccess;
            return new NginxSiteApplyResult(
                true,
                false,
                false,
                false,
                cleanup.IsSuccess
                    ? "nginx candidate validated in the live configuration context, replaced atomically, reloaded and verified."
                    : "nginx was applied and verified, but the recovery backup cleanup could not be confirmed. The backup may still exist.",
                RecoveryBackupPath: cleanup.IsSuccess ? null : backupPath);
        }
        catch (OperationCanceledException)
        {
            if (liveReplaced)
            {
                return Ambiguous(
                    "The nginx apply was cancelled after the live file had been replaced. Do not retry until the live configuration and runtime state are refreshed.",
                    backupCreated ? backupPath : null);
            }

            throw;
        }
        catch (RemoteFileSystemException exception)
        {
            return liveReplaced
                ? Ambiguous(
                    $"The nginx apply lost reliable file-system state after live replacement: {exception.Error.Message}",
                    backupCreated ? backupPath : null,
                    exception.Error.TechnicalDetails)
                : Failure(exception.Error, backupCreated ? backupPath : null);
        }
        finally
        {
            await BestEffortDeleteUserTempAsync(fileSystem, candidatePath).ConfigureAwait(false);
            await BestEffortDeleteUserTempAsync(fileSystem, helperPath).ConfigureAwait(false);
            if (rootHelperInstalled)
            {
                _ = await DeletePrivilegedTempAsync(executor, rootHelperPath, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async ValueTask<NginxSiteApplyResult?> ReloadForStaleCheckAsync(
        ServerProfile profile,
        NginxSiteEditDocument original,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _editor.LoadAsync(profile, original.CanonicalPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.Text, original.Document.Text, StringComparison.Ordinal) ||
                current.Metadata.UserId != original.Document.Metadata.UserId ||
                current.Metadata.GroupId != original.Document.Metadata.GroupId ||
                current.Metadata.Permissions != original.Document.Metadata.Permissions)
            {
                return Failure(
                    RemoteErrorCode.PathConflict,
                    "The nginx file or its ownership/mode changed after it was opened. Reload before applying changes.");
            }

            return null;
        }
        catch (RemoteFileSystemException exception)
        {
            return Failure(exception.Error);
        }
    }

    private async Task<NginxSiteApplyResult?> VerifyLiveContentAsync(
        ServerProfile profile,
        NginxSiteEditDocument original,
        string candidateText,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _editor.LoadAsync(profile, original.CanonicalPath, cancellationToken).ConfigureAwait(false);
            if (!string.Equals(current.Text, candidateText, StringComparison.Ordinal) ||
                current.Metadata.UserId != original.Document.Metadata.UserId ||
                current.Metadata.GroupId != original.Document.Metadata.GroupId ||
                current.Metadata.Permissions != original.Document.Metadata.Permissions)
            {
                return Ambiguous(
                    "nginx reload returned success, but the live file content or original owner/group/mode could not be verified.");
            }

            return null;
        }
        catch (OperationCanceledException)
        {
            return Ambiguous("nginx reload returned success, but post-reload file verification was cancelled.");
        }
        catch (RemoteFileSystemException exception)
        {
            return Ambiguous($"nginx reload returned success, but the live file could not be re-read: {exception.Error.Message}");
        }
    }

    private async Task<NginxSiteApplyResult> RollBackAsync(
        ServerProfile profile,
        IRemoteCommandExecutor executor,
        NginxSiteEditDocument original,
        RemotePath backupPath,
        bool reloadRuntime,
        string reason,
        CancellationToken cancellationToken)
    {
        var restore = await ExecuteCheckedAsync(
                executor,
                _options.PrivilegeExecutable,
                ["-n", "mv", "-f", "--", backupPath.Value, original.CanonicalPath.Value],
                OperationRisk.Destructive,
                "Could not restore the nginx recovery backup.",
                ambiguousOnTransport: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!restore.IsSuccess)
        {
            return restore.Ambiguous
                ? restore.ToApplyResult(backupPath)
                : Failure(
                    restore.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, restore.Message),
                    backupPath,
                    rolledBack: false);
        }

        var test = await RunNginxTestAsync(executor, mutationHasStarted: true, cancellationToken).ConfigureAwait(false);
        if (!test.IsSuccess)
        {
            return test.Ambiguous
                ? test.ToApplyResult()
                : new NginxSiteApplyResult(
                    false,
                    false,
                    true,
                    false,
                    $"{reason} The previous file was restored, but nginx validation of the restored file failed: {test.Message}",
                    test.Error);
        }

        if (reloadRuntime)
        {
            var reload = await ExecuteCheckedAsync(
                    executor,
                    _options.PrivilegeExecutable,
                    ["-n", _options.NginxExecutable, "-s", "reload"],
                    OperationRisk.Destructive,
                    "The previous nginx file was restored but runtime rollback reload failed.",
                    ambiguousOnTransport: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!reload.IsSuccess)
            {
                return reload.Ambiguous
                    ? reload.ToApplyResult()
                    : new NginxSiteApplyResult(
                        false,
                        false,
                        true,
                        false,
                        $"{reason} The previous file was restored, but its runtime reload failed: {reload.Message}",
                        reload.Error);
            }
        }

        var verified = await VerifyRollbackContentAsync(profile, original, cancellationToken).ConfigureAwait(false);
        if (verified is not null)
        {
            return verified;
        }

        return new NginxSiteApplyResult(
            false,
            false,
            true,
            false,
            reason,
            new RemoteError(RemoteErrorCode.CommandFailed, reason));
    }

    private async Task<NginxSiteApplyResult?> VerifyRollbackContentAsync(
        ServerProfile profile,
        NginxSiteEditDocument original,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _editor.LoadAsync(profile, original.CanonicalPath, cancellationToken).ConfigureAwait(false);
            if (string.Equals(current.Text, original.Document.Text, StringComparison.Ordinal) &&
                current.Metadata.UserId == original.Document.Metadata.UserId &&
                current.Metadata.GroupId == original.Document.Metadata.GroupId &&
                current.Metadata.Permissions == original.Document.Metadata.Permissions)
            {
                return null;
            }

            return Ambiguous("Rollback commands completed, but the restored nginx file or original metadata did not match the pre-edit snapshot.");
        }
        catch (OperationCanceledException)
        {
            return Ambiguous("Rollback commands completed, but rollback verification was cancelled.");
        }
        catch (RemoteFileSystemException exception)
        {
            return Ambiguous($"Rollback commands completed, but ServerDesk could not re-read the restored nginx file: {exception.Error.Message}");
        }
    }

    private Task<CommandCheck> RunNginxTestAsync(
        IRemoteCommandExecutor executor,
        bool mutationHasStarted,
        CancellationToken cancellationToken) =>
        ExecuteCheckedAsync(
            executor,
            _options.PrivilegeExecutable,
            ["-n", _options.NginxExecutable, "-t"],
            OperationRisk.ReadOnly,
            "nginx configuration validation failed.",
            ambiguousOnTransport: mutationHasStarted,
            cancellationToken);

    private async ValueTask<ResolvedPath> ResolveCanonicalPathAsync(
        ServerProfile profile,
        RemotePath requestedPath,
        CancellationToken cancellationToken)
    {
        await using var executor = _executorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    _options.ReadlinkExecutable,
                    ["-f", "--", requestedPath.Value],
                    _options.CommandTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return new ResolvedPath(null, execution.Error);
        }

        var command = execution.Command!;
        if (command.ExitCode != 0 || string.IsNullOrWhiteSpace(command.StandardOutput))
        {
            var detail = FirstUseful(command.StandardError, command.StandardOutput, $"Could not resolve nginx path '{requestedPath.Value}'.");
            return new ResolvedPath(null, new RemoteError(ClassifyFailure(detail), detail));
        }

        try
        {
            var path = RemotePath.Parse(command.StandardOutput.Trim().Split('\n')[0]);
            return path.IsAbsolute
                ? new ResolvedPath(path, null)
                : new ResolvedPath(null, new RemoteError(RemoteErrorCode.ParseFailed, "readlink returned a non-absolute nginx target path."));
        }
        catch (ArgumentException exception)
        {
            return new ResolvedPath(null, new RemoteError(RemoteErrorCode.ParseFailed, "readlink returned an invalid nginx target path.", exception.Message));
        }
    }

    private async Task<CommandCheck> DeletePrivilegedTempAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        CancellationToken cancellationToken) =>
        await ExecuteCheckedAsync(
                executor,
                _options.PrivilegeExecutable,
                ["-n", "rm", "-f", "--", path.Value],
                OperationRisk.Mutating,
                $"Could not remove temporary file '{path.Value}'.",
                ambiguousOnTransport: true,
                cancellationToken)
            .ConfigureAwait(false);

    private static async ValueTask UploadPrivateAsync(
        IRemoteFileSystem fileSystem,
        RemotePath path,
        byte[] content,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(content, writable: false);
        await fileSystem.UploadAsync(
                stream,
                path,
                content.Length,
                overwrite: false,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await fileSystem.SetPermissionsAsync(path, RemoteUnixPermissions.FromMode(600), cancellationToken)
            .ConfigureAwait(false);
    }

    private static async ValueTask BestEffortDeleteUserTempAsync(IRemoteFileSystem fileSystem, RemotePath path)
    {
        try
        {
            if (fileSystem.IsConnected)
            {
                await fileSystem.DeleteFileAsync(path, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
        }
    }

    private async Task<CommandCheck> ExecuteCheckedAsync(
        IRemoteCommandExecutor executor,
        string executable,
        IReadOnlyList<string> arguments,
        OperationRisk risk,
        string fallback,
        bool ambiguousOnTransport,
        CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    executable,
                    arguments,
                    _options.CommandTimeout,
                    risk,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            if (ambiguousOnTransport && IsPotentiallyAmbiguous(execution.Error.Code))
            {
                var error = new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    fallback + " ServerDesk lost a reliable completion signal; do not retry blindly.",
                    execution.Error.TechnicalDetails);
                return new CommandCheck(false, true, false, error.Message, error);
            }

            return new CommandCheck(false, false, false, execution.Error.Message, execution.Error);
        }

        var command = execution.Command!;
        if (command.ExitCode == 0)
        {
            return new CommandCheck(true, false, false, "Remote command completed.", null);
        }

        var detail = FirstUseful(command.StandardError, command.StandardOutput, fallback);
        var errorCode = ClassifyFailure(detail);
        var error = new RemoteError(errorCode, detail);
        return new CommandCheck(false, false, true, detail, error);
    }

    private static bool IsPotentiallyAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or RemoteErrorCode.OperationCancelled;

    private static RemoteErrorCode ClassifyFailure(string detail)
    {
        if (detail.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("sudoers", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not allowed to run sudo", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.SudoRequired;
        }

        if (detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PermissionDenied;
        }

        if (detail.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("no such file", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.CommandNotFound;
        }

        return RemoteErrorCode.CommandFailed;
    }

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        return !string.IsNullOrWhiteSpace(second) ? second.Trim() : fallback;
    }

    private static NginxSiteEditLoadResult LoadFailure(RemoteErrorCode code, string message) =>
        new(null, new RemoteError(code, message));

    private static NginxSiteApplyResult Failure(
        RemoteErrorCode code,
        string message,
        RemotePath? backupPath = null,
        bool rolledBack = false) =>
        Failure(new RemoteError(code, message), backupPath, rolledBack);

    private static NginxSiteApplyResult Failure(
        RemoteError error,
        RemotePath? backupPath = null,
        bool rolledBack = false) =>
        new(false, false, rolledBack, false, error.Message, error, backupPath);

    private static NginxSiteApplyResult Ambiguous(
        string message,
        RemotePath? backupPath = null,
        string? technicalDetails = null)
    {
        var error = new RemoteError(RemoteErrorCode.AmbiguousState, message, technicalDetails);
        return new NginxSiteApplyResult(false, false, false, true, message, error, backupPath);
    }

    private sealed record ResolvedPath(RemotePath? Path, RemoteError? Error);

    private sealed record CommandCheck(
        bool IsSuccess,
        bool Ambiguous,
        bool CommandRejected,
        string Message,
        RemoteError? Error)
    {
        public NginxSiteApplyResult ToApplyResult(
            RemotePath? backupPath = null,
            bool validationFailed = false) =>
            new(
                false,
                validationFailed,
                false,
                Ambiguous,
                Message,
                Error,
                backupPath);
    }
}

public sealed class AuditedNginxSiteEditingService : INginxSiteEditingService
{
    private readonly INginxSiteEditingService _inner;
    private readonly IOperationAudit _audit;

    public AuditedNginxSiteEditingService(INginxSiteEditingService inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public ValueTask<NginxSiteEditLoadResult> LoadAsync(
        ServerProfile profile,
        RemotePath requestedPath,
        CancellationToken cancellationToken = default) =>
        _inner.LoadAsync(profile, requestedPath, cancellationToken);

    public async Task<NginxSiteApplyResult> ApplyAsync(
        ServerProfile profile,
        NginxSiteEditDocument original,
        string candidateText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(original);
        try
        {
            var result = await _inner.ApplyAsync(profile, original, candidateText, cancellationToken).ConfigureAwait(false);
            var outcome = result.IsSuccess
                ? OperationOutcome.Succeeded
                : result.AmbiguousState
                    ? OperationOutcome.Unknown
                    : OperationOutcome.Failed;
            var persisted = await TryAuditAsync(profile, original.CanonicalPath, outcome, cancellationToken).ConfigureAwait(false);
            return persisted
                ? result
                : result with
                {
                    Message = result.Message + " Audit persistence failed; do not repeat the nginx apply solely to create an audit record.",
                };
        }
        catch (OperationCanceledException)
        {
            _ = await TryAuditAsync(
                    profile,
                    original.CanonicalPath,
                    OperationOutcome.Cancelled,
                    CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<bool> TryAuditAsync(
        ServerProfile profile,
        RemotePath canonicalPath,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = $"{profile.Username}@{profile.Host}:{profile.Port} nginx:{canonicalPath.Value}";
            var entry = OperationAuditEntry.Create(
                "nginx-site-apply",
                $"Validated nginx site apply requested for {canonicalPath.Value}",
                OperationRisk.Destructive,
                outcome,
                target);
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
