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

public sealed class DockerContainerActionIntegrationTests
{
    private const string ContainerId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task LifecycleActionsCrossOpenSshWithTokenizedIdsAndVerifiedState()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var token = Guid.NewGuid().ToString("N");
        var scriptPath = RemotePath.Parse($"{Home}/serverdesk-docker-actions-{token}");
        var statePath = RemotePath.Parse(scriptPath.Value + ".state");
        await InstallFixtureAsync(fixture, scriptPath, cancellationToken);

        try
        {
            var commandFactory = new ExecutableRewriteFactory(fixture.CommandFactory, scriptPath.Value);
            var diagnostics = new DockerContainerDiagnosticsService(
                commandFactory,
                DockerContainerDiagnosticsOptions.Default);
            var service = new DockerContainerActionService(
                commandFactory,
                diagnostics,
                DockerContainerActionOptions.Default);

            var started = await service.ExecuteAsync(
                fixture.Profile,
                ContainerId,
                DockerContainerAction.Start,
                cancellationToken);
            Assert.True(started.IsSuccess, started.Error?.Message);
            Assert.True(started.VerifiedDetails?.State.Running);
            Assert.False(started.VerifiedDetails?.State.Paused);

            var paused = await service.ExecuteAsync(
                fixture.Profile,
                ContainerId,
                DockerContainerAction.Pause,
                cancellationToken);
            Assert.True(paused.IsSuccess, paused.Error?.Message);
            Assert.True(paused.VerifiedDetails?.State.Paused);

            var unpaused = await service.ExecuteAsync(
                fixture.Profile,
                ContainerId,
                DockerContainerAction.Unpause,
                cancellationToken);
            Assert.True(unpaused.IsSuccess, unpaused.Error?.Message);
            Assert.True(unpaused.VerifiedDetails?.State.Running);
            Assert.False(unpaused.VerifiedDetails?.State.Paused);

            var restarted = await service.ExecuteAsync(
                fixture.Profile,
                ContainerId,
                DockerContainerAction.Restart,
                cancellationToken);
            Assert.True(restarted.IsSuccess, restarted.Error?.Message);
            Assert.Equal("2026-08-28T04:01:00Z", restarted.VerifiedDetails?.State.StartedAt);

            var killed = await service.ExecuteAsync(
                fixture.Profile,
                ContainerId,
                DockerContainerAction.Kill,
                cancellationToken);
            Assert.True(killed.IsSuccess, killed.Error?.Message);
            Assert.False(killed.VerifiedDetails?.State.Running);

            var removed = await service.ExecuteAsync(
                fixture.Profile,
                ContainerId,
                DockerContainerAction.Remove,
                cancellationToken);
            Assert.True(removed.IsSuccess, removed.Error?.Message);
            Assert.Null(removed.VerifiedDetails);
        }
        finally
        {
            await CleanupAsync(fixture, [scriptPath, statePath], CancellationToken.None);
        }
    }

    private static async Task InstallFixtureAsync(
        DockerFixture fixture,
        RemotePath remotePath,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "docker-actions-fixture.sh"),
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
        await fileSystem.SetPermissionsAsync(
            remotePath,
            RemoteUnixPermissions.FromMode(700),
            cancellationToken);
    }

    private static async Task CleanupAsync(
        DockerFixture fixture,
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

    private static DockerFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Docker actions fixture",
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
        return new DockerFixture(
            profile,
            new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options),
            new SftpRemoteFileSystemFactory(secretStore, trust, prompt, options));
    }

    private sealed record DockerFixture(
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
            return ValueTask.FromResult(new HostTrustVerification(
                HostTrustOutcome.TrustedOnce,
                observation,
                []));
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
