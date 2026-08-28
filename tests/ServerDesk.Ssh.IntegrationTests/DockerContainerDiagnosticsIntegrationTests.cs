using System.Globalization;
using System.Text;
using ServerDesk.Application.Docker;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class DockerContainerDiagnosticsIntegrationTests
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
    public async Task DiagnosticsUseStructuredTokensRedactSecretsAndPreserveIndependentChannel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var token = Guid.NewGuid().ToString("N");
        var diagnosticsPath = RemotePath.Parse($"{Home}/serverdesk-docker-diag-{token}");
        var slowPath = RemotePath.Parse($"{Home}/serverdesk-docker-diag-slow-{token}");
        await InstallFixtureAsync(fixture, diagnosticsPath, "docker-diagnostics-fixture.sh", cancellationToken);
        await InstallFixtureAsync(fixture, slowPath, "docker-diagnostics-slow-fixture.sh", cancellationToken);

        try
        {
            var service = new DockerContainerDiagnosticsService(
                new ExecutableRewriteFactory(fixture.CommandFactory, diagnosticsPath.Value),
                DockerContainerDiagnosticsOptions.Default);

            var inspect = await service.InspectAsync(fixture.Profile, ContainerId, cancellationToken);
            Assert.True(inspect.IsSuccess, inspect.Error?.Message);
            Assert.NotNull(inspect.Details);
            Assert.Equal("fixture-api", inspect.Details.Name);
            var secret = Assert.Single(inspect.Details.Environment, item => item.Name == "DATABASE_PASSWORD");
            Assert.True(secret.IsSensitive);
            Assert.Equal("••••••", secret.DisplayValue);
            Assert.DoesNotContain("hidden-value", inspect.Details.Environment.Select(item => item.DisplayValue));

            var stats = await service.ReadStatsAsync(fixture.Profile, ContainerId, cancellationToken);
            Assert.True(stats.IsSuccess, stats.Error?.Message);
            Assert.NotNull(stats.Stats);
            Assert.Equal(7.5, stats.Stats.CpuPercent);
            Assert.Equal(268_435_456, stats.Stats.MemoryUsageBytes);
            Assert.Equal(1_073_741_824, stats.Stats.MemoryLimitBytes);
            Assert.Equal(3, stats.Stats.ProcessCount);

            var recent = await service.ReadRecentLogsAsync(fixture.Profile, ContainerId, 20, cancellationToken);
            Assert.True(recent.IsSuccess, recent.Error?.Message);
            Assert.Equal(2, recent.Entries.Count);
            Assert.Equal("2026-08-28T04:00:02Z", recent.LastTimestampToken);

            var since = await service.ReadLogsSinceAsync(
                fixture.Profile,
                ContainerId,
                "2026-08-28T04:00:02Z",
                20,
                cancellationToken);
            Assert.True(since.IsSuccess, since.Error?.Message);
            var incremental = Assert.Single(since.Entries);
            Assert.Equal("incremental", incremental.Message);
            Assert.Equal("2026-08-28T04:00:03Z", since.LastTimestampToken);

            var slowService = new DockerContainerDiagnosticsService(
                new ExecutableRewriteFactory(fixture.CommandFactory, slowPath.Value),
                DockerContainerDiagnosticsOptions.Default);
            using var slowCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var slowRead = slowService.ReadStatsAsync(fixture.Profile, ContainerId, slowCancellation.Token);
            await Task.Delay(150, cancellationToken);

            await using var independentExecutor = fixture.CommandFactory.Create(fixture.Profile);
            var independent = await independentExecutor.ExecuteAsync(
                RemoteCommandSpec.ReadOnly("printf", "docker-diagnostics-channel-ok"),
                cancellationToken);
            Assert.True(independent.IsSuccess, independent.Error?.Message);
            Assert.Equal("docker-diagnostics-channel-ok", independent.Command?.StandardOutput);

            slowCancellation.Cancel();
            var cancelled = await slowRead;
            Assert.False(cancelled.IsSuccess);
            Assert.Equal(RemoteErrorCode.OperationCancelled, cancelled.Error?.Code);
        }
        finally
        {
            await CleanupAsync(fixture, [diagnosticsPath, slowPath], CancellationToken.None);
        }
    }

    private static async Task InstallFixtureAsync(
        DockerFixture fixture,
        RemotePath remotePath,
        string fixtureName,
        CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", fixtureName),
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
            "Docker diagnostics fixture",
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
