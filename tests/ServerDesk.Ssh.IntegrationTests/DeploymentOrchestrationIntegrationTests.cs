using System.Globalization;
using System.Text;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Deployment;
using ServerDesk.Application.Docker;
using ServerDesk.Application.Git;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Services;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class DeploymentOrchestrationIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";
    private static readonly DockerComposeProject Project = new("serverdesk-demo", "", ["/srv/demo/compose.yaml"]);

    [Fact]
    public async Task ComposeUpHealthFailureAndExplicitRollbackCrossOpenSshWithVerification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var token = Guid.NewGuid().ToString("N");
        var scriptPath = RemotePath.Parse($"{Home}/serverdesk-deploy-compose-{token}");
        var statePath = RemotePath.Parse(scriptPath.Value + ".state");
        await InstallFixtureAsync(fixture, scriptPath, statePath, cancellationToken);

        try
        {
            var commands = new RecordingRewriteFactory(fixture.CommandFactory, scriptPath.Value);
            var compose = new DockerComposeService(commands, DockerComposeOptions.Default);
            var options = new DeploymentOptions(TimeSpan.FromSeconds(4), 1, TimeSpan.Zero, 5);
            var audit = new MemoryAudit();
            var health = new DeploymentHealthCheckRunner(
                commands,
                new RejectServiceManager(),
                new RejectDockerInventory(),
                options);
            var service = new DeploymentOrchestrationService(
                new RejectGit(),
                compose,
                new RejectServiceManager(),
                health,
                audit,
                options);
            var target = new DeploymentTarget(
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
                HealthChecks: [new DeploymentHealthCheck("missing-process", DeploymentHealthCheckKind.Process, "2147483647")]);

            var preview = await service.PreviewAsync(fixture.Profile, target, cancellationToken);
            Assert.True(preview.IsSuccess, preview.Error?.Message);
            Assert.True(preview.Plan!.DeterministicRollbackPossible);
            Assert.Equal(
                [DeploymentStepKind.ComposeUp, DeploymentStepKind.HealthCheck],
                preview.Plan.Steps.Select(step => step.Kind).ToArray());

            var run = await service.ExecuteAsync(fixture.Profile, preview.Plan, cancellationToken);
            Assert.Equal(DeploymentRunStatus.Failed, run.Status);
            Assert.NotNull(run.Rollback);
            Assert.Equal(DeploymentStepOutcome.Succeeded, run.Steps[0].Outcome);
            Assert.Equal(DeploymentStepOutcome.Failed, run.Steps[1].Outcome);

            var afterFailure = await compose.InspectAsync(fixture.Profile, cancellationToken);
            Assert.True(afterFailure.IsSuccess, afterFailure.Error?.Message);
            Assert.Single(afterFailure.Snapshot!.Projects);

            var rollback = await service.RollbackAsync(fixture.Profile, run.Rollback!, cancellationToken);
            Assert.True(rollback.IsSuccess, rollback.Message);

            var afterRollback = await compose.InspectAsync(fixture.Profile, cancellationToken);
            Assert.True(afterRollback.IsSuccess, afterRollback.Error?.Message);
            Assert.Empty(afterRollback.Snapshot!.Projects);

            Assert.Equal(1, commands.ComposeMutationCount("up"));
            Assert.Equal(1, commands.ComposeMutationCount("down"));
            Assert.Contains(commands.Commands, command =>
                command.Executable == "ps" &&
                command.Arguments.SequenceEqual(["-p", "2147483647", "-o", "pid="]));
            Assert.Contains(audit.Entries, entry => entry.Summary.Contains("ComposeUp", StringComparison.Ordinal));
            Assert.Contains(audit.Entries, entry => entry.Summary.Contains("HealthCheck", StringComparison.Ordinal));
            Assert.Contains(audit.Entries, entry => entry.Summary.Contains("RollbackComposeDown", StringComparison.Ordinal));
        }
        finally
        {
            await CleanupAsync(fixture, [scriptPath, statePath], CancellationToken.None);
        }
    }

    private static async Task InstallFixtureAsync(
        DeploymentFixture fixture,
        RemotePath scriptPath,
        RemotePath statePath,
        CancellationToken cancellationToken)
    {
        var script = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "docker-compose-fixture.sh"),
            cancellationToken);
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);

        var scriptBytes = Encoding.UTF8.GetBytes(script);
        await using (var scriptStream = new MemoryStream(scriptBytes, writable: false))
        {
            await fileSystem.UploadAsync(
                scriptStream,
                scriptPath,
                scriptBytes.Length,
                overwrite: false,
                cancellationToken: cancellationToken);
        }

        await fileSystem.SetPermissionsAsync(scriptPath, RemoteUnixPermissions.FromMode(700), cancellationToken);

        var stateBytes = Encoding.UTF8.GetBytes("down\n");
        await using var stateStream = new MemoryStream(stateBytes, writable: false);
        await fileSystem.UploadAsync(
            stateStream,
            statePath,
            stateBytes.Length,
            overwrite: false,
            cancellationToken: cancellationToken);
    }

    private static async Task CleanupAsync(
        DeploymentFixture fixture,
        IReadOnlyList<RemotePath> paths,
        CancellationToken cancellationToken)
    {
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        try
        {
            await fileSystem.ConnectAsync(cancellationToken);
            foreach (var path in paths)
            {
                try
                {
                    await fileSystem.DeleteFileAsync(path, cancellationToken);
                }
                catch
                {
                }
            }
        }
        catch
        {
        }
    }

    private static DeploymentFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Deployment fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var secretStore = new MemorySecretStore(reference, Password);
        var trust = new TrustOnceHostTrustService();
        var prompt = new RejectInteractivePrompt();
        var options = new SshSessionOptions(
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));
        return new DeploymentFixture(
            profile,
            new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options),
            new SftpRemoteFileSystemFactory(secretStore, trust, prompt, options));
    }

    private sealed record DeploymentFixture(
        ServerProfile Profile,
        IRemoteCommandExecutorFactory CommandFactory,
        IRemoteFileSystemFactory FileSystemFactory);

    private sealed class RecordingRewriteFactory : IRemoteCommandExecutorFactory
    {
        private readonly IRemoteCommandExecutorFactory _inner;
        private readonly string _dockerExecutable;
        private readonly object _gate = new();
        private readonly List<RemoteCommandSpec> _commands = [];

        public RecordingRewriteFactory(IRemoteCommandExecutorFactory inner, string dockerExecutable)
        {
            _inner = inner;
            _dockerExecutable = dockerExecutable;
        }

        public IReadOnlyList<RemoteCommandSpec> Commands
        {
            get
            {
                lock (_gate)
                {
                    return _commands.ToArray();
                }
            }
        }

        public int ComposeMutationCount(string verb) => Commands.Count(command =>
            command.Executable == "docker" &&
            command.Arguments.Contains("compose", StringComparer.Ordinal) &&
            command.Arguments.Contains(verb, StringComparer.Ordinal));

        public IRemoteCommandExecutor Create(ServerProfile profile) =>
            new RecordingRewriteExecutor(_inner.Create(profile), this, _dockerExecutable);

        private void Record(RemoteCommandSpec command)
        {
            lock (_gate)
            {
                _commands.Add(command);
            }
        }

        private sealed class RecordingRewriteExecutor : IRemoteCommandExecutor
        {
            private readonly IRemoteCommandExecutor _inner;
            private readonly RecordingRewriteFactory _owner;
            private readonly string _dockerExecutable;

            public RecordingRewriteExecutor(
                IRemoteCommandExecutor inner,
                RecordingRewriteFactory owner,
                string dockerExecutable)
            {
                _inner = inner;
                _owner = owner;
                _dockerExecutable = dockerExecutable;
            }

            public Guid ServerProfileId => _inner.ServerProfileId;

            public Task<RemoteExecutionResult> ExecuteAsync(
                RemoteCommandSpec command,
                CancellationToken cancellationToken = default)
            {
                _owner.Record(command);
                return _inner.ExecuteAsync(
                    command.Executable == "docker" ? command with { Executable = _dockerExecutable } : command,
                    cancellationToken);
            }

            public ValueTask DisposeAsync() => _inner.DisposeAsync();
        }
    }

    private sealed class RejectGit : IGitOperationsService
    {
        public Task<GitDiscoveryResult> DiscoverAsync(ServerProfile profile, string rootPath, int maximumDepth, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Git is outside the Compose-only integration path.");

        public Task<GitRepositoryResult> InspectAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Git is outside the Compose-only integration path.");

        public Task<GitFetchResult> FetchAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Git is outside the Compose-only integration path.");

        public Task<GitPullPreviewResult> PreviewPullAsync(ServerProfile profile, string repositoryPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Git is outside the Compose-only integration path.");

        public Task<GitPullResult> PullAsync(ServerProfile profile, string repositoryPath, string expectedRevision, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Git is outside the Compose-only integration path.");
    }

    private sealed class RejectServiceManager : IServerServiceManager
    {
        public Task<ServerServiceQueryResult> ListAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("systemd is outside this integration path.");

        public Task<ServerServiceQueryResult> GetAsync(ServerProfile profile, string unit, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("systemd is outside this integration path.");

        public Task<ServerServiceActionResult> ExecuteAsync(ServerProfile profile, string unit, ServerServiceAction action, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("systemd is outside this integration path.");
    }

    private sealed class RejectDockerInventory : IDockerInventoryService
    {
        public Task<DockerInventoryResult> InspectAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Docker inventory is outside the process-health integration path.");
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

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly SecretReference _reference;
        private readonly string _secret;

        public MemorySecretStore(SecretReference reference, string secret)
        {
            _reference = reference;
            _secret = secret;
        }

        public ValueTask SetAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(reference == _reference ? _secret : null);
        }

        public ValueTask DeleteAsync(SecretReference reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TrustOnceHostTrustService : IHostTrustService
    {
        public ValueTask<HostTrustVerification> VerifyAsync(
            HostKeyObservation observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new HostTrustVerification(HostTrustOutcome.TrustedOnce, observation, []));
        }
    }

    private sealed class RejectInteractivePrompt : IInteractiveAuthenticationPrompt
    {
        public ValueTask<IReadOnlyList<string>?> PromptAsync(
            InteractiveAuthenticationChallenge challenge,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Password fixture must not request keyboard-interactive authentication.");
    }
}
