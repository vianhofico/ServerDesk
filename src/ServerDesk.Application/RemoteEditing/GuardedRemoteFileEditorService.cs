using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.RemoteEditing;

public sealed class GuardedRemoteFileEditorService : IRemoteFileEditorService
{
    private readonly IRemoteFileSystemFactory _fileSystemFactory;
    private readonly RemoteFileEditorService _inner;

    public GuardedRemoteFileEditorService(
        IRemoteFileSystemFactory fileSystemFactory,
        IRemoteCommandExecutorFactory commandExecutorFactory)
    {
        _fileSystemFactory = fileSystemFactory ?? throw new ArgumentNullException(nameof(fileSystemFactory));
        _inner = new RemoteFileEditorService(
            _fileSystemFactory,
            commandExecutorFactory ?? throw new ArgumentNullException(nameof(commandExecutorFactory)));
    }

    public ValueTask<RemoteEditorDocument> LoadAsync(
        ServerProfile profile,
        RemotePath path,
        CancellationToken cancellationToken = default) =>
        _inner.LoadAsync(profile, path, cancellationToken);

    public ValueTask<RemoteEditorSaveResult> SaveWritableAsync(
        ServerProfile profile,
        RemoteEditorDocument original,
        string editedText,
        CancellationToken cancellationToken = default) =>
        _inner.SaveWritableAsync(profile, original, editedText, cancellationToken);

    public async ValueTask<RemoteEditorSaveResult> SavePrivilegedAsync(
        ServerProfile profile,
        RemoteEditorDocument original,
        string editedText,
        RemoteEditValidationSpec? validation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(original);

        var targetIdentity = await InspectTargetIdentityAsync(profile, original.Metadata.Path, cancellationToken)
            .ConfigureAwait(false);
        if (targetIdentity.Error is not null)
        {
            return new RemoteEditorSaveResult(false, false, targetIdentity.Error.Message, targetIdentity.Error);
        }

        if (targetIdentity.Entry?.Kind == RemoteFileKind.SymbolicLink)
        {
            return Failure(
                RemoteErrorCode.CapabilityUnavailable,
                "Privileged atomic save does not replace symbolic links. Open the resolved target file explicitly before editing.");
        }

        if (targetIdentity.Entry?.Kind != RemoteFileKind.File)
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "Privileged atomic save requires an existing regular-file target.");
        }

        var result = await _inner.SavePrivilegedAsync(
                profile,
                original,
                editedText,
                validation,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            return IsPotentiallyAmbiguous(result.Error?.Code)
                ? Failure(
                    RemoteErrorCode.AmbiguousState,
                    "The privileged save lost a reliable completion signal. Reload the live file before deciding whether to retry.")
                : result;
        }

        try
        {
            var verified = await _inner.LoadAsync(profile, original.Metadata.Path, cancellationToken)
                .ConfigureAwait(false);
            if (!string.Equals(verified.Text, editedText ?? string.Empty, StringComparison.Ordinal) ||
                verified.Metadata.Permissions != original.Metadata.Permissions ||
                verified.Metadata.UserId != original.Metadata.UserId ||
                verified.Metadata.GroupId != original.Metadata.GroupId)
            {
                return Failure(
                    RemoteErrorCode.AmbiguousState,
                    "The privileged replace completed, but post-save verification did not match the requested content or original ownership/mode metadata.");
            }

            return result with
            {
                Message = "Privileged file replaced atomically and verified with original mode/UID/GID preserved.",
            };
        }
        catch (OperationCanceledException)
        {
            return Failure(
                RemoteErrorCode.AmbiguousState,
                "The privileged replace completed but verification was cancelled. Reload the live file before another mutation.");
        }
        catch (RemoteFileSystemException exception)
        {
            return Failure(
                RemoteErrorCode.AmbiguousState,
                $"The privileged replace completed but ServerDesk could not verify the live file: {exception.Error.Message}");
        }
    }

    private async ValueTask<TargetIdentityResult> InspectTargetIdentityAsync(
        ServerProfile profile,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        if (path.Value is "/" or ".")
        {
            return new TargetIdentityResult(
                null,
                new RemoteError(RemoteErrorCode.PathConflict, "The remote editor target must identify a file."));
        }

        await using var fileSystem = _fileSystemFactory.Create(profile);
        try
        {
            await fileSystem.ConnectAsync(cancellationToken).ConfigureAwait(false);
            var entries = await fileSystem.ListAsync(path.Parent, cancellationToken).ConfigureAwait(false);
            var entry = entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, path.Name, StringComparison.Ordinal));
            return entry is null
                ? new TargetIdentityResult(
                    null,
                    new RemoteError(RemoteErrorCode.PathNotFound, $"Remote file '{path.Value}' no longer exists."))
                : new TargetIdentityResult(entry, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RemoteFileSystemException exception)
        {
            return new TargetIdentityResult(null, exception.Error);
        }
    }

    private static bool IsPotentiallyAmbiguous(RemoteErrorCode? code) =>
        code is RemoteErrorCode.NetworkInterrupted or
            RemoteErrorCode.CommandTimeout or
            RemoteErrorCode.OperationCancelled;

    private static RemoteEditorSaveResult Failure(RemoteErrorCode code, string message)
    {
        var error = new RemoteError(code, message);
        return new RemoteEditorSaveResult(false, false, message, error);
    }

    private sealed record TargetIdentityResult(RemoteFileEntry? Entry, RemoteError? Error);
}
