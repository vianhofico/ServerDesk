using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Git;

public sealed class GitOperationsService : IGitOperationsService
{
    private static readonly IReadOnlyDictionary<string, string> ReadEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C",
            ["GIT_TERMINAL_PROMPT"] = "0",
            ["GIT_OPTIONAL_LOCKS"] = "0",
        };

    private static readonly IReadOnlyDictionary<string, string> MutationEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C",
            ["GIT_TERMINAL_PROMPT"] = "0",
        };

    private readonly IRemoteCommandExecutorFactory _executorFactory;
    private readonly GitOperationsOptions _options;

    public GitOperationsService(IRemoteCommandExecutorFactory executorFactory, GitOperationsOptions options)
    {
        _executorFactory = executorFactory ?? throw new ArgumentNullException(nameof(executorFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<GitDiscoveryResult> DiscoverAsync(
        ServerProfile profile,
        string rootPath,
        int maximumDepth,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var root = GitRepositoryPath.Normalize(rootPath);
        if (maximumDepth < 1 || maximumDepth > _options.MaximumDiscoveryDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumDepth));
        }

        await using var executor = _executorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    "find",
                    [root, "-maxdepth", maximumDepth.ToString(System.Globalization.CultureInfo.InvariantCulture), "-name", ".git", "-print"],
                    _options.DiscoveryTimeout,
                    OperationRisk.ReadOnly,
                    new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" }),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            return new GitDiscoveryResult([], execution.Error);
        }

        var command = execution.Command!;
        var repositories = GitOperationsParser.ParseDiscovery(command.StandardOutput, _options.MaximumDiscoveryResults);
        if (command.ExitCode == 0)
        {
            return new GitDiscoveryResult(repositories, null);
        }

        var detail = FirstUseful(command.StandardError, command.StandardOutput, "Repository discovery failed.");
        if (repositories.Count > 0)
        {
            return new GitDiscoveryResult(
                repositories,
                null,
                $"Repository discovery returned partial results: {GitOperationsParser.Sanitize(detail)}");
        }

        return new GitDiscoveryResult([], GitOperationsParser.MapFailure(detail));
    }

    public async Task<GitRepositoryResult> InspectAsync(
        ServerProfile profile,
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var requestedPath = GitRepositoryPath.Normalize(repositoryPath);
        await using var executor = _executorFactory.Create(profile);

        var version = await ExecuteGitAsync(
                executor,
                ["--version"],
                OperationRisk.ReadOnly,
                _options.ReadTimeout,
                ReadEnvironment,
                cancellationToken)
            .ConfigureAwait(false);
        var versionFailure = GetFailure(version, "Git is unavailable on the remote server.");
        if (versionFailure is not null)
        {
            return new GitRepositoryResult(null, versionFailure);
        }

        var rootExecution = await ExecuteGitAsync(
                executor,
                ["-C", requestedPath, "rev-parse", "--show-toplevel"],
                OperationRisk.ReadOnly,
                _options.ReadTimeout,
                ReadEnvironment,
                cancellationToken)
            .ConfigureAwait(false);
        var rootFailure = GetFailure(rootExecution, "The selected path is not a Git repository.");
        if (rootFailure is not null)
        {
            return new GitRepositoryResult(null, rootFailure);
        }

        string repositoryRoot;
        try
        {
            repositoryRoot = GitRepositoryPath.Normalize(rootExecution.Command!.StandardOutput.Trim());
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return new GitRepositoryResult(
                null,
                new RemoteError(RemoteErrorCode.ParseFailed, "Git returned an invalid repository root path.", exception.Message));
        }

        var statusExecution = await ExecuteGitAsync(
                executor,
                ["-C", repositoryRoot, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"],
                OperationRisk.ReadOnly,
                _options.ReadTimeout,
                ReadEnvironment,
                cancellationToken)
            .ConfigureAwait(false);
        var statusFailure = GetFailure(statusExecution, "Git status failed.");
        if (statusFailure is not null)
        {
            return new GitRepositoryResult(null, statusFailure);
        }

        var unstagedExecution = await ExecuteGitAsync(
                executor,
                ["-C", repositoryRoot, "diff", "--shortstat", "--no-color"],
                OperationRisk.ReadOnly,
                _options.ReadTimeout,
                ReadEnvironment,
                cancellationToken)
            .ConfigureAwait(false);
        var unstagedFailure = GetFailure(unstagedExecution, "Git could not summarize unstaged changes.");
        if (unstagedFailure is not null)
        {
            return new GitRepositoryResult(null, unstagedFailure);
        }

        var stagedExecution = await ExecuteGitAsync(
                executor,
                ["-C", repositoryRoot, "diff", "--cached", "--shortstat", "--no-color"],
                OperationRisk.ReadOnly,
                _options.ReadTimeout,
                ReadEnvironment,
                cancellationToken)
            .ConfigureAwait(false);
        var stagedFailure = GetFailure(stagedExecution, "Git could not summarize staged changes.");
        if (stagedFailure is not null)
        {
            return new GitRepositoryResult(null, stagedFailure);
        }

        var remoteExecution = await ExecuteGitAsync(
                executor,
                ["-C", repositoryRoot, "config", "--get-regexp", "^remote\\..*\\.url$"],
                OperationRisk.ReadOnly,
                _options.ReadTimeout,
                ReadEnvironment,
                cancellationToken)
            .ConfigureAwait(false);
        if (remoteExecution.Error is not null)
        {
            return new GitRepositoryResult(null, remoteExecution.Error);
        }

        var remoteCommand = remoteExecution.Command!;
        if (remoteCommand.ExitCode is not 0 and not 1)
        {
            return new GitRepositoryResult(
                null,
                GitOperationsParser.MapFailure(FirstUseful(
                    remoteCommand.StandardError,
                    remoteCommand.StandardOutput,
                    "Git could not inspect repository remotes.")));
        }

        try
        {
            var snapshot = GitOperationsParser.ParseStatus(
                requestedPath,
                repositoryRoot,
                statusExecution.Command!.StandardOutput,
                unstagedExecution.Command!.StandardOutput,
                stagedExecution.Command!.StandardOutput,
                remoteCommand.ExitCode == 0 ? remoteCommand.StandardOutput : string.Empty,
                _options.MaximumChanges,
                _options.MaximumRemotes);
            return new GitRepositoryResult(snapshot, null);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return new GitRepositoryResult(
                null,
                new RemoteError(RemoteErrorCode.ParseFailed, "Git returned malformed machine-readable repository state.", exception.Message));
        }
    }

    public async Task<GitFetchResult> FetchAsync(
        ServerProfile profile,
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var before = await InspectAsync(profile, repositoryPath, cancellationToken).ConfigureAwait(false);
        if (!before.IsSuccess || before.Snapshot is null)
        {
            var error = before.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, "Repository inspection failed before fetch.");
            return new GitFetchResult(false, error, error.Message);
        }

        await using var executor = _executorFactory.Create(profile);
        var execution = await ExecuteGitAsync(
                executor,
                ["-C", before.Snapshot.RepositoryRoot, "fetch", "--no-recurse-submodules"],
                OperationRisk.Mutating,
                _options.MutationTimeout,
                MutationEnvironment,
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            var error = MapMutationExecutionError(execution.Error, "fetch", before.Snapshot.RepositoryRoot);
            return new GitFetchResult(false, error, error.Message);
        }

        if (execution.Command!.ExitCode != 0)
        {
            var error = GitOperationsParser.MapFailure(FirstUseful(
                execution.Command.StandardError,
                execution.Command.StandardOutput,
                "Git fetch failed."));
            return new GitFetchResult(false, error, error.Message);
        }

        var verified = await InspectAsync(profile, before.Snapshot.RepositoryRoot, cancellationToken).ConfigureAwait(false);
        if (!verified.IsSuccess || verified.Snapshot is null)
        {
            var error = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                "Git fetch returned success, but ServerDesk could not verify repository state afterward. Refresh before retrying.",
                verified.Error?.Message);
            return new GitFetchResult(false, error, error.Message);
        }

        return new GitFetchResult(
            true,
            null,
            $"Git fetch completed and repository state was verified for '{verified.Snapshot.RepositoryRoot}'.",
            verified.Snapshot);
    }

    public async Task<GitPullPreviewResult> PreviewPullAsync(
        ServerProfile profile,
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var inspected = await InspectAsync(profile, repositoryPath, cancellationToken).ConfigureAwait(false);
        if (!inspected.IsSuccess || inspected.Snapshot is null)
        {
            return new GitPullPreviewResult(null, inspected.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, "Repository inspection failed."));
        }

        var snapshot = inspected.Snapshot;
        var reason = GetPullBlockReason(snapshot);
        if (reason is not null)
        {
            return new GitPullPreviewResult(
                new GitPullPreview(
                    false,
                    reason,
                    snapshot.RepositoryRoot,
                    snapshot.Revision,
                    snapshot.Branch,
                    snapshot.Upstream ?? string.Empty,
                    snapshot.Ahead,
                    snapshot.Behind,
                    []),
                null);
        }

        await using var executor = _executorFactory.Create(profile);
        var execution = await ExecuteGitAsync(
                executor,
                [
                    "-C", snapshot.RepositoryRoot,
                    "log",
                    "--format=%h%x09%s",
                    "--max-count", _options.MaximumPreviewCommits.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "HEAD..@{upstream}",
                ],
                OperationRisk.ReadOnly,
                _options.ReadTimeout,
                ReadEnvironment,
                cancellationToken)
            .ConfigureAwait(false);
        var failure = GetFailure(execution, "Git could not preview incoming commits.");
        if (failure is not null)
        {
            return new GitPullPreviewResult(null, failure);
        }

        var commits = GitOperationsParser.ParseIncomingCommits(
            execution.Command!.StandardOutput,
            _options.MaximumPreviewCommits);
        return new GitPullPreviewResult(
            new GitPullPreview(
                true,
                $"Safe pull can fast-forward '{snapshot.Branch}' by {snapshot.Behind} commit(s) from '{snapshot.Upstream}'.",
                snapshot.RepositoryRoot,
                snapshot.Revision,
                snapshot.Branch,
                snapshot.Upstream!,
                snapshot.Ahead,
                snapshot.Behind,
                commits),
            null);
    }

    public async Task<GitPullResult> PullAsync(
        ServerProfile profile,
        string repositoryPath,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedRevision);
        if (!string.Equals(expectedRevision, expectedRevision.Trim(), StringComparison.Ordinal) ||
            expectedRevision.IndexOfAny(['\0', '\r', '\n']) >= 0)
        {
            throw new FormatException("Expected Git revision must be a single normalized revision value.");
        }

        var previewResult = await PreviewPullAsync(profile, repositoryPath, cancellationToken).ConfigureAwait(false);
        if (!previewResult.IsSuccess || previewResult.Preview is null)
        {
            var error = previewResult.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, "Safe pull preview failed.");
            return new GitPullResult(false, error, error.Message);
        }

        var preview = previewResult.Preview;
        if (!preview.CanApply)
        {
            var error = new RemoteError(RemoteErrorCode.PathConflict, preview.Message);
            return new GitPullResult(false, error, error.Message);
        }

        if (!string.Equals(preview.CurrentRevision, expectedRevision, StringComparison.Ordinal))
        {
            var error = new RemoteError(
                RemoteErrorCode.PathConflict,
                "Repository revision changed after the confirmed preview. Refresh and preview again before applying a safe pull.");
            return new GitPullResult(false, error, error.Message);
        }

        await using var executor = _executorFactory.Create(profile);
        var execution = await ExecuteGitAsync(
                executor,
                [
                    "-c", "core.hooksPath=/dev/null",
                    "-C", preview.RepositoryRoot,
                    "merge", "--ff-only", "@{upstream}",
                ],
                OperationRisk.Mutating,
                _options.MutationTimeout,
                MutationEnvironment,
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            var error = MapMutationExecutionError(execution.Error, "safe pull", preview.RepositoryRoot);
            return new GitPullResult(false, error, error.Message);
        }

        if (execution.Command!.ExitCode != 0)
        {
            var error = GitOperationsParser.MapFailure(FirstUseful(
                execution.Command.StandardError,
                execution.Command.StandardOutput,
                "Git safe pull failed."));
            return new GitPullResult(false, error, error.Message);
        }

        var verified = await InspectAsync(profile, preview.RepositoryRoot, cancellationToken).ConfigureAwait(false);
        if (!verified.IsSuccess || verified.Snapshot is null ||
            !verified.Snapshot.IsClean ||
            verified.Snapshot.IsDetached ||
            !string.Equals(verified.Snapshot.Branch, preview.Branch, StringComparison.Ordinal) ||
            !string.Equals(verified.Snapshot.Upstream, preview.Upstream, StringComparison.Ordinal) ||
            string.Equals(verified.Snapshot.Revision, expectedRevision, StringComparison.Ordinal) ||
            verified.Snapshot.Behind != 0)
        {
            var error = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                "Git reported a successful fast-forward, but ServerDesk could not verify the expected clean updated branch state. Refresh before any retry.",
                verified.Error?.Message);
            return new GitPullResult(false, error, error.Message, verified.Snapshot);
        }

        return new GitPullResult(
            true,
            null,
            $"Safe pull fast-forwarded '{preview.Branch}' to {verified.Snapshot.Revision} and verification succeeded.",
            verified.Snapshot);
    }

    private static string? GetPullBlockReason(GitRepositorySnapshot snapshot)
    {
        if (snapshot.IsDetached)
        {
            return "Safe pull is unavailable for a detached HEAD. Use the terminal for advanced Git operations.";
        }

        if (!snapshot.IsClean)
        {
            return "Safe pull requires a clean worktree and index. Commit, stash or otherwise handle local changes explicitly first.";
        }

        if (string.IsNullOrWhiteSpace(snapshot.Upstream))
        {
            return "Safe pull requires the current branch to have an upstream branch.";
        }

        if (snapshot.Ahead > 0 && snapshot.Behind > 0)
        {
            return "The local and upstream branches have diverged. ServerDesk will not choose a merge/rebase strategy automatically.";
        }

        if (snapshot.Ahead > 0)
        {
            return "The local branch contains commits not present upstream. ServerDesk will not rewrite or reconcile them automatically.";
        }

        if (snapshot.Behind == 0)
        {
            return "The fetched upstream state is already up to date. Run Fetch first if you want to check the remote network state.";
        }

        return null;
    }

    private static Task<RemoteExecutionResult> ExecuteGitAsync(
        IRemoteCommandExecutor executor,
        IReadOnlyList<string> arguments,
        OperationRisk risk,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string> environment,
        CancellationToken cancellationToken) =>
        executor.ExecuteAsync(
            new RemoteCommandSpec("git", arguments, timeout, risk, environment),
            cancellationToken);

    private static RemoteError? GetFailure(RemoteExecutionResult execution, string fallback)
    {
        if (execution.Error is not null)
        {
            return execution.Error;
        }

        var command = execution.Command!;
        return command.ExitCode == 0
            ? null
            : GitOperationsParser.MapFailure(FirstUseful(command.StandardError, command.StandardOutput, fallback));
    }

    private static RemoteError MapMutationExecutionError(RemoteError error, string operation, string repositoryRoot)
    {
        if (error.Code is RemoteErrorCode.NetworkInterrupted or
            RemoteErrorCode.CommandTimeout or
            RemoteErrorCode.OperationCancelled)
        {
            return new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"ServerDesk lost a reliable completion signal during Git {operation} for '{repositoryRoot}'. Refresh repository state before deciding whether to retry.",
                error.TechnicalDetails);
        }

        return error;
    }

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return GitOperationsParser.Sanitize(first.Trim());
        }

        return !string.IsNullOrWhiteSpace(second)
            ? GitOperationsParser.Sanitize(second.Trim())
            : fallback;
    }
}

public sealed class AuditedGitOperationsService : IGitOperationsService
{
    private readonly IGitOperationsService _inner;
    private readonly IOperationAudit _audit;

    public AuditedGitOperationsService(IGitOperationsService inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public Task<GitDiscoveryResult> DiscoverAsync(ServerProfile profile, string rootPath, int maximumDepth, CancellationToken cancellationToken = default) =>
        _inner.DiscoverAsync(profile, rootPath, maximumDepth, cancellationToken);

    public Task<GitRepositoryResult> InspectAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default) =>
        _inner.InspectAsync(profile, repositoryPath, cancellationToken);

    public Task<GitPullPreviewResult> PreviewPullAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default) =>
        _inner.PreviewPullAsync(profile, repositoryPath, cancellationToken);

    public async Task<GitFetchResult> FetchAsync(
        ServerProfile profile,
        string repositoryPath,
        CancellationToken cancellationToken = default)
    {
        var path = GitRepositoryPath.Normalize(repositoryPath);
        var result = await _inner.FetchAsync(profile, path, cancellationToken).ConfigureAwait(false);
        await TryAuditAsync(
                profile,
                path,
                "fetch",
                result.IsSuccess ? OperationOutcome.Succeeded : OutcomeFor(result.Error),
                cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public async Task<GitPullResult> PullAsync(
        ServerProfile profile,
        string repositoryPath,
        string expectedRevision,
        CancellationToken cancellationToken = default)
    {
        var path = GitRepositoryPath.Normalize(repositoryPath);
        var result = await _inner.PullAsync(profile, path, expectedRevision, cancellationToken).ConfigureAwait(false);
        await TryAuditAsync(
                profile,
                path,
                "safe-pull",
                result.IsSuccess ? OperationOutcome.Succeeded : OutcomeFor(result.Error),
                cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    private static OperationOutcome OutcomeFor(RemoteError? error) =>
        error?.Code == RemoteErrorCode.AmbiguousState
            ? OperationOutcome.Unknown
            : error?.Code == RemoteErrorCode.OperationCancelled
                ? OperationOutcome.Cancelled
                : OperationOutcome.Failed;

    private async ValueTask TryAuditAsync(
        ServerProfile profile,
        string repositoryPath,
        string operation,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            await _audit.AppendAsync(
                    OperationAuditEntry.Create(
                        "git-operation",
                        $"Git {operation} requested for repository {repositoryPath}",
                        OperationRisk.Mutating,
                        outcome,
                        $"{profile.Username}@{profile.Host}:{profile.Port} git:{repositoryPath}"),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // Audit persistence failure must never trigger a Git mutation retry.
        }
    }
}
