using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;

namespace ServerDesk.Application.Nginx;

public sealed record NginxSiteEditDocument(
    RemotePath RequestedPath,
    RemotePath CanonicalPath,
    RemoteEditorDocument Document);

public sealed record NginxSiteApplyResult(
    bool IsSuccess,
    bool ValidationFailed,
    bool RolledBack,
    bool AmbiguousState,
    string Message,
    RemoteError? Error = null);

public sealed record NginxSiteEditingOptions(
    TimeSpan CommandTimeout,
    int MaximumCandidateBytes)
{
    public static NginxSiteEditingOptions Default { get; } = new(
        TimeSpan.FromSeconds(30),
        1024 * 1024);
}
