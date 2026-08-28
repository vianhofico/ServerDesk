using System.Text;
using ServerDesk.Application.Audit;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.EnvironmentFiles;

public sealed class EnvironmentFileService : IEnvironmentFileService
{
    private readonly IRemoteFileEditorService _editor;
    private readonly EnvironmentFileOptions _options;

    public EnvironmentFileService(IRemoteFileEditorService editor, EnvironmentFileOptions options)
    {
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async ValueTask<EnvironmentFileLoadResult> LoadAsync(
        ServerProfile profile,
        RemotePath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (!path.IsAbsolute || path.Value == "/")
        {
            return FailureLoad(RemoteErrorCode.InvalidEndpoint, "Environment-file inspection requires an explicit absolute remote file path.");
        }

        try
        {
            var document = await _editor.LoadAsync(profile, path, cancellationToken).ConfigureAwait(false);
            if (document.Metadata.Kind != RemoteFileKind.File)
            {
                return FailureLoad(RemoteErrorCode.PathConflict, "Environment-file editing requires an existing regular file. Symbolic links must be resolved explicitly first.");
            }

            if (document.Metadata.Size > _options.MaximumCandidateBytes)
            {
                return FailureLoad(RemoteErrorCode.CapabilityUnavailable, $"Environment files larger than {_options.MaximumCandidateBytes} bytes are not editable in this workflow.");
            }

            return new EnvironmentFileLoadResult(BuildSnapshot(path, document), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RemoteFileSystemException exception)
        {
            return new EnvironmentFileLoadResult(null, exception.Error);
        }
    }

    public async ValueTask<EnvironmentFileApplyResult> ApplyAsync(
        ServerProfile profile,
        EnvironmentFileSnapshot original,
        string candidateText,
        EnvironmentFileValidationSpec? validation = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(original);
        candidateText ??= string.Empty;
        if (Encoding.UTF8.GetByteCount(candidateText) > _options.MaximumCandidateBytes)
        {
            return FailureApply(RemoteErrorCode.CapabilityUnavailable, "The candidate environment file exceeds the configured safety limit.");
        }

        var validationResult = BuildValidation(validation);
        if (validationResult.Error is not null)
        {
            return new EnvironmentFileApplyResult(false, false, false, validationResult.Error.Message, validationResult.Error);
        }

        var mutationStarted = false;
        try
        {
            var current = await _editor.LoadAsync(profile, original.Path, cancellationToken).ConfigureAwait(false);
            if (HasChangedSinceLoad(original.Original, current))
            {
                return FailureApply(
                    RemoteErrorCode.PathConflict,
                    "The remote environment file changed after it was loaded. Reload the live file and reapply your edits instead of overwriting it.");
            }

            mutationStarted = true;
            var saved = await _editor.SavePrivilegedAsync(
                    profile,
                    original.Original,
                    candidateText,
                    validationResult.Spec,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!saved.IsSuccess)
            {
                if (saved.ValidationFailed)
                {
                    return new EnvironmentFileApplyResult(
                        false,
                        true,
                        false,
                        "The deployment-specific validator rejected the staged environment file. Validator output is intentionally not surfaced because it may contain secret values.");
                }

                if (saved.Error?.Code == RemoteErrorCode.AmbiguousState)
                {
                    return Ambiguous("The environment-file apply lost a reliable completion signal. Reload the live file before deciding whether to retry.");
                }

                return new EnvironmentFileApplyResult(
                    false,
                    false,
                    false,
                    SafeFailureMessage(saved.Error),
                    saved.Error);
            }

            EnvironmentFileLoadResult reloaded;
            try
            {
                reloaded = await LoadAsync(profile, original.Path, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return Ambiguous("The environment file was replaced but post-apply verification was cancelled. Reload before another mutation.");
            }

            if (!reloaded.IsSuccess || reloaded.Snapshot is null)
            {
                return Ambiguous("The environment file was replaced but ServerDesk could not verify the live file afterwards. Reload before another mutation.");
            }

            if (!string.Equals(reloaded.Snapshot.Text, candidateText, StringComparison.Ordinal) ||
                !MetadataPolicyMatches(original.Original.Metadata, reloaded.Snapshot.Original.Metadata))
            {
                return Ambiguous("The environment file was replaced but verified content or mode/UID/GID did not match the requested atomic apply.");
            }

            return new EnvironmentFileApplyResult(
                true,
                false,
                false,
                "Environment file replaced atomically and verified with original mode/UID/GID preserved.",
                Snapshot: reloaded.Snapshot);
        }
        catch (OperationCanceledException) when (mutationStarted)
        {
            return Ambiguous("The environment-file apply was cancelled after mutation started. Reload the live file before deciding whether to retry.");
        }
        catch (RemoteFileSystemException exception)
        {
            return mutationStarted && IsPotentiallyAmbiguous(exception.Error.Code)
                ? Ambiguous("The environment-file apply lost a reliable completion signal. Reload the live file before deciding whether to retry.")
                : new EnvironmentFileApplyResult(false, false, false, SafeFailureMessage(exception.Error), exception.Error);
        }
    }

    private static EnvironmentFileSnapshot BuildSnapshot(RemotePath path, RemoteEditorDocument document)
    {
        var parsed = EnvironmentFileParser.Parse(document.Text);
        return new EnvironmentFileSnapshot(
            path,
            document,
            parsed.Lines,
            parsed.Entries,
            parsed.HasUnsupportedLines,
            parsed.NewLine);
    }

    private static bool HasChangedSinceLoad(RemoteEditorDocument original, RemoteEditorDocument current) =>
        !string.Equals(original.Text, current.Text, StringComparison.Ordinal) ||
        original.Metadata.Size != current.Metadata.Size ||
        original.Metadata.LastWriteTimeUtc != current.Metadata.LastWriteTimeUtc ||
        !MetadataPolicyMatches(original.Metadata, current.Metadata);

    private static bool MetadataPolicyMatches(RemoteFileEntry original, RemoteFileEntry current) =>
        original.Kind == current.Kind &&
        original.UserId == current.UserId &&
        original.GroupId == current.GroupId &&
        original.Permissions == current.Permissions;

    private static ValidationBuildResult BuildValidation(EnvironmentFileValidationSpec? validation)
    {
        if (validation is null)
        {
            return new ValidationBuildResult(null, null);
        }

        if (string.IsNullOrWhiteSpace(validation.Executable) ||
            validation.Executable.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            return ValidationFailure("The environment-file validator executable is invalid.");
        }

        var executable = validation.Executable.Trim();
        var arguments = validation.Arguments.ToArray();
        foreach (var argument in arguments)
        {
            if (argument.IndexOfAny(['\r', '\n', '\0']) >= 0)
            {
                return ValidationFailure("Environment-file validator arguments cannot contain control-line characters.");
            }

            var withoutFilePlaceholder = argument.Replace("{file}", string.Empty, StringComparison.Ordinal);
            if (withoutFilePlaceholder.Contains('{') || withoutFilePlaceholder.Contains('}'))
            {
                return ValidationFailure("Environment-file validators support only the typed {file} staged-path placeholder.");
            }
        }

        if (!IsSupportedNonExecutingValidator(executable, arguments))
        {
            return ValidationFailure(
                "Only explicitly supported non-executing environment-file validator shapes are allowed. Generic interpreters and arbitrary executables are rejected.");
        }

        return new ValidationBuildResult(new RemoteEditValidationSpec(executable, arguments), null);
    }

    private static bool IsSupportedNonExecutingValidator(string executable, IReadOnlyList<string> arguments)
    {
        var separator = executable.LastIndexOf('/');
        var executableName = separator >= 0 ? executable[(separator + 1)..] : executable;
        if (string.Equals(executableName, "docker", StringComparison.OrdinalIgnoreCase))
        {
            return arguments.SequenceEqual(
                ["compose", "--env-file", "{file}", "config", "--quiet"],
                StringComparer.Ordinal);
        }

        if (string.Equals(executableName, "docker-compose", StringComparison.OrdinalIgnoreCase))
        {
            return arguments.SequenceEqual(
                ["--env-file", "{file}", "config", "--quiet"],
                StringComparer.Ordinal);
        }

        return false;
    }

    private static ValidationBuildResult ValidationFailure(string message) =>
        new(null, new RemoteError(RemoteErrorCode.InvalidEndpoint, message));

    private static string SafeFailureMessage(RemoteError? error) => error?.Code switch
    {
        RemoteErrorCode.SudoRequired => "Passwordless privileged apply is unavailable for this environment file.",
        RemoteErrorCode.PermissionDenied => "Permission was denied while applying the environment file.",
        RemoteErrorCode.PathNotFound => "The environment file no longer exists at the selected path.",
        RemoteErrorCode.PathConflict => "The environment-file target changed or is no longer a compatible regular file.",
        RemoteErrorCode.NetworkInterrupted => "The connection was interrupted before ServerDesk could confirm the environment-file operation.",
        RemoteErrorCode.CommandTimeout => "The environment-file operation timed out.",
        _ => "The environment-file operation failed. Reload the live file before retrying.",
    };

    private static bool IsPotentiallyAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or RemoteErrorCode.OperationCancelled;

    private static EnvironmentFileLoadResult FailureLoad(RemoteErrorCode code, string message) =>
        new(null, new RemoteError(code, message));

    private static EnvironmentFileApplyResult FailureApply(RemoteErrorCode code, string message)
    {
        var error = new RemoteError(code, message);
        return new EnvironmentFileApplyResult(false, false, false, message, error);
    }

    private static EnvironmentFileApplyResult Ambiguous(string message) =>
        new(false, false, true, message, new RemoteError(RemoteErrorCode.AmbiguousState, message));

    private sealed record ValidationBuildResult(RemoteEditValidationSpec? Spec, RemoteError? Error);
}

public sealed class AuditedEnvironmentFileService : IEnvironmentFileService
{
    private readonly IEnvironmentFileService _inner;
    private readonly IOperationAudit _audit;

    public AuditedEnvironmentFileService(IEnvironmentFileService inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public ValueTask<EnvironmentFileLoadResult> LoadAsync(
        ServerProfile profile,
        RemotePath path,
        CancellationToken cancellationToken = default) =>
        _inner.LoadAsync(profile, path, cancellationToken);

    public async ValueTask<EnvironmentFileApplyResult> ApplyAsync(
        ServerProfile profile,
        EnvironmentFileSnapshot original,
        string candidateText,
        EnvironmentFileValidationSpec? validation = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _inner.ApplyAsync(profile, original, candidateText, validation, cancellationToken).ConfigureAwait(false);
        await AuditAsync(profile, original.Path, result, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task AuditAsync(
        ServerProfile profile,
        RemotePath path,
        EnvironmentFileApplyResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = result.IsSuccess
                ? OperationOutcome.Succeeded
                : result.AmbiguousState
                    ? OperationOutcome.Unknown
                    : OperationOutcome.Failed;
            var target = $"{profile.Username}@{profile.Host}:{profile.Port} env-file:{path.Value}";
            var entry = OperationAuditEntry.Create(
                "environment-file",
                "Environment-file apply requested",
                OperationRisk.Mutating,
                outcome,
                target);
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Audit failure must never cause an environment-file mutation to be retried.
        }
    }
}
