using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Git;

public sealed record GitChange(
    char IndexStatus,
    char WorktreeStatus,
    string Path,
    string? OriginalPath = null)
{
    public bool IsStaged => IndexStatus != '.';

    public bool IsUnstaged => WorktreeStatus != '.';

    public bool IsUntracked => IndexStatus == '?' && WorktreeStatus == '?';

    public string StatusDisplay => IsUntracked ? "??" : $"{IndexStatus}{WorktreeStatus}";
}

public sealed record GitRemoteInfo(string Name, string SafeUrl);

public sealed record GitRepositorySnapshot(
    string RequestedPath,
    string RepositoryRoot,
    string Branch,
    string Revision,
    string? Upstream,
    bool IsDetached,
    int Ahead,
    int Behind,
    IReadOnlyList<GitChange> Changes,
    IReadOnlyList<GitRemoteInfo> Remotes,
    string UnstagedDiffSummary,
    string StagedDiffSummary)
{
    public bool IsClean => Changes.Count == 0;

    public int StagedCount => Changes.Count(change => change.IsStaged && !change.IsUntracked);

    public int UnstagedCount => Changes.Count(change => change.IsUnstaged && !change.IsUntracked);

    public int UntrackedCount => Changes.Count(change => change.IsUntracked);
}

public sealed record GitRepositoryResult(
    GitRepositorySnapshot? Snapshot,
    RemoteError? Error)
{
    public bool IsSuccess => Snapshot is not null && Error is null;
}

public sealed record GitDiscoveryResult(
    IReadOnlyList<string> RepositoryPaths,
    RemoteError? Error,
    string? Warning = null)
{
    public bool IsSuccess => Error is null;
}

public sealed record GitFetchResult(
    bool IsSuccess,
    RemoteError? Error,
    string Message,
    GitRepositorySnapshot? VerifiedSnapshot = null);

public sealed record GitPullPreview(
    bool CanApply,
    string Message,
    string RepositoryRoot,
    string CurrentRevision,
    string Branch,
    string Upstream,
    int Ahead,
    int Behind,
    IReadOnlyList<string> IncomingCommits);

public sealed record GitPullPreviewResult(
    GitPullPreview? Preview,
    RemoteError? Error)
{
    public bool IsSuccess => Preview is not null && Error is null;
}

public sealed record GitPullResult(
    bool IsSuccess,
    RemoteError? Error,
    string Message,
    GitRepositorySnapshot? VerifiedSnapshot = null);

public sealed record GitOperationsOptions(
    TimeSpan ReadTimeout,
    TimeSpan DiscoveryTimeout,
    TimeSpan MutationTimeout,
    int MaximumDiscoveryDepth,
    int MaximumDiscoveryResults,
    int MaximumChanges,
    int MaximumRemotes,
    int MaximumPreviewCommits)
{
    public static GitOperationsOptions Default { get; } = new(
        TimeSpan.FromSeconds(25),
        TimeSpan.FromSeconds(35),
        TimeSpan.FromMinutes(2),
        6,
        100,
        1000,
        30,
        30);

    public void Validate()
    {
        if (ReadTimeout <= TimeSpan.Zero || ReadTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ReadTimeout));
        }

        if (DiscoveryTimeout <= TimeSpan.Zero || DiscoveryTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(DiscoveryTimeout));
        }

        if (MutationTimeout <= TimeSpan.Zero || MutationTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(MutationTimeout));
        }

        if (MaximumDiscoveryDepth is < 1 or > 12 ||
            MaximumDiscoveryResults is < 1 or > 1000 ||
            MaximumChanges is < 1 or > 10000 ||
            MaximumRemotes is < 1 or > 100 ||
            MaximumPreviewCommits is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumDiscoveryDepth));
        }
    }
}

public interface IGitOperationsService
{
    Task<GitDiscoveryResult> DiscoverAsync(
        ServerProfile profile,
        string rootPath,
        int maximumDepth,
        CancellationToken cancellationToken = default);

    Task<GitRepositoryResult> InspectAsync(
        ServerProfile profile,
        string repositoryPath,
        CancellationToken cancellationToken = default);

    Task<GitFetchResult> FetchAsync(
        ServerProfile profile,
        string repositoryPath,
        CancellationToken cancellationToken = default);

    Task<GitPullPreviewResult> PreviewPullAsync(
        ServerProfile profile,
        string repositoryPath,
        CancellationToken cancellationToken = default);

    Task<GitPullResult> PullAsync(
        ServerProfile profile,
        string repositoryPath,
        string expectedRevision,
        CancellationToken cancellationToken = default);
}

public static class GitRepositoryPath
{
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > 4096 ||
            value.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new FormatException("Git repository paths must not contain surrounding whitespace or control characters.");
        }

        var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment is "." or ".."))
        {
            throw new FormatException("Git repository paths must not contain '.' or '..' traversal segments.");
        }

        var parsed = RemotePath.Parse(value);
        if (!parsed.IsAbsolute || !string.Equals(parsed.Value, value, StringComparison.Ordinal))
        {
            throw new FormatException("Git repository paths must be normalized absolute Linux paths.");
        }

        return parsed.Value;
    }
}
