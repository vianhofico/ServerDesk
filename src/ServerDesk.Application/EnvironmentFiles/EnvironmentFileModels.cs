using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.EnvironmentFiles;

public enum EnvironmentFileLineKind
{
    Blank,
    Comment,
    Assignment,
    Unsupported,
}

public sealed record EnvironmentFileLine(
    int LineNumber,
    EnvironmentFileLineKind Kind,
    string RawText,
    string? Key = null,
    string? Value = null,
    bool IsSecret = false);

public sealed record EnvironmentFileEntry(
    int LineNumber,
    string Key,
    string Value,
    bool IsSecret);

public sealed record EnvironmentFileSnapshot(
    RemotePath Path,
    RemoteEditorDocument Original,
    IReadOnlyList<EnvironmentFileLine> Lines,
    IReadOnlyList<EnvironmentFileEntry> Entries,
    bool HasUnsupportedLines,
    string NewLine)
{
    public string Text => Original.Text;
}

public sealed record EnvironmentFileLoadResult(
    EnvironmentFileSnapshot? Snapshot,
    RemoteError? Error)
{
    public bool IsSuccess => Snapshot is not null && Error is null;
}

public sealed record EnvironmentFileApplyResult(
    bool IsSuccess,
    bool ValidationFailed,
    bool AmbiguousState,
    string Message,
    RemoteError? Error = null,
    EnvironmentFileSnapshot? Snapshot = null);

public sealed record EnvironmentFileValidationSpec(
    string Executable,
    IReadOnlyList<string> Arguments);

public interface IEnvironmentFileService
{
    ValueTask<EnvironmentFileLoadResult> LoadAsync(
        ServerProfile profile,
        RemotePath path,
        CancellationToken cancellationToken = default);

    ValueTask<EnvironmentFileApplyResult> ApplyAsync(
        ServerProfile profile,
        EnvironmentFileSnapshot original,
        string candidateText,
        EnvironmentFileValidationSpec? validation = null,
        CancellationToken cancellationToken = default);
}

public sealed record EnvironmentFileOptions(int MaximumCandidateBytes)
{
    public static EnvironmentFileOptions Default { get; } = new(1024 * 1024);

    public void Validate()
    {
        if (MaximumCandidateBytes is <= 0 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCandidateBytes));
        }
    }
}
