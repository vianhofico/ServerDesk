using ServerDesk.Application.Audit;
using ServerDesk.Application.Deployment;
using ServerDesk.Application.Docker;
using ServerDesk.Application.Git;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.Services;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DeploymentOrchestrationTests
{
    private const string RepositoryPath = "/srv/app";
    private const string RevisionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string RevisionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DockerComposeProject Project = new("serverdesk-demo", "", ["/srv/demo/compose.yaml"]);

    [Fact]
    public void TargetPolicyRejectsImplicitOrSecretBearingHealthIdentity()
    {
        var noHealth = ComposeTarget([]);
        Assert.Throws<ArgumentException>(() => DeploymentTargetPolicy.Normalize(noHealth, DeploymentOptions.Default));

        var secretUrl = ComposeTarget([
            new DeploymentHealthCheck("http", DeploymentHealthCheckKind.Http, "https://user:secret@example.invalid/health"),
        ]);
        Assert.Throws<FormatException>(() => DeploymentTargetPolicy.Normalize(secretUrl, DeploymentOptions.Default));

        var queryUrl = ComposeTarget([
            new DeploymentHealthCheck("http", DeploymentHealthCheckKind.Http, "https://example.invalid/health?token=secret"),
        ]);
        Assert.Throws<FormatException>(() => DeploymentTargetPolicy.Normalize(queryUrl, DeploymentOptions.Default));

        var hostileId = ComposeTarget([ProcessHealth()]) with { Id = "prod;rm" };
        Assert.Throws<FormatException>(() => DeploymentTargetPolicy.Normalize(hostileId, DeploymentOptions.Default));
    }

    [Fact]
    public async Task GitComposePreviewExactlyMatchesSupportedOperationOrder()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var git = new FakeGit { Behind = 2 };
        var compose = new FakeCompose { Present = true };
        var service = CreateService(git, compose, new FakeServiceManager(), new FakeHealth(), new MemoryAudit());
        var target = new DeploymentTarget(
            "api-prod",
            "API production",
            "prod",
            DeploymentTargetKind.GitCompose,
            RepositoryPath,
            Project,
            DeploymentComposeMode.Restart,
            ComposePull: true,
            ComposeBuild: true,
            SystemdUnit: null,
            HealthChecks: [ProcessHealth()]);

        var preview = await service.PreviewAsync(Profile(), target, cancellationToken);

        Assert.True(preview.IsSuccess, preview.Error?.Message);
        Assert.Equal(
            [
                DeploymentStepKind.GitFetch,
                DeploymentStepKind.GitFastForward,
                DeploymentStepKind.ComposePull,
                DeploymentStepKind.ComposeBuild,
                DeploymentStepKind.ComposeRestart,
                DeploymentStepKind.HealthCheck,
            ],
            preview.Plan!.Steps.Select(step => step.Kind).ToArray());
        Assert.Equal(Enumerable.Range(1, 6), preview.Plan.Steps.Select(step => step.Sequence));
        Assert.False(preview.Plan.DeterministicRollbackPossible);
    }

    [Fact]
    public async Task StalePreviewBlocksBeforeAnyMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var git = new FakeGit { Revision = RevisionA };
        var services = new FakeServiceManager();
        var service = CreateService(git, new FakeCompose(), services, new FakeHealth(), new MemoryAudit());
        var target = GitSystemdTarget();
        var preview = await service.PreviewAsync(Profile(), target, cancellationToken);
        Assert.True(preview.IsSuccess, preview.Error?.Message);

        git.Revision = RevisionB;
        var result = await service.ExecuteAsync(Profile(), preview.Plan!, cancellationToken);

        Assert.Equal(DeploymentRunStatus.Failed, result.Status);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.Equal(0, git.FetchCount);
        Assert.Equal(0, services.ExecuteCount);
    }

    [Fact]
    public async Task AmbiguousGitFetchStopsOrchestrationWithoutBlindRetry()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var git = new FakeGit
        {
            FetchError = new RemoteError(RemoteErrorCode.AmbiguousState, "fetch completion unknown"),
        };
        var services = new FakeServiceManager();
        var health = new FakeHealth();
        var service = CreateService(git, new FakeCompose(), services, health, new MemoryAudit());
        var preview = await service.PreviewAsync(Profile(), GitSystemdTarget(), cancellationToken);
        Assert.True(preview.IsSuccess, preview.Error?.Message);

        var result = await service.ExecuteAsync(Profile(), preview.Plan!, cancellationToken);

        Assert.Equal(DeploymentRunStatus.Ambiguous, result.Status);
        Assert.Single(result.Steps);
        Assert.Equal(DeploymentStepOutcome.Unknown, result.Steps[0].Outcome);
        Assert.Equal(1, git.FetchCount);
        Assert.Equal(0, git.PullCount);
        Assert.Equal(0, services.ExecuteCount);
        Assert.Equal(0, health.RunCount);
    }

    [Fact]
    public async Task ComposeOnlyHealthFailureOffersRollbackOnlyForDeterministicPrestate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var compose = new FakeCompose { Present = false };
        var health = new FakeHealth
        {
            Result = new DeploymentHealthCheckResult(
                false,
                "health failed",
                new RemoteError(RemoteErrorCode.CommandFailed, "health failed")),
        };
        var service = CreateService(new FakeGit(), compose, new FakeServiceManager(), health, new MemoryAudit());
        var preview = await service.PreviewAsync(Profile(), ComposeTarget([ProcessHealth()]), cancellationToken);

        Assert.True(preview.IsSuccess, preview.Error?.Message);
        Assert.True(preview.Plan!.DeterministicRollbackPossible);

        var result = await service.ExecuteAsync(Profile(), preview.Plan, cancellationToken);

        Assert.Equal(DeploymentRunStatus.Failed, result.Status);
        Assert.NotNull(result.Rollback);
        Assert.Equal(DeploymentRollbackKind.ComposeDown, result.Rollback!.Kind);
        Assert.Equal([DockerComposeAction.Up], compose.Actions);
        Assert.Equal(1, health.RunCount);
    }

    [Fact]
    public async Task RollbackIsBlockedWhenComposeIdentityChangedAfterFailure()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var compose = new FakeCompose { Present = false };
        var health = FailingHealth();
        var service = CreateService(new FakeGit(), compose, new FakeServiceManager(), health, new MemoryAudit());
        var preview = await service.PreviewAsync(Profile(), ComposeTarget([ProcessHealth()]), cancellationToken);
        var run = await service.ExecuteAsync(Profile(), preview.Plan!, cancellationToken);
        Assert.NotNull(run.Rollback);

        compose.Image = "example/app:externally-changed";
        var rollback = await service.RollbackAsync(Profile(), run.Rollback!, cancellationToken);

        Assert.False(rollback.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, rollback.Error?.Code);
        Assert.DoesNotContain(DockerComposeAction.Down, compose.Actions);
    }

    [Fact]
    public async Task DeterministicComposeRollbackUsesVerifiedDownAndAuditsTargetIdentityOnly()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var compose = new FakeCompose { Present = false };
        var audit = new MemoryAudit();
        var service = CreateService(new FakeGit(), compose, new FakeServiceManager(), FailingHealth(), audit);
        var target = ComposeTarget([
            new DeploymentHealthCheck("secret-looking-health", DeploymentHealthCheckKind.Http, "https://internal.example.invalid/private-health"),
        ]);
        var preview = await service.PreviewAsync(Profile(), target, cancellationToken);
        var run = await service.ExecuteAsync(Profile(), preview.Plan!, cancellationToken);
        Assert.NotNull(run.Rollback);

        var rollback = await service.RollbackAsync(Profile(), run.Rollback!, cancellationToken);

        Assert.True(rollback.IsSuccess, rollback.Message);
        Assert.Equal([DockerComposeAction.Up, DockerComposeAction.Down], compose.Actions);
        Assert.False(compose.Present);
        Assert.NotEmpty(audit.Entries);
        Assert.All(audit.Entries, entry =>
        {
            Assert.Contains("deployment:compose-prod/prod", entry.Target, StringComparison.Ordinal);
            Assert.DoesNotContain("internal.example.invalid", entry.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("private-health", entry.Summary, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("internal.example.invalid", entry.Target, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static DeploymentOrchestrationService CreateService(
        FakeGit git,
        FakeCompose compose,
        FakeServiceManager services,
        FakeHealth health,
        MemoryAudit audit) =>
        new(git, compose, services, health, audit, TestOptions());

    private static DeploymentOptions TestOptions() =>
        new(TimeSpan.FromSeconds(2), 1, TimeSpan.Zero, 10);

    private static ServerProfile Profile() => ServerProfile.Create("Deploy", "example.invalid", 22, "dev");

    private static DeploymentTarget ComposeTarget(IReadOnlyList<DeploymentHealthCheck> checks) =>
        new(
            "compose-prod",
            "Compose production",
            "prod",
            DeploymentTargetKind.Compose,
            RepositoryPath: null,
            ComposeProject: Project,
            ComposeMode: DeploymentComposeMode.Up,
            ComposePull: false,
            ComposeBuild: false,
            SystemdUnit: null,
            HealthChecks: checks);

    private static DeploymentTarget GitSystemdTarget() =>
        new(
            "api-prod",
            "API production",
            "prod",
            DeploymentTargetKind.GitSystemd,
            RepositoryPath,
            ComposeProject: null,
            ComposeMode: null,
            ComposePull: false,
            ComposeBuild: false,
            SystemdUnit: "api.service",
            HealthChecks: [ProcessHealth()]);

    private static DeploymentHealthCheck ProcessHealth() =>
        new("process", DeploymentHealthCheckKind.Process, "4242");

    private static FakeHealth FailingHealth() =>
        new()
        {
            Result = new DeploymentHealthCheckResult(
                false,
                "health failed",
                new RemoteError(RemoteErrorCode.CommandFailed, "health failed")),
        };

    private sealed class FakeGit : IGitOperationsService
    {
        public string Revision { get; set; } = RevisionA;
        public int Ahead { get; set; }
        public int Behind { get; set; }
        public int BehindAfterFetch { get; set; }
        public RemoteError? FetchError { get; set; }
        public int FetchCount { get; private set; }
        public int PullCount { get; private set; }

        public Task<GitDiscoveryResult> DiscoverAsync(ServerProfile profile, string rootPath, int maximumDepth, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitDiscoveryResult([], null));

        public Task<GitRepositoryResult> InspectAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitRepositoryResult(Snapshot(Revision, Ahead, Behind), null));

        public Task<GitFetchResult> FetchAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default)
        {
            FetchCount++;
            if (FetchError is not null)
            {
                return Task.FromResult(new GitFetchResult(false, FetchError, FetchError.Message));
            }

            Behind = BehindAfterFetch;
            return Task.FromResult(new GitFetchResult(true, null, "fetched", Snapshot(Revision, 0, BehindAfterFetch)));
        }

        public Task<GitPullPreviewResult> PreviewPullAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GitPullPreviewResult(
                new GitPullPreview(
                    CanApply: Behind > 0 && Ahead == 0,
                    Message: "preview",
                    RepositoryRoot: RepositoryPath,
                    CurrentRevision: Revision,
                    Branch: "main",
                    Upstream: "origin/main",
                    Ahead,
                    Behind,
                    IncomingCommits: Behind > 0 ? ["bbbb update"] : []),
                null));

        public Task<GitPullResult> PullAsync(ServerProfile profile, string repositoryPath, string expectedRevision, CancellationToken cancellationToken = default)
        {
            PullCount++;
            Revision = RevisionB;
            Behind = 0;
            return Task.FromResult(new GitPullResult(true, null, "pulled", Snapshot(RevisionB, 0, 0)));
        }

        private static GitRepositorySnapshot Snapshot(string revision, int ahead, int behind) =>
            new(
                RepositoryPath,
                RepositoryPath,
                "main",
                revision,
                "origin/main",
                IsDetached: false,
                ahead,
                behind,
                Changes: [],
                Remotes: [],
                UnstagedDiffSummary: string.Empty,
                StagedDiffSummary: string.Empty);
    }

    private sealed class FakeCompose : IDockerComposeService
    {
        public bool Present { get; set; }
        public string Image { get; set; } = "example/app:v1";
        public List<DockerComposeAction> Actions { get; } = [];

        public Task<DockerComposeSnapshotResult> InspectAsync(ServerProfile profile, CancellationToken cancellationToken = default)
        {
            var runtime = new DockerComposeRuntimeState(DockerComposeRuntimeStatus.Available, "2.40.0", "available");
            var projects = Present ? [Project] : Array.Empty<DockerComposeProject>();
            return Task.FromResult(new DockerComposeSnapshotResult(new DockerComposeSnapshot(runtime, projects), null));
        }

        public Task<DockerComposeProjectResult> InspectProjectAsync(ServerProfile profile, DockerComposeProject project, CancellationToken cancellationToken = default) =>
            Present
                ? Task.FromResult(new DockerComposeProjectResult(Details(), null))
                : Task.FromResult(new DockerComposeProjectResult(null, new RemoteError(RemoteErrorCode.PathNotFound, "missing")));

        public Task<DockerComposeLogsResult> ReadLogsAsync(ServerProfile profile, DockerComposeProject project, int tail, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DockerComposeLogsResult([], null));

        public Task<DockerComposeActionResult> ExecuteAsync(ServerProfile profile, DockerComposeProject project, DockerComposeAction action, CancellationToken cancellationToken = default)
        {
            Actions.Add(action);
            if (action == DockerComposeAction.Down)
            {
                Present = false;
                return Task.FromResult(new DockerComposeActionResult(true, null, "down"));
            }

            if (action is DockerComposeAction.Up or DockerComposeAction.Restart)
            {
                Present = true;
            }

            return Task.FromResult(new DockerComposeActionResult(true, null, action.ToString(), Details()));
        }

        public RemoteEditValidationSpec BuildConfigValidation(DockerComposeProject project) =>
            new("docker", ["compose", "--file", "{file}", "config", "--quiet"]);

        private DockerComposeProjectDetails Details() =>
            new(
                Project,
                [new DockerComposeServiceInfo("container-1", "serverdesk-demo-api-1", "api", Image, "running", "Up", "")],
                "{\"services\":{\"api\":{}}}");
    }

    private sealed class FakeServiceManager : IServerServiceManager
    {
        public int ExecuteCount { get; private set; }

        public Task<ServerServiceQueryResult> ListAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServerServiceQueryResult([], null));

        public Task<ServerServiceQueryResult> GetAsync(ServerProfile profile, string unit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ServerServiceQueryResult(
                [new ServerServiceInfo(unit, "API", "loaded", "active", "running", "enabled", 42, string.Empty)],
                null));

        public Task<ServerServiceActionResult> ExecuteAsync(ServerProfile profile, string unit, ServerServiceAction action, CancellationToken cancellationToken = default)
        {
            ExecuteCount++;
            return Task.FromResult(new ServerServiceActionResult(
                true,
                null,
                "restarted",
                new ServerServiceInfo(unit, "API", "loaded", "active", "running", "enabled", 42, string.Empty)));
        }
    }

    private sealed class FakeHealth : IDeploymentHealthCheckRunner
    {
        public int RunCount { get; private set; }
        public DeploymentHealthCheckResult Result { get; set; } = new(true, "healthy");

        public Task<DeploymentHealthCheckResult> RunAsync(ServerProfile profile, DeploymentHealthCheck check, CancellationToken cancellationToken = default)
        {
            RunCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class MemoryAudit : IOperationAudit
    {
        public List<OperationAuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OperationAuditEntry>>(Entries.Take(limit).ToArray());
    }
}
