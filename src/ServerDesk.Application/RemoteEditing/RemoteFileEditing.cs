using System.Text;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.RemoteEditing;

public sealed record RemoteEditorDocument(RemoteFileEntry Metadata, string Text);

public sealed record RemoteEditorDiff(int AddedLines, int RemovedLines, int ChangedLines)
{
    public int TotalChanges => AddedLines + RemovedLines + ChangedLines;

    public string Summary => TotalChanges == 0
        ? "No changes"
        : $"{ChangedLines} changed · {AddedLines} added · {RemovedLines} removed";

    public static RemoteEditorDiff Calculate(string original, string edited)
    {
        original ??= string.Empty;
        edited ??= string.Empty;
        var before = SplitLines(original);
        var after = SplitLines(edited);
        var common = Math.Min(before.Length, after.Length);
        var changed = 0;
        for (var index = 0; index < common; index++)
        {
            if (!string.Equals(before[index], after[index], StringComparison.Ordinal))
            {
                changed++;
            }
        }

        return new RemoteEditorDiff(
            Math.Max(0, after.Length - before.Length),
            Math.Max(0, before.Length - after.Length),
            changed);
    }

    private static string[] SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
}

public sealed record RemoteEditValidationSpec(string Executable, IReadOnlyList<string> Arguments)
{
    public IReadOnlyList<string> Resolve(RemotePath stagedFile)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Executable);
        return Arguments
            .Select(argument => argument.Replace("{file}", stagedFile.Value, StringComparison.Ordinal))
            .ToArray();
    }
}

public sealed record RemoteEditorSaveResult(
    bool IsSuccess,
    bool ValidationFailed,
    string Message,
    RemoteError? Error = null);

public interface IRemoteFileEditorService
{
    ValueTask<RemoteEditorDocument> LoadAsync(
        ServerProfile profile,
        RemotePath path,
        CancellationToken cancellationToken = default);

    ValueTask<RemoteEditorSaveResult> SaveWritableAsync(
        ServerProfile profile,
        RemoteEditorDocument original,
        string editedText,
        CancellationToken cancellationToken = default);

    ValueTask<RemoteEditorSaveResult> SavePrivilegedAsync(
        ServerProfile profile,
        RemoteEditorDocument original,
        string editedText,
        RemoteEditValidationSpec? validation,
        CancellationToken cancellationToken = default);
}

public sealed class RemoteFileEditorService : IRemoteFileEditorService
{
    private const long MaximumEditableBytes = 4L * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly IRemoteFileSystemFactory _fileSystemFactory;
    private readonly IRemoteCommandExecutorFactory _commandExecutorFactory;

    public RemoteFileEditorService(
        IRemoteFileSystemFactory fileSystemFactory,
        IRemoteCommandExecutorFactory commandExecutorFactory)
    {
        _fileSystemFactory = fileSystemFactory ?? throw new ArgumentNullException(nameof(fileSystemFactory));
        _commandExecutorFactory = commandExecutorFactory ?? throw new ArgumentNullException(nameof(commandExecutorFactory));
    }

    public async ValueTask<RemoteEditorDocument> LoadAsync(
        ServerProfile profile,
        RemotePath path,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var fileSystem = _fileSystemFactory.Create(profile);
        await fileSystem.ConnectAsync(cancellationToken).ConfigureAwait(false);
        var metadata = await fileSystem.StatAsync(path, cancellationToken).ConfigureAwait(false);
        if (metadata.Kind is not (RemoteFileKind.File or RemoteFileKind.SymbolicLink))
        {
            throw new RemoteFileSystemException(new RemoteError(
                RemoteErrorCode.PathConflict,
                $"Remote path '{path.Value}' is not an editable file."));
        }

        if (metadata.Size > MaximumEditableBytes)
        {
            throw new RemoteFileSystemException(new RemoteError(
                RemoteErrorCode.CapabilityUnavailable,
                $"Remote editor is limited to {MaximumEditableBytes / 1024 / 1024} MiB text files."));
        }

        await using var buffer = new MemoryStream(metadata.Size is > 0 and <= int.MaxValue ? (int)metadata.Size : 0);
        await fileSystem.DownloadAsync(path, buffer, cancellationToken: cancellationToken).ConfigureAwait(false);
        try
        {
            return new RemoteEditorDocument(metadata, StrictUtf8.GetString(buffer.ToArray()));
        }
        catch (DecoderFallbackException exception)
        {
            throw new RemoteFileSystemException(
                new RemoteError(RemoteErrorCode.ParseFailed, "The remote file is not valid UTF-8 text."),
                exception);
        }
    }

    public async ValueTask<RemoteEditorSaveResult> SaveWritableAsync(
        ServerProfile profile,
        RemoteEditorDocument original,
        string editedText,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(original);
        var content = StrictUtf8.GetBytes(editedText ?? string.Empty);

        await using var fileSystem = _fileSystemFactory.Create(profile);
        try
        {
            await fileSystem.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await using var source = new MemoryStream(content, writable: false);
            await fileSystem.UploadAsync(
                    source,
                    original.Metadata.Path,
                    content.Length,
                    overwrite: true,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await fileSystem.SetPermissionsAsync(
                    original.Metadata.Path,
                    original.Metadata.Permissions,
                    cancellationToken)
                .ConfigureAwait(false);
            return new RemoteEditorSaveResult(true, false, "Remote file saved atomically through SFTP.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RemoteFileSystemException exception)
        {
            return new RemoteEditorSaveResult(false, false, exception.Error.Message, exception.Error);
        }
    }

    public async ValueTask<RemoteEditorSaveResult> SavePrivilegedAsync(
        ServerProfile profile,
        RemoteEditorDocument original,
        string editedText,
        RemoteEditValidationSpec? validation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(original);
        var target = original.Metadata.Path;
        if (!target.IsAbsolute)
        {
            return Failure(RemoteErrorCode.InvalidEndpoint, "Privileged editing requires an absolute remote path.");
        }

        if (original.Metadata.UserId is not { } userId || original.Metadata.GroupId is not { } groupId)
        {
            return Failure(
                RemoteErrorCode.CapabilityUnavailable,
                "ServerDesk will not replace a privileged file when its original UID/GID cannot be preserved.");
        }

        var token = Guid.NewGuid().ToString("N");
        var userStage = RemotePath.Parse($"/tmp/serverdesk-edit-{token}.tmp");
        var privilegedStage = target.Parent.Combine($".serverdesk-edit-{token}.new");
        var content = StrictUtf8.GetBytes(editedText ?? string.Empty);
        var privilegedStageCreated = false;
        var committed = false;

        await using var fileSystem = _fileSystemFactory.Create(profile);
        await using var executor = _commandExecutorFactory.Create(profile);
        try
        {
            await fileSystem.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await using (var source = new MemoryStream(content, writable: false))
            {
                await fileSystem.UploadAsync(
                        source,
                        userStage,
                        content.Length,
                        overwrite: false,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            await fileSystem.SetPermissionsAsync(userStage, RemoteUnixPermissions.FromMode(600), cancellationToken)
                .ConfigureAwait(false);

            if (validation is not null)
            {
                var validationResult = await executor.ExecuteAsync(
                        new RemoteCommandSpec(
                            validation.Executable,
                            validation.Resolve(userStage),
                            TimeSpan.FromSeconds(30),
                            OperationRisk.ReadOnly),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (validationResult.Error is not null)
                {
                    return new RemoteEditorSaveResult(false, false, validationResult.Error.Message, validationResult.Error);
                }

                var command = validationResult.Command!;
                if (command.ExitCode != 0)
                {
                    var detail = FirstUseful(command.StandardError, command.StandardOutput, "Validator rejected the staged file.");
                    return new RemoteEditorSaveResult(false, true, detail);
                }
            }

            var install = await ExecuteCheckedAsync(
                    executor,
                    "sudo",
                    [
                        "-n",
                        "install",
                        "-m",
                        original.Metadata.Permissions.ToString(),
                        "-o",
                        userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "-g",
                        groupId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        "--",
                        userStage.Value,
                        privilegedStage.Value,
                    ],
                    OperationRisk.Mutating,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!install.IsSuccess)
            {
                return install;
            }

            privilegedStageCreated = true;
            var replace = await ExecuteCheckedAsync(
                    executor,
                    "sudo",
                    ["-n", "mv", "-f", "--", privilegedStage.Value, target.Value],
                    OperationRisk.Mutating,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!replace.IsSuccess)
            {
                return replace;
            }

            committed = true;
            return new RemoteEditorSaveResult(
                true,
                false,
                "Validation passed and the privileged file was replaced atomically without changing its mode/UID/GID policy.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (RemoteFileSystemException exception)
        {
            return new RemoteEditorSaveResult(false, false, exception.Error.Message, exception.Error);
        }
        finally
        {
            try
            {
                if (fileSystem.IsConnected)
                {
                    await fileSystem.DeleteFileAsync(userStage, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
                // Best-effort cleanup of user-owned staging content.
            }

            if (privilegedStageCreated && !committed)
            {
                try
                {
                    _ = await executor.ExecuteAsync(
                            new RemoteCommandSpec(
                                "sudo",
                                ["-n", "rm", "-f", "--", privilegedStage.Value],
                                TimeSpan.FromSeconds(10),
                                OperationRisk.Mutating),
                            CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch
                {
                    // Best-effort privileged staging cleanup; live target was never replaced.
                }
            }
        }
    }

    private static async Task<RemoteEditorSaveResult> ExecuteCheckedAsync(
        IRemoteCommandExecutor executor,
        string executable,
        IReadOnlyList<string> arguments,
        OperationRisk risk,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
                new RemoteCommandSpec(executable, arguments, TimeSpan.FromSeconds(20), risk),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new RemoteEditorSaveResult(false, false, result.Error.Message, result.Error);
        }

        var command = result.Command!;
        if (command.ExitCode == 0)
        {
            return new RemoteEditorSaveResult(true, false, "Remote command completed.");
        }

        var detail = FirstUseful(command.StandardError, command.StandardOutput, "Privileged command failed.");
        var lower = detail.ToLowerInvariant();
        var code = lower.Contains("password", StringComparison.Ordinal) ||
                   lower.Contains("sudoers", StringComparison.Ordinal) ||
                   lower.Contains("not allowed", StringComparison.Ordinal)
            ? RemoteErrorCode.SudoRequired
            : lower.Contains("permission denied", StringComparison.Ordinal)
                ? RemoteErrorCode.PermissionDenied
                : RemoteErrorCode.CommandFailed;
        var error = new RemoteError(code, detail);
        return new RemoteEditorSaveResult(false, false, detail, error);
    }

    private static RemoteEditorSaveResult Failure(RemoteErrorCode code, string message)
    {
        var error = new RemoteError(code, message);
        return new RemoteEditorSaveResult(false, false, message, error);
    }

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        return !string.IsNullOrWhiteSpace(second) ? second.Trim() : fallback;
    }
}
