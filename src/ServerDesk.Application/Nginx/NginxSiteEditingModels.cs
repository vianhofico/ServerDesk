using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Nginx;

public sealed record NginxSiteEditDocument(
    RemotePath RequestedPath,
    RemotePath CanonicalPath,
    RemoteEditorDocument Document);

public sealed record NginxSiteEditLoadResult(
    NginxSiteEditDocument? Document,
    RemoteError? Error)
{
    public bool IsSuccess => Document is not null && Error is null;
}

public sealed record NginxSiteApplyResult(
    bool IsSuccess,
    bool ValidationFailed,
    bool RolledBack,
    bool AmbiguousState,
    string Message,
    RemoteError? Error = null,
    RemotePath? RecoveryBackupPath = null);

public sealed record NginxSimpleSitePatch(
    IReadOnlyList<string> ServerNames,
    string Listen,
    string ProxyPass);

public interface INginxSiteEditingService
{
    ValueTask<NginxSiteEditLoadResult> LoadAsync(
        ServerProfile profile,
        RemotePath requestedPath,
        CancellationToken cancellationToken = default);

    Task<NginxSiteApplyResult> ApplyAsync(
        ServerProfile profile,
        NginxSiteEditDocument original,
        string candidateText,
        CancellationToken cancellationToken = default);
}

public sealed record NginxSiteEditingOptions(
    TimeSpan CommandTimeout,
    int MaximumCandidateBytes,
    string PrivilegeExecutable,
    string NginxExecutable,
    string NamespaceExecutable,
    string ShellExecutable,
    string ReadlinkExecutable)
{
    public static NginxSiteEditingOptions Default { get; } = new(
        TimeSpan.FromSeconds(30),
        1024 * 1024,
        "sudo",
        "nginx",
        "unshare",
        "/bin/sh",
        "readlink");

    public void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }

        if (MaximumCandidateBytes is <= 0 or > 4 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCandidateBytes));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(PrivilegeExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(NginxExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(NamespaceExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(ShellExecutable);
        ArgumentException.ThrowIfNullOrWhiteSpace(ReadlinkExecutable);
    }
}
