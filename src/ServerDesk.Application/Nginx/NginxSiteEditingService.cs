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
        if (!requestedPath.IsAbsolute || requestedPath.Value == "/")
        {
            return LoadFailure(RemoteErrorCode.InvalidEndpoint, "nginx site editing requires an absolute file path.");
        }

        var resolved = await ResolveAsync(profile, requestedPath, cancellationToken).ConfigureAwait(false);
        if (resolved.Error is not null || resolved.Path is null)
        {
            return new NginxSiteEditLoadResult(null, resolved.Error);
        }

        try
        {
            var document = await _editor.LoadAsync(profile, resolved.Path.Value, cancellationToken).ConfigureAwait(false);
            if (document.Metadata.Kind != RemoteFileKind.File)
            {
                return LoadFailure(RemoteErrorCode.PathConflict, "The resolved nginx site target must be a regular file.");
            }

            return new NginxSiteEditLoadResult(
                new NginxSiteEditDocument(requestedPath, resolved.Path.Value, document),
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
            return Failure(RemoteErrorCode.CapabilityUnavailable, "The nginx candidate exceeds the configured edit safety limit.");
        }

        if (string.Equals(original.Document.Text, candidateText, StringComparison.Ordinal))
        {
            return new NginxSiteApplyResult(true, false, false, false, "No nginx changes were detected; no mutation was sent.");
        }

        var resolved = await ResolveAsync(profile, original.RequestedPath, cancellationToken).ConfigureAwait(false);
        if (resolved.Error is not null || resolved.Path is null)
        {
            return Failure(resolved.Error ?? new RemoteError(RemoteErrorCode.PathNotFound, "The nginx target can no longer be resolved."));
        }

        if (resolved.Path.Value != original.CanonicalPath)
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                $"The nginx site path now resolves to '{resolved.Path.Value.Value}' instead of '{original.CanonicalPath.Value}'. Reload before applying.");
        }

        var stale = await CheckStaleAsync(profile, original, cancellationToken).ConfigureAwait(false);
        if (stale is not null)
        {
            return stale;
        }

        var metadata = original.Document.Metadata;
        if (metadata.UserId is not { } userId || metadata.GroupId is not { } groupId)
        {
            return Failure(RemoteErrorCode.CapabilityUnavailable, "Original nginx UID/GID is unavailable, so ServerDesk will not replace the file.");
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
            await UploadPrivateAsync(fileSystem, helperPath, Encoding.UTF8.GetBytes(ValidatorScript), cancellationToken).ConfigureAwait(false);

            var helperInstall = await RunAsync(
                    executor,
                    _options.PrivilegeExecutable,
                    ["-n", "install", "-m", "700", "-o", "0", "-g", "0", "--", helperPath.Value, rootHelperPath.Value],
                    OperationRisk.Mutating,
                    "Could not install the temporary root-owned nginx validator.",
                    ambiguousOnTransport: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!helperInstall.Success)
            {
                return ToApply(helperInstall, backupPath);
            }

            rootHelperInstalled = true;
            var validation = await RunAsync(
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

            var helperCleanup = await RemovePrivilegedAsync(executor, rootHelperPath, cancellationToken).ConfigureAwait(false);
            rootHelperInstalled = false;
            if (!helperCleanup.Success)
            {
                return ToApply(helperCleanup, backupPath);
            }

            if (!validation.Success)
            {
                var isValidationFailure = validation.Rejected && validation.Error?.Code == RemoteErrorCode.CommandFailed;
                return ToApply(validation, backupPath, validationFailed: isValidationFailure);
            }

            var backup = await RunAsync(
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
            if (!backup.Success)
            {
                return ToApply(backup, backupPath);
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
                return save.Error?.Code == RemoteErrorCode.AmbiguousState
                    ? Ambiguous(
                        "The nginx live-file replace lost a reliable completion signal. Do not retry until the live file and recovery backup are inspected.",
                        backupPath,
                        save.Error.TechnicalDetails)
                    : Failure(save.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, save.Message), backupPath);
            }

            liveReplaced = true;
            var liveTest = await RunNginxTestAsync(executor, ambiguousOnTransport: true, cancellationToken).ConfigureAwait(false);
            if (!liveTest.Success)
            {
                return liveTest.Ambiguous
                    ? ToApply(liveTest, backupPath)
                    : await RollBackAsync(profile, executor, original, backupPath, reloadRuntime: false,
                        "The candidate passed isolated validation but failed the real nginx configuration test after replacement.", cancellationToken)
                        .ConfigureAwait(false);
            }

            var reload = await RunAsync(
                    executor,
                    _options.PrivilegeExecutable,
                    ["-n", _options.NginxExecutable, "-s", "reload"],
                    OperationRisk.Destructive,
                    "nginx reload failed.",
                    ambiguousOnTransport: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!reload.Success)
            {
                return reload.Ambiguous
                    ? ToApply(reload, backupPath)
                    : await RollBackAsync(profile, executor, original, backupPath, reloadRuntime: false,
                        "nginx returned a deterministic reload failure; the previous file was restored.", cancellationToken)
                        .ConfigureAwait(false);
            }

            var postTest = await RunNginxTestAsync(executor, ambiguousOnTransport: true, cancellationToken).ConfigureAwait(false);
            if (!postTest.Success)
            {
                return postTest.Ambiguous
                    ? ToApply(postTest, backupPath)
                    : await RollBackAsync(profile, executor, original, backupPath, reloadRuntime: true,
                        "Post-reload nginx verification failed; ServerDesk restored and reloaded the previous configuration.", cancellationToken)
                        .ConfigureAwait(false);
            }

            var verified = await VerifyContentAsync(profile, original, candidateText, cancellationToken).ConfigureAwait(false);
            if (!verified.Success)
            {
                return Ambiguous(verified.Message, backupPath, verified.Error?.TechnicalDetails);
            }

            var cleanup = await RemovePrivilegedAsync(executor, backupPath, cancellationToken).ConfigureAwait(false);
            backupCreated = !cleanup.Success;
            return new NginxSiteApplyResult(
                true,
                false,
                false,
                false,
                cleanup.Success
                    ? "nginx candidate validated in live context, replaced atomically, reloaded and verified."
                    : "nginx was applied and verified, but recovery-backup cleanup could not be confirmed.",
                RecoveryBackupPath: cleanup.Success ? null : backupPath);
        }
        catch (OperationCanceledException)
        {
            if (liveReplaced)
            {
                return Ambiguous(
                    "The nginx apply was cancelled after live replacement. Refresh live file and runtime state before any retry.",
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
            await BestEffortDeleteAsync(fileSystem, candidatePath).ConfigureAwait(false);
            await BestEffortDeleteAsync(fileSystem, helperPath).ConfigureAwait(false);
            if (rootHelperInstalled)
            {
                _ = await RemovePrivilegedAsync(executor, rootHelperPath, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<NginxSiteApplyResult?> CheckStaleAsync(
        ServerProfile profile,
        NginxSiteEditDocument original,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _editor.LoadAsync(profile, original.CanonicalPath, cancellationToken).ConfigureAwait(false);
            return SameSnapshot(current, original.Document)
                ? null
                : Failure(RemoteErrorCode.PathConflict, "The nginx file or its ownership/mode changed after it was opened. Reload before applying.");
        }
        catch (RemoteFileSystemException exception)
        {
            return Failure(exception.Error);
        }
    }

    private async Task<StepResult> VerifyContentAsync(
        ServerProfile profile,
        NginxSiteEditDocument original,
        string expectedText,
        CancellationToken cancellationToken)
    {
        try
        {
            var current = await _editor.LoadAsync(profile, original.CanonicalPath, cancellationToken).ConfigureAwait(false);
            var same = string.Equals(current.Text, expectedText, StringComparison.Ordinal) &&
                SameMetadata(current, original.Document);
            return same
                ? StepResult.Ok()
                : StepResult.Fail(new RemoteError(RemoteErrorCode.AmbiguousState,
                    "nginx reload returned success, but live content or original owner/group/mode could not be verified."), ambiguous: true);
        }
        catch (OperationCanceledException)
        {
            return StepResult.Fail(new RemoteError(RemoteErrorCode.AmbiguousState,
                "nginx reload returned success, but content verification was cancelled."), ambiguous: true);
        }
        catch (RemoteFileSystemException exception)
        {
            return StepResult.Fail(new RemoteError(RemoteErrorCode.AmbiguousState,
                $"nginx reload returned success, but the live file could not be re-read: {exception.Error.Message}",
                exception.Error.TechnicalDetails), ambiguous: true);
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
        var restore = await RunAsync(
                executor,
                _options.PrivilegeExecutable,
                ["-n", "mv", "-f", "--", backupPath.Value, original.CanonicalPath.Value],
                OperationRisk.Destructive,
                "Could not restore the nginx recovery backup.",
                ambiguousOnTransport: true,
                cancellationToken)
            .ConfigureAwait(false);
        if (!restore.Success)
        {
            return ToApply(restore, backupPath);
        }

        var test = await RunNginxTestAsync(executor, ambiguousOnTransport: true, cancellationToken).ConfigureAwait(false);
        if (!test.Success)
        {
            return test.Ambiguous
                ? ToApply(test)
                : new NginxSiteApplyResult(false, false, true, false,
                    $"{reason} The previous file was restored, but validation of the restored config failed: {test.Message}", test.Error);
        }

        if (reloadRuntime)
        {
            var reload = await RunAsync(
                    executor,
                    _options.PrivilegeExecutable,
                    ["-n", _options.NginxExecutable, "-s", "reload"],
                    OperationRisk.Destructive,
                    "The previous nginx file was restored but runtime rollback reload failed.",
                    ambiguousOnTransport: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!reload.Success)
            {
                return reload.Ambiguous
                    ? ToApply(reload)
                    : new NginxSiteApplyResult(false, false, true, false,
                        $"{reason} The previous file was restored, but runtime rollback reload failed: {reload.Message}", reload.Error);
            }
        }

        try
        {
            var current = await _editor.LoadAsync(profile, original.CanonicalPath, cancellationToken).ConfigureAwait(false);
            if (!SameSnapshot(current, original.Document))
            {
                return Ambiguous("Rollback completed, but restored nginx content or metadata did not match the pre-edit snapshot.");
            }
        }
        catch (OperationCanceledException)
        {
            return Ambiguous("Rollback completed, but rollback verification was cancelled.");
        }
        catch (RemoteFileSystemException exception)
        {
            return Ambiguous($"Rollback completed, but the restored file could not be re-read: {exception.Error.Message}");
        }

        var error = new RemoteError(RemoteErrorCode.CommandFailed, reason);
        return new NginxSiteApplyResult(false, false, true, false, reason, error);
    }

    private async ValueTask<ResolvedPath> ResolveAsync(
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
            var detail = Useful(command.StandardError, command.StandardOutput, "Could not resolve the nginx file path.");
            return new ResolvedPath(null, new RemoteError(Classify(detail), detail));
        }

        try
        {
            var path = RemotePath.Parse(command.StandardOutput.Trim().Split('\n')[0]);
            return path.IsAbsolute
                ? new ResolvedPath(path, null)
                : new ResolvedPath(null, new RemoteError(RemoteErrorCode.ParseFailed, "readlink returned a non-absolute path."));
        }
        catch (ArgumentException exception)
        {
            return new ResolvedPath(null, new RemoteError(RemoteErrorCode.ParseFailed, "readlink returned an invalid path.", exception.Message));
        }
    }

    private Task<StepResult> RunNginxTestAsync(
        IRemoteCommandExecutor executor,
        bool ambiguousOnTransport,
        CancellationToken cancellationToken) =>
        RunAsync(
            executor,
            _options.PrivilegeExecutable,
            ["-n", _options.NginxExecutable, "-t"],
            OperationRisk.ReadOnly,
            "nginx configuration validation failed.",
            ambiguousOnTransport,
            cancellationToken);

    private Task<StepResult> RemovePrivilegedAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        CancellationToken cancellationToken) =>
        RunAsync(
            executor,
            _options.PrivilegeExecutable,
            ["-n", "rm", "-f", "--", path.Value],
            OperationRisk.Mutating,
            $"Could not remove temporary file '{path.Value}'.",
            ambiguousOnTransport: true,
            cancellationToken);

    private async Task<StepResult> RunAsync(
        IRemoteCommandExecutor executor,
        string executable,
        IReadOnlyList<string> arguments,
        OperationRisk risk,
        string fallback,
        bool ambiguousOnTransport,
        CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(executable, arguments, _options.CommandTimeout, risk, StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            if (ambiguousOnTransport && IsTransportAmbiguous(execution.Error.Code))
            {
                return StepResult.Fail(
                    new RemoteError(RemoteErrorCode.AmbiguousState,
                        fallback + " ServerDesk lost a reliable completion signal; do not retry blindly.",
                        execution.Error.TechnicalDetails),
                    ambiguous: true);
            }

            return StepResult.Fail(execution.Error);
        }

        var command = execution.Command!;
        if (command.ExitCode == 0)
        {
            return StepResult.Ok();
        }

        var detail = Useful(command.StandardError, command.StandardOutput, fallback);
        return StepResult.Fail(new RemoteError(Classify(detail), detail), rejected: true);
    }

    private static async ValueTask UploadPrivateAsync(
        IRemoteFileSystem fileSystem,
        RemotePath path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new MemoryStream(bytes, writable: false);
        await fileSystem.UploadAsync(stream, path, bytes.Length, overwrite: false, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        await fileSystem.SetPermissionsAsync(path, RemoteUnixPermissions.FromMode(600), cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask BestEffortDeleteAsync(IRemoteFileSystem fileSystem, RemotePath path)
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

    private static bool SameSnapshot(RemoteEditorDocument current, RemoteEditorDocument original) =>
        string.Equals(current.Text, original.Text, StringComparison.Ordinal) && SameMetadata(current, original);

    private static bool SameMetadata(RemoteEditorDocument current, RemoteEditorDocument original) =>
        current.Metadata.UserId == original.Metadata.UserId &&
        current.Metadata.GroupId == original.Metadata.GroupId &&
        current.Metadata.Permissions == original.Metadata.Permissions;

    private static bool IsTransportAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or RemoteErrorCode.OperationCancelled;

    private static RemoteErrorCode Classify(string detail)
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

    private static string Useful(string first, string second, string fallback) =>
        !string.IsNullOrWhiteSpace(first) ? first.Trim() : !string.IsNullOrWhiteSpace(second) ? second.Trim() : fallback;

    private static NginxSiteEditLoadResult LoadFailure(RemoteErrorCode code, string message) =>
        new(null, new RemoteError(code, message));

    private static NginxSiteApplyResult Failure(RemoteErrorCode code, string message, RemotePath? backup = null) =>
        Failure(new RemoteError(code, message), backup);

    private static NginxSiteApplyResult Failure(RemoteError error, RemotePath? backup = null) =>
        new(false, false, false, false, error.Message, error, backup);

    private static NginxSiteApplyResult Ambiguous(string message, RemotePath? backup = null, string? technical = null)
    {
        var error = new RemoteError(RemoteErrorCode.AmbiguousState, message, technical);
        return new NginxSiteApplyResult(false, false, false, true, message, error, backup);
    }

    private static NginxSiteApplyResult ToApply(StepResult step, RemotePath? backup = null, bool validationFailed = false) =>
        new(false, validationFailed, false, step.Ambiguous, step.Message, step.Error, backup);

    private sealed record ResolvedPath(RemotePath? Path, RemoteError? Error);

    private sealed record StepResult(bool Success, bool Ambiguous, bool Rejected, string Message, RemoteError? Error)
    {
        public static StepResult Ok() => new(true, false, false, "Remote command completed.", null);

        public static StepResult Fail(RemoteError error, bool ambiguous = false, bool rejected = false) =>
            new(false, ambiguous, rejected, error.Message, error);
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
            var audited = await TryAuditAsync(profile, original.CanonicalPath, outcome, cancellationToken).ConfigureAwait(false);
            return audited
                ? result
                : result with { Message = result.Message + " Audit persistence failed; do not repeat the nginx apply solely to create an audit record." };
        }
        catch (OperationCanceledException)
        {
            _ = await TryAuditAsync(profile, original.CanonicalPath, OperationOutcome.Cancelled, CancellationToken.None)
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
            var entry = OperationAuditEntry.Create(
                "nginx-site-apply",
                $"Validated nginx site apply requested for {canonicalPath.Value}",
                OperationRisk.Destructive,
                outcome,
                $"{profile.Username}@{profile.Host}:{profile.Port} nginx:{canonicalPath.Value}");
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
