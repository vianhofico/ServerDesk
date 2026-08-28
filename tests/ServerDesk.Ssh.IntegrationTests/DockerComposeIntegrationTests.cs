using System.Globalization;
using System.Text;
using ServerDesk.Application.Docker;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteEditing;
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
    public async Task ComposeProjectActionsAndRawYamlValidationCrossOpenSshAndSftp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var token = Guid.NewGuid().ToString("N");
        var scriptPath = RemotePath.Parse($"{Home}/serverdesk-compose-{token}");
        var configPath = RemotePath.Parse(scriptPath.Value + ".yaml");
        var statePath = RemotePath.Parse(scriptPath.Value + ".state");
        const string originalYaml = "x-common: &common\n  restart: unless-stopped\nservices:\n  api:\n    <<: *common\n    image: example/api:latest\n";
        await InstallFixtureAsync(fixture, scriptPath, configPath, originalYaml, cancellationToken);

        try
        {
            var commandFactory = new ExecutableRewriteFactory(fixture.CommandFactory, scriptPath.Value);
            var editor = new GuardedRemoteFileEditorService(fixture.FileSystemFactory, commandFactory);
            var service = new DockerComposeService(
                commandFactory,
                fixture.FileSystemFactory,
                editor,
                DockerComposeOptions.Default);

            var listed = await service.ListProjectsAsync(fixture.Profile, cancellationToken);
            Assert.True(listed.IsSuccess, listed.Error?.Message);
            Assert.Equal("2.39.1", listed.Runtime.Version);
            var project = Assert.Single(listed.Projects);
            Assert.Equal("serverdesk", project.Name);
            Assert.Equal(configPath, project.PrimaryConfigFile);

            var state = await service.ReadProjectAsync(fixture.Profile, project, cancellationToken);
            Assert.True(state.IsSuccess, state.Error?.Message);
            var api = Assert.Single(state.Services);
            Assert.Equal("api", api.Service);
            Assert.Equal("8080->80/tcp", api.Ports);

            var logs = await service.ReadLogsAsync(fixture.Profile, project, 20, cancellationToken);
            Assert.True(logs.IsSuccess, logs.Error?.Message);
            Assert.Contains(logs.Lines, line => line.Contains("ready", StringComparison.Ordinal));

            var document = await service.LoadConfigAsync(fixture.Profile, project, configPath, cancellationToken);
            Assert.Equal(originalYaml, document.Text);

            var invalid = await service.SaveConfigAsync(
                fixture.Profile,
                project,
                document,
                originalYaml + "# INVALID_COMPOSE\n",
                privileged: false,
                cancellationToken);
            Assert.False(invalid.IsSuccess);
            Assert.True(invalid.ValidationFailed);
            var afterRejected = await service.LoadConfigAsync(fixture.Profile, project, configPath, cancellationToken);
            Assert.Equal(originalYaml, afterRejected.Text);

            const string validYaml = "x-common: &common\n  restart: unless-stopped\nservices:\n  api:\n    <<: *common\n    image: example/api:v2\n";
            var saved = await service.SaveConfigAsync(
                fixture.Profile,
                project,
                afterRejected,
                validYaml,
                privileged: false,
                cancellationToken);
            Assert.True(saved.IsSuccess, saved.Error?.Message);
            var afterSaved = await service.LoadConfigAsync(fixture.Profile, project, configPath, cancellationToken);
            Assert.Equal(validYaml, afterSaved.Text);

            var down = await service.ExecuteAsync(
                fixture.Profile,
                project,
                DockerComposeAction.Down,
                cancellationToken);
            Assert.True(down.IsSuccess, down.Error?.Message);
            Assert.Empty(down.VerifiedState!.Services);

            var up = await service.ExecuteAsync(
                fixture.Profile,
                project,
                DockerComposeAction.Up,
                cancellationToken);
            Assert.True(up.IsSuccess, up.Error?.Message);
            Assert.Single(up.VerifiedState!.Services);
        }
        finally
        {
            await CleanupAsync(fixture, [scriptPath, configPath, statePath], CancellationToken.None);
        }
    }

    private static async Task InstallFixtureAsync(
        ComposeFixture fixture,
        RemotePath scriptPath,
        RemotePath configPath,
        string config,
        CancellationToken cancellationToken)
    {
        var script = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "docker-compose-fixture.sh"),
            cancellationToken);
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        await UploadAsync(fileSystem, scriptPath, script, overwrite: false, cancellationToken);
        await fileSystem.SetPermissionsAsync(scriptPath, RemoteUnixPermissions.FromMode(700), cancellationToken);
        await UploadAsync(fileSystem, configPath, config, overwrite: false, cancellationToken);
        await fileSystem.SetPermissionsAsync(configPath, RemoteUnixPermissions.FromMode(644), cancellationToken);
    }

    private static async ValueTask UploadAsync(
        IRemoteFileSystem fileSystem,
        RemotePath path,
        string text,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(text);
        await using var stream = new MemoryStream(payload, writable: false);
        await fileSystem.UploadAsync(
            stream,
            path,
            payload.Length,
            overwrite,
            cancellationToken: cancellationToken);
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

        public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default) =>
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
