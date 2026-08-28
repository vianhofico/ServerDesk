using System.Text.Json;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Git;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class GitOperationsTests
{
    private const string Root = "/srv/app";
    private const string RevisionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RevisionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    [Theory]
    [InlineData("ubuntu-24.04.json")]
    [InlineData("ubuntu-26.04.json")]
    [InlineData("debian-13.json")]
    public void CertifiedFixturesParseStableRepositoryState(string file)
    {
        using var fixture = JsonDocument.Parse(ReadFixture(file));
        var root = fixture.RootElement;
        var snapshot = GitOperationsParser.ParseStatus(
            root.GetProperty("root").GetString()!,
            root.GetProperty("root").GetString()!,
            root.GetProperty("status").GetString()!,
            string.Empty,
            string.Empty,
            root.GetProperty("remotes").GetString()!,
            1000,
            30);

        Assert.Equal(root.GetProperty("expectedBranch").GetString(), snapshot.Branch);
        Assert.Equal(root.GetProperty("expectedAhead").GetInt32(), snapshot.Ahead);
        Assert.Equal(root.GetProperty("expectedBehind").GetInt32(), snapshot.Behind);
        Assert.Equal(root.GetProperty("expectedChanges").GetInt32(), snapshot.Changes.Count);
    }

    [Fact]
    public void RemoteUrlsRemoveCredentialsQueryAndFragment()
    {
        var remotes = GitOperationsParser.ParseRemotes(
            "remote.origin.url https://token-value:secret@example.invalid/team/app.git?token=other#fragment\n",
            10);

        var remote = Assert.Single(remotes);
        Assert.Equal("origin", remote.Name);
        Assert.DoesNotContain("token-value", remote.SafeUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", remote.SafeUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("token=", remote.SafeUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fragment", remote.SafeUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("example.invalid", remote.SafeUrl, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("srv/app")]
    [InlineData("/srv/../etc")]
    [InlineData("/srv/app\n--help")]
    [InlineData(" /srv/app")]
    [InlineData("/srv/app/")]
    public async Task UnsafeRepositoryPathsAreRejectedBeforeRemoteExecution(string path)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Git", "example.invalid", 22, "dev");
        var factory = new RecordingFactory(_ => Success());
        var service = new GitOperationsService(factory, GitOperationsOptions.Default);

        await Assert.ThrowsAnyAsync<Exception>(() => service.InspectAsync(profile, path, cancellationToken));
        Assert.Equal(0, factory.CreateCount);
        Assert.Empty(factory.Commands);
    }

    [Fact]
    public async Task InspectUsesReadOnlyTokenizedPorcelainCommands()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Git", "example.invalid", 22, "dev");
        var state = new RepositoryState();
        var factory = FactoryFor(state);
        var service = new GitOperationsService(factory, GitOperationsOptions.Default);

        var result = await service.InspectAsync(profile, Root, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("main", result.Snapshot?.Branch);
        Assert.Equal(1, result.Snapshot?.Behind);
        Assert.All(factory.Commands, command => Assert.Equal(OperationRisk.ReadOnly, command.Risk));
        Assert.Contains(factory.Commands, command =>
            command.Executable == "git" &&
            command.Arguments.SequenceEqual(["-C", Root, "status", "--porcelain=v2", "--branch", "-z", "--untracked-files=all"]));
        Assert.All(factory.Commands, command => Assert.DoesNotContain(";", string.Join(' ', command.Arguments), StringComparison.Ordinal));
    }

    [Fact]
    public async Task DiscoveryUsesTokenizedFindWithExplicitDepth()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Git", "example.invalid", 22, "dev");
        var factory = new RecordingFactory(command =>
            command.Executable == "find"
                ? Success("/srv/app/.git\n/srv/other/.git\n")
                : Success());
        var service = new GitOperationsService(factory, GitOperationsOptions.Default);

        var result = await service.DiscoverAsync(profile, "/srv", 4, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(["/srv/app", "/srv/other"], result.RepositoryPaths);
        var command = Assert.Single(factory.Commands);
        Assert.Equal("find", command.Executable);
        Assert.Equal(["/srv", "-maxdepth", "4", "-name", ".git", "-print"], command.Arguments);
        Assert.Equal(OperationRisk.ReadOnly, command.Risk);
    }

    [Fact]
    public async Task FetchIsMutatingNeverUsesResetCleanForceOrSubmoduleRecursion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Git", "example.invalid", 22, "dev");
        var state = new RepositoryState();
        var factory = FactoryFor(state);
        var service = new GitOperationsService(factory, GitOperationsOptions.Default);

        var result = await service.FetchAsync(profile, Root, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var fetch = Assert.Single(factory.Commands, command => command.Arguments.Contains("fetch"));
        Assert.Equal(OperationRisk.Mutating, fetch.Risk);
        Assert.Equal(["-C", Root, "fetch", "--no-recurse-submodules"], fetch.Arguments);
        Assert.DoesNotContain("reset", fetch.Arguments);
        Assert.DoesNotContain("clean", fetch.Arguments);
        Assert.DoesNotContain("--force", fetch.Arguments);
        Assert.DoesNotContain("checkout", fetch.Arguments);
    }

    [Fact]
    public async Task FetchTransportLossReturnsAmbiguousAndDoesNotRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Git", "example.invalid", 22, "dev");
        var state = new RepositoryState();
        var factory = FactoryFor(state, command =>
            command.Arguments.Contains("fetch")
                ? RemoteExecutionResult.Failure(new RemoteError(RemoteErrorCode.NetworkInterrupted, "channel dropped"))
                : null);
        var service = new GitOperationsService(factory, GitOperationsOptions.Default);

        var result = await service.FetchAsync(profile, Root, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Single(factory.Commands, command => command.Arguments.Contains("fetch"));
    }

    [Fact]
    public async Task DirtyWorktreeBlocksSafePullBeforeMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Git", "example.invalid", 22, "dev");
        var state = new RepositoryState { Dirty = true };
        var factory = FactoryFor(state);
        var service = new GitOperationsService(factory, GitOperationsOptions.Default);

        var preview = await service.PreviewPullAsync(profile, Root, cancellationToken);
        var result = await service.PullAsync(profile, Root, RevisionA, cancellationToken);

        Assert.True(preview.IsSuccess);
        Assert.False(preview.Preview?.CanApply);
        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.DoesNotContain(factory.Commands, command => command.Arguments.Contains("merge"));
    }

    [Fact]
    public async Task SafePullUsesConfirmedRevisionFastForwardOnlyAndDisablesHooks()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Git", "example.invalid", 22, "dev");
        var state = new RepositoryState();
        var factory = FactoryFor(state);
        var service = new GitOperationsService(factory, GitOperationsOptions.Default);

        var preview = await service.PreviewPullAsync(profile, Root, cancellationToken);
        Assert.True(preview.IsSuccess, preview.Error?.Message);
        Assert.True(preview.Preview?.CanApply);
        Assert.Equal(RevisionA, preview.Preview?.CurrentRevision);
        Assert.Contains(preview.Preview!.IncomingCommits, commit => commit.Contains("incoming", StringComparison.OrdinalIgnoreCase));

        var result = await service.PullAsync(profile, Root, RevisionA, cancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(RevisionB, result.VerifiedSnapshot?.Revision);
        Assert.Equal(0, result.VerifiedSnapshot?.Behind);
        var merge = Assert.Single(factory.Commands, command => command.Arguments.Contains("merge"));
        Assert.Equal(OperationRisk.Mutating, merge.Risk);
        Assert.Equal(
            ["-c", "core.hooksPath=/dev/null", "-C", Root, "merge", "--ff-only", "@{upstream}"],
            merge.Arguments);
        Assert.DoesNotContain("reset", merge.Arguments);
        Assert.DoesNotContain("clean", merge.Arguments);
        Assert.DoesNotContain("checkout", merge.Arguments);
        Assert.DoesNotContain("--force", merge.Arguments);
    }

    [Fact]
    public async Task StaleConfirmedRevisionBlocksSafePullWithoutMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Git", "example.invalid", 22, "dev");
        var state = new RepositoryState();
        var factory = FactoryFor(state);
        var service = new GitOperationsService(factory, GitOperationsOptions.Default);

        var result = await service.PullAsync(profile, Root, RevisionB, cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.DoesNotContain(factory.Commands, command => command.Arguments.Contains("merge"));
    }

    [Fact]
    public async Task DivergedBranchIsNeverReconciledAutomatically()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Git", "example.invalid", 22, "dev");
        var state = new RepositoryState { Ahead = 2, Behind = 3 };
        var factory = FactoryFor(state);
        var service = new GitOperationsService(factory, GitOperationsOptions.Default);

        var preview = await service.PreviewPullAsync(profile, Root, cancellationToken);

        Assert.True(preview.IsSuccess);
        Assert.False(preview.Preview?.CanApply);
        Assert.Contains("diverged", preview.Preview!.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(factory.Commands, command => command.Arguments.Contains("merge"));
    }

    [Fact]
    public async Task AuditMarksAmbiguousGitMutationUnknownWithoutRemoteCredentials()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Git", "example.invalid", 22, "dev");
        var audit = new RecordingAudit();
        var inner = new StubGitService(new GitFetchResult(
            false,
            new RemoteError(RemoteErrorCode.AmbiguousState, "unknown"),
            "unknown"));
        var service = new AuditedGitOperationsService(inner, audit);

        await service.FetchAsync(profile, Root, cancellationToken);

        var entry = Assert.Single(audit.Entries);
        Assert.Equal("git-operation", entry.Category);
        Assert.Equal(OperationRisk.Mutating, entry.Risk);
        Assert.Equal(OperationOutcome.Unknown, entry.Outcome);
        Assert.Contains(Root, entry.Target!, StringComparison.Ordinal);
        Assert.DoesNotContain("token", entry.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", entry.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static RecordingFactory FactoryFor(
        RepositoryState state,
        Func<RemoteCommandSpec, RemoteExecutionResult?>? overrideHandler = null) =>
        new(command =>
        {
            var overridden = overrideHandler?.Invoke(command);
            if (overridden is not null)
            {
                return overridden;
            }

            if (command.Executable != "git")
            {
                return Success();
            }

            if (command.Arguments.SequenceEqual(["--version"]))
            {
                return Success("git version 2.45.2\n");
            }

            if (command.Arguments.Contains("rev-parse"))
            {
                return Success(Root + "\n");
            }

            if (command.Arguments.Contains("status"))
            {
                return Success(Status(state));
            }

            if (command.Arguments.Contains("diff"))
            {
                return Success(state.Dirty ? " 1 file changed, 1 insertion(+)\n" : string.Empty);
            }

            if (command.Arguments.Contains("config"))
            {
                return Success("remote.origin.url https://token-value@example.invalid/team/app.git\n");
            }

            if (command.Arguments.Contains("log"))
            {
                return Success("bbbbbbb\tincoming commit\n");
            }

            if (command.Arguments.Contains("fetch"))
            {
                return Success();
            }

            if (command.Arguments.Contains("merge"))
            {
                state.Merged = true;
                return Success("Updating aaaaaaa..bbbbbbb\nFast-forward\n");
            }

            return Success();
        });

    private static string Status(RepositoryState state)
    {
        var revision = state.Merged ? RevisionB : RevisionA;
        var ahead = state.Merged ? 0 : state.Ahead;
        var behind = state.Merged ? 0 : state.Behind;
        var output = $"# branch.oid {revision}\0# branch.head main\0# branch.upstream origin/main\0# branch.ab +{ahead} -{behind}\0";
        if (state.Dirty)
        {
            output += $"1 .M N... 100644 100644 100644 {revision} {revision} src/app.cs\0";
        }

        return output;
    }

    private static string ReadFixture(string file) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Git", file));

    private static RemoteExecutionResult Success(string output = "") =>
        RemoteExecutionResult.Success(new RemoteCommandResult(0, output, string.Empty, TimeSpan.Zero));

    private sealed class RepositoryState
    {
        public bool Dirty { get; init; }

        public int Ahead { get; init; }

        public int Behind { get; init; } = 1;

        public bool Merged { get; set; }
    }

    private sealed class RecordingFactory : IRemoteCommandExecutorFactory
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public RecordingFactory(Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            _handler = handler;
        }

        public List<RemoteCommandSpec> Commands { get; } = [];

        public int CreateCount { get; private set; }

        public IRemoteCommandExecutor Create(ServerProfile profile)
        {
            CreateCount++;
            return new RecordingExecutor(profile.Id, Commands, _handler);
        }
    }

    private sealed class RecordingExecutor : IRemoteCommandExecutor
    {
        private readonly List<RemoteCommandSpec> _commands;
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public RecordingExecutor(
            Guid serverProfileId,
            List<RemoteCommandSpec> commands,
            Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            ServerProfileId = serverProfileId;
            _commands = commands;
            _handler = handler;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _commands.Add(command);
            return Task.FromResult(_handler(command));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingAudit : IOperationAudit
    {
        public List<OperationAuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OperationAuditEntry>>(Entries.Take(limit).ToArray());
    }

    private sealed class StubGitService : IGitOperationsService
    {
        private readonly GitFetchResult _fetchResult;

        public StubGitService(GitFetchResult fetchResult)
        {
            _fetchResult = fetchResult;
        }

        public Task<GitDiscoveryResult> DiscoverAsync(ServerProfile profile, string rootPath, int maximumDepth, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitDiscoveryResult([], null));

        public Task<GitRepositoryResult> InspectAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitRepositoryResult(null, new RemoteError(RemoteErrorCode.CommandFailed, "unused")));

        public Task<GitFetchResult> FetchAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(_fetchResult);

        public Task<GitPullPreviewResult> PreviewPullAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitPullPreviewResult(null, new RemoteError(RemoteErrorCode.CommandFailed, "unused")));

        public Task<GitPullResult> PullAsync(ServerProfile profile, string repositoryPath, string expectedRevision, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitPullResult(false, new RemoteError(RemoteErrorCode.CommandFailed, "unused"), "unused"));
    }
}
