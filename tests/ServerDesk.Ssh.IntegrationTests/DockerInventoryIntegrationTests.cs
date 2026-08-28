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

public sealed class DockerInventoryIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task InventoryPermissionCancellationAndChannelIsolationWorkThroughSsh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var token = Guid.NewGuid().ToString("N");
        var usableScript = RemotePath.Parse($"{Home}/serverdesk-docker-{token}");
        var deniedScript = RemotePath.Parse($"{Home}/serverdesk-docker-denied-{token}");
        var slowScript = RemotePath.Parse($"{Home}/serverdesk-docker-slow-{token}");
        await InstallScriptAsync(fixture, usableScript, UsableDockerScript, cancellationToken);
        await InstallScriptAsync(fixture, deniedScript, DeniedDockerScript, cancellationToken);
        await InstallScriptAsync(fixture, slowScript, SlowDockerScript, cancellationToken);

        try
        {
            var usableService = new DockerInventoryService(
                new ExecutableRewriteFactory(fixture.CommandFactory, usableScript.Value),
                DockerInventoryOptions.Default);
            var inventory = await usableService.InspectAsync(fixture.Profile, cancellationToken);

            Assert.True(inventory.IsSuccess, inventory.Error?.Message);
            Assert.False(inventory.IsPartial);
            Assert.NotNull(inventory.Snapshot);
            Assert.Equal(DockerRuntimeStatus.Available, inventory.Snapshot.Runtime.Status);
            Assert.Equal("27.5.1", inventory.Snapshot.Runtime.EngineVersion);
            Assert.Equal("1.47", inventory.Snapshot.Runtime.ApiVersion);
            Assert.Single(inventory.Snapshot.Containers);
            Assert.Single(inventory.Snapshot.Images);
            Assert.Single(inventory.Snapshot.Volumes);
            Assert.Single(inventory.Snapshot.Networks);
            Assert.Equal("fixture-api", inventory.Snapshot.Containers[0].Name);

            var deniedService = new DockerInventoryService(
                new ExecutableRewriteFactory(fixture.CommandFactory, deniedScript.Value),
                DockerInventoryOptions.Default);
            var denied = await deniedService.InspectAsync(fixture.Profile, cancellationToken);

            Assert.True(denied.IsSuccess);
            Assert.NotNull(denied.Snapshot);
            Assert.Equal(DockerRuntimeStatus.PermissionDenied, denied.Snapshot.Runtime.Status);

            var slowService = new DockerInventoryService(
                new ExecutableRewriteFactory(fixture.CommandFactory, slowScript.Value),
                DockerInventoryOptions.Default);
            using var slowCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var slowRead = slowService.InspectAsync(fixture.Profile, slowCancellation.Token);
            await Task.Delay(150, cancellationToken);

            await using var independentExecutor = fixture.CommandFactory.Create(fixture.Profile);
            var independent = await independentExecutor.ExecuteAsync(
                RemoteCommandSpec.ReadOnly("printf", "docker-channel-ok"),
                cancellationToken);
            Assert.True(independent.IsSuccess, independent.Error?.Message);
            Assert.Equal("docker-channel-ok", independent.Command?.StandardOutput);

            slowCancellation.Cancel();
            var cancelled = await slowRead;
            Assert.False(cancelled.IsSuccess);
            Assert.Equal(RemoteErrorCode.OperationCancelled, cancelled.Error?.Code);
        }
        finally
        {
            await CleanupAsync(fixture, [usableScript, deniedScript, slowScript], CancellationToken.None);
        }
    }

    private static async Task InstallScriptAsync(
        DockerFixture fixture,
        RemotePath path,
        string content,
        CancellationToken cancellationToken)
    {
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        var payload = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(payload, writable: false);
        await fileSystem.UploadAsync(
            stream,
            path,
            payload.Length,
            overwrite: false,
            cancellationToken: cancellationToken);
        await fileSystem.SetPermissionsAsync(
            path,
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
            "Docker fixture",
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
        var commandFactory = new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options);
        var fileSystemFactory = new SftpRemoteFileSystemFactory(secretStore, trust, prompt, options);
        return new DockerFixture(profile, commandFactory, fileSystemFactory);
    }

    private const string UsableDockerScript = """
        #!/bin/sh
        set -eu
        check_format() {
          last=''
          for arg in "$@"; do last="$arg"; done
          if [ "$last" != '{{json .}}' ]; then
            echo 'fixture: structured format token was not preserved' >&2
            exit 9
          fi
        }
        case "${1:-}" in
          --version)
            printf '%s\n' 'Docker version 27.5.1, build fixture'
            ;;
          version)
            check_format "$@"
            printf '%s\n' '{"Client":{"Version":"27.5.1","ApiVersion":"1.47","Os":"linux","Arch":"amd64"},"Server":{"Version":"27.5.1","ApiVersion":"1.47","Os":"linux","Arch":"amd64"}}'
            ;;
          info)
            check_format "$@"
            printf '%s\n' '{"Containers":1,"ContainersRunning":1,"ContainersPaused":0,"ContainersStopped":0,"Images":1,"Driver":"overlay2","DockerRootDir":"/var/lib/docker","ServerVersion":"27.5.1","OperatingSystem":"OpenSSH Docker fixture","OSType":"linux","Architecture":"x86_64","NCPU":2,"MemTotal":2147483648,"Name":"docker-fixture"}'
            ;;
          container)
            check_format "$@"
            printf '%s\n' '{"ID":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","Names":"fixture-api","Image":"example/api:fixture","State":"running","Status":"Up 1 minute","Ports":"8080/tcp","Mounts":"fixture-data","Networks":"fixture-net","CreatedAt":"2026-08-28 04:00:00 +0000 UTC","Size":"0B"}'
            ;;
          image)
            check_format "$@"
            printf '%s\n' '{"ID":"sha256:1111111111111111111111111111111111111111111111111111111111111111","Repository":"example/api","Tag":"fixture","Digest":"sha256:fixture","CreatedAt":"2026-08-28 03:00:00 +0000 UTC","Size":"100MB"}'
            ;;
          volume)
            check_format "$@"
            printf '%s\n' '{"Name":"fixture-data","Driver":"local","Scope":"local","Mountpoint":"/var/lib/docker/volumes/fixture-data/_data","Labels":"fixture=true"}'
            ;;
          network)
            check_format "$@"
            printf '%s\n' '{"ID":"net1111111111111111111111111111111111111111111111111111111111111","Name":"fixture-net","Driver":"bridge","Scope":"local","IPv6":"false","Internal":"false","Labels":"fixture=true"}'
            ;;
          *)
            echo 'fixture: unexpected Docker command' >&2
            exit 8
            ;;
        esac
        """;

    private const string DeniedDockerScript = """
        #!/bin/sh
        if [ "${1:-}" = '--version' ]; then
          printf '%s\n' 'Docker version 27.5.1, build fixture'
          exit 0
        fi
        echo 'permission denied while trying to connect to the Docker daemon socket at unix:///var/run/docker.sock' >&2
        exit 1
        """;

    private const string SlowDockerScript = """
        #!/bin/sh
        if [ "${1:-}" = '--version' ]; then
          printf '%s\n' 'Docker version 27.5.1, build fixture'
          exit 0
        fi
        if [ "${1:-}" = 'version' ]; then
          sleep 10
          printf '%s\n' '{"Client":{"Version":"27.5.1"},"Server":{"Version":"27.5.1","ApiVersion":"1.47","Os":"linux","Arch":"amd64"}}'
          exit 0
        fi
        exit 0
        """;

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
