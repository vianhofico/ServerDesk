using System.Globalization;
using System.Text;
using ServerDesk.Application.Docker;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class DockerComposeIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task ComposeDiscoveryLogsAndLifecycleCrossOpenSshWithVerification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var token = Guid.NewGuid().ToString("N");
        var scriptPath = RemotePath.Parse($"{Home}/serverdesk-compose-{token}");
        var statePath = RemotePath.Parse(scriptPath.Value + ".state");
        await InstallFixtureAsync(fixture, scriptPath, cancellationToken);

        try
        {
            var commandFactory = new ExecutableRewriteFactory(fixture.CommandFactory, scriptPath.Value);
            var service = new DockerComposeService(commandFactory, DockerComposeOptions.Default);

            var snapshot = await service.InspectAsync(fixture.Profile, cancellationToken);
            Assert.True(snapshot.IsSuccess, snapshot.Error?.Message);
            Assert.Equal(DockerComposeRuntimeStatus.Available, snapshot.Snapshot?.Runtime.Status);
            var project = Assert.Single(snapshot.Snapshot!.Projects);
            Assert.Equal("serverdesk-demo", project.Name);
            Assert.Equal("/srv/demo/compose.yaml", project.PrimaryConfigFile);

            var details = await service.InspectProjectAsync(fixture.Profile, project, cancellationToken);
            Assert.True(details.IsSuccess, details.Error?.Message);
            var composeService = Assert.Single(details.Details!.Services);
            Assert.Equal("api", composeService.Service);
            Assert.Contains("services", details.Details.NormalizedConfigJson, StringComparison.Ordinal);

            var logs = await service.ReadLogsAsync(fixture.Profile, project, 50, cancellationToken);
            Assert.True(logs.IsSuccess, logs.Error?.Message);
            Assert.Equal(2, logs.Lines.Count);
            Assert.Contains(logs.Lines, line => line.Contains("healthy", StringComparison.Ordinal));

            var restarted = await service.ExecuteAsync(
                fixture.Profile,
                project,
                DockerComposeAction.Restart,
                cancellationToken);
            Assert.True(restarted.IsSuccess, restarted.Error?.Message);
            Assert.Single(restarted.VerifiedDetails!.Services);

            var down = await service.ExecuteAsync(
                fixture.Profile,
                project,
                DockerComposeAction.Down,
                cancellationToken);
            Assert.True(down.IsSuccess, down.Error?.Message);

            var afterDown = await service.InspectAsync(fixture.Profile, cancellationToken);
            Assert.True(afterDown.IsSuccess, afterDown.Error?.Message);
            Assert.Empty(afterDown.Snapshot!.Projects);

            var up = await service.ExecuteAsync(
                fixture.Profile,
                project,
                DockerComposeAction.Up,
                cancellationToken);
            Assert.True(up.IsSuccess, up.Error?.Message);
            Assert.Single(up.VerifiedDetails!.Services);
        }
        finally
        {
            await CleanupAsync(fixture, [scriptPath, statePath], CancellationToken.None);
        }
    }

    private static async Task InstallFixtureAsync(
        ComposeFixture fixture,
        RemotePath remotePath,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "docker-compose-fixture.sh"),
            cancellationToken);
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        var payload = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(payload, writable: false);
        await fileSystem.UploadAsync(
            stream,
            remotePath,
            payload.Length,
            overwrite: false,
            cancellationToken: cancellationToken);
        await fileSystem.SetPermissionsAsync(remotePath, RemoteUnixPermissions.FromMode(700), cancellationToken);
    }

    private static async Task CleanupAsync(
        ComposeFixture fixture,
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

    private static ComposeFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Compose fixture",
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
        return new ComposeFixture(
            profile,
            new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options),
            new SftpRemoteFileSystemFactory(secretStore, trust, prompt, options));
    }

    private sealed record ComposeFixture(
        ServerProfile Profile,
        IRemoteCommandExecutorFactory CommandFactory,
        IRemoteFileSystemFactory FileSystemFactory);

    private sealed class ExecutableRewriteFactory : IRemoteCommandExecutorFactory
    {
        private readonly IRemoteCommandExecutorFactory _inner;
        private readonly string _dockerExecutable;

        public ExecutableRewriteFactory(IRemoteCommandExecutorFactory inner, string dockerExecutable)
        {
            _inner = inner;
            _dockerExecutable = dockerExecutable;
        }

        public IRemoteCommandExecutor Create(ServerProfile profile) =>
            new ExecutableRewriteExecutor(_inner.Create(profile), _dockerExecutable);
    }

    private sealed class ExecutableRewriteExecutor : IRemoteCommandExecutor
    {
        private readonly IRemoteCommandExecutor _inner;
        private readonly string _dockerExecutable;

        public ExecutableRewriteExecutor(IRemoteCommandExecutor inner, string dockerExecutable)
        {
            _inner = inner;
            _dockerExecutable = dockerExecutable;
        }

        public Guid ServerProfileId => _inner.ServerProfileId;

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default) =>
            _inner.ExecuteAsync(
                command.Executable == "docker" ? command with { Executable = _dockerExecutable } : command,
                cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
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
