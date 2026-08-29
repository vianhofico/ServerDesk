using ServerDesk.Application.Audit;
using ServerDesk.Application.Deployment;
using ServerDesk.Application.Docker;
using ServerDesk.Application.Git;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.Services;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DeploymentPlanIntegrityTests
{
    private static readonly DockerComposeProject Project = new("serverdesk-demo", "", ["/srv/demo/compose.yaml"]);

    [Fact]
    public void DuplicateHealthCheckNamesAreRejectedBeforeRemoteWork()
    {
        var target = Target([
            new DeploymentHealthCheck("ready", DeploymentHealthCheckKind.Process, "123"),
            new DeploymentHealthCheck("ready", DeploymentHealthCheckKind.Process, "456"),
        ]);

        var exception = Assert.Throws<ArgumentException>(() =>
            DeploymentTargetPolicy.Normalize(target, DeploymentOptions.Default));

        Assert.Contains("unique", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ChangingHealthTargetAfterPreviewConsumesAndRejectsPlanBeforeMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var compose = new ComposeFake();
        var service = CreateService(compose);
        var preview = await service.PreviewAsync(Profile(), Target([ProcessHealth("123")]), cancellationToken);
        Assert.True(preview.IsSuccess, preview.Error?.Message);

        var tampered = preview.Plan! with
        {
            Target = preview.Plan.Target with
            {
                HealthChecks = [ProcessHealth("456")],
            },
        };
        var result = await service.ExecuteAsync(Profile(), tampered, cancellationToken);

        Assert.Equal(DeploymentRunStatus.Failed, result.Status);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.Empty(compose.Actions);
    }

    [Fact]
    public async Task ChangingPreviewedStepSequenceIsRejectedBeforeMutation()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var compose = new ComposeFake();
        var service = CreateService(compose);
        var preview = await service.PreviewAsync(Profile(), Target([ProcessHealth("123")]), cancellationToken);
        Assert.True(preview.IsSuccess, preview.Error?.Message);

        var steps = preview.Plan!.Steps.Reverse().ToArray();
        var tampered = preview.Plan with { Steps = steps };
        var result = await service.ExecuteAsync(Profile(), tampered, cancellationToken);

        Assert.Equal(DeploymentRunStatus.Failed, result.Status);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error?.Code);
        Assert.Empty(compose.Actions);
    }

    [Fact]
    public async Task PreviewPlanIsSingleUseEvenAfterSuccessfulExecution()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var compose = new ComposeFake();
        var service = CreateService(compose);
        var preview = await service.PreviewAsync(Profile(), Target([ProcessHealth("123")]), cancellationToken);
        Assert.True(preview.IsSuccess, preview.Error?.Message);

        var first = await service.ExecuteAsync(Profile(), preview.Plan!, cancellationToken);
        Assert.True(first.IsSuccess, first.Message);
        Assert.Equal([DockerComposeAction.Up], compose.Actions);

        var second = await service.ExecuteAsync(Profile(), preview.Plan!, cancellationToken);
        Assert.Equal(DeploymentRunStatus.Failed, second.Status);
        Assert.Equal(RemoteErrorCode.PathConflict, second.Error?.Code);
        Assert.Equal([DockerComposeAction.Up], compose.Actions);
    }

    private static DeploymentOrchestrationService CreateService(ComposeFake compose) =>
        new(
            new GitFake(),
            compose,
            new ServiceFake(),
            new PassingHealth(),
            new MemoryAudit(),
            new DeploymentOptions(TimeSpan.FromSeconds(2), 1, TimeSpan.Zero, 10));

    private static DeploymentTarget Target(IReadOnlyList<DeploymentHealthCheck> checks) =>
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

    private static DeploymentHealthCheck ProcessHealth(string pid) =>
        new("process", DeploymentHealthCheckKind.Process, pid);

    private static ServerProfile Profile() => ServerProfile.Create("Deploy", "example.invalid", 22, "dev");

    private sealed class ComposeFake : IDockerComposeService
    {
        public bool Present { get; private set; }
        public List<DockerComposeAction> Actions { get; } = [];

        public Task<DockerComposeSnapshotResult> InspectAsync(ServerProfile profile, CancellationToken cancellationToken = default)
        {
            var runtime = new DockerComposeRuntimeState(DockerComposeRuntimeStatus.Available, "2.40.0", "available");
            IReadOnlyList<DockerComposeProject> projects = Present ? [Project] : [];
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

            Present = true;
            return Task.FromResult(new DockerComposeActionResult(true, null, action.ToString(), Details()));
        }

        public RemoteEditValidationSpec BuildConfigValidation(DockerComposeProject project) =>
            new("docker", ["compose", "--file", "{file}", "config", "--quiet"]);

        private static DockerComposeProjectDetails Details() =>
            new(
                Project,
                [new DockerComposeServiceInfo("container-1", "serverdesk-demo-api-1", "api", "demo/api:latest", "running", "Up", "")],
                "{\"services\":{\"api\":{}}}");
    }

    private sealed class GitFake : IGitOperationsService
    {
        public Task<GitDiscoveryResult> DiscoverAsync(ServerProfile profile, string rootPath, int maximumDepth, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Git must not be used by Compose-only integrity tests.");

        public Task<GitRepositoryResult> InspectAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Git must not be used by Compose-only integrity tests.");

        public Task<GitFetchResult> FetchAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Git must not be used by Compose-only integrity tests.");

        public Task<GitPullPreviewResult> PreviewPullAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Git must not be used by Compose-only integrity tests.");

        public Task<GitPullResult> PullAsync(ServerProfile profile, string repositoryPath, string expectedRevision, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Git must not be used by Compose-only integrity tests.");
    }

    private sealed class ServiceFake : IServerServiceManager
    {
        public Task<ServerServiceQueryResult> ListAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("systemd must not be used by Compose-only integrity tests.");

        public Task<ServerServiceQueryResult> GetAsync(ServerProfile profile, string unit, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("systemd must not be used by Compose-only integrity tests.");

        public Task<ServerServiceActionResult> ExecuteAsync(ServerProfile profile, string unit, ServerServiceAction action, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("systemd must not be used by Compose-only integrity tests.");
    }

    private sealed class PassingHealth : IDeploymentHealthCheckRunner
    {
        public Task<DeploymentHealthCheckResult> RunAsync(ServerProfile profile, DeploymentHealthCheck check, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DeploymentHealthCheckResult(true, "healthy"));
    }

    private sealed class MemoryAudit : IOperationAudit
    {
        public ValueTask AppendAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OperationAuditEntry>>([]);
    }
}
