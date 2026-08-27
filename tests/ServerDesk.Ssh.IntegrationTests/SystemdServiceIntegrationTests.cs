using System.Globalization;
using System.Text;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Services;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class SystemdServiceIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task FixtureListsDetailsAndVerifiesLifecycleActionsThroughSsh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var token = Guid.NewGuid().ToString("N");
        var script = RemotePath.Parse($"{Home}/serverdesk-systemctl-{token}");
        var active = RemotePath.Parse($"{Home}/.serverdesk-systemctl-{token}.active");
        var enabled = RemotePath.Parse($"{Home}/.serverdesk-systemctl-{token}.enabled");
        await InstallFixtureAsync(fixture, script, active, enabled, cancellationToken);
        var manager = new SystemdServiceManager(
            fixture.CommandFactory,
            new SystemdServiceOptions(script.Value, UseSudoForMutations: false));

        try
        {
            var listed = await manager.ListAsync(fixture.Profile, cancellationToken);
            Assert.True(listed.IsSuccess, listed.Error?.Message);
            var row = Assert.Single(listed.Services);
            Assert.Equal("serverdesk-fixture.service", row.Unit);
            Assert.Equal("inactive", row.ActiveState);
            Assert.Equal("disabled", row.EnabledState);

            var details = await manager.GetAsync(fixture.Profile, row.Unit, cancellationToken);
            Assert.True(details.IsSuccess, details.Error?.Message);
            Assert.Equal("dead", Assert.Single(details.Services).SubState);

            var started = await manager.ExecuteAsync(
                fixture.Profile,
                row.Unit,
                ServerServiceAction.Start,
                cancellationToken);
            Assert.True(started.IsSuccess, started.Message);
            Assert.Equal("active", started.VerifiedService?.ActiveState);

            var enabledResult = await manager.ExecuteAsync(
                fixture.Profile,
                row.Unit,
                ServerServiceAction.Enable,
                cancellationToken);
            Assert.True(enabledResult.IsSuccess, enabledResult.Message);
            Assert.Equal("enabled", enabledResult.VerifiedService?.EnabledState);

            var stopped = await manager.ExecuteAsync(
                fixture.Profile,
                row.Unit,
                ServerServiceAction.Stop,
                cancellationToken);
            Assert.True(stopped.IsSuccess, stopped.Message);
            Assert.Equal("inactive", stopped.VerifiedService?.ActiveState);
        }
        finally
        {
            await CleanupAsync(fixture, [script, active, enabled], CancellationToken.None);
        }
    }

    private static async Task InstallFixtureAsync(
        ServiceFixture fixture,
        RemotePath script,
        RemotePath active,
        RemotePath enabled,
        CancellationToken cancellationToken)
    {
        var content = $$"""
            #!/bin/sh
            ACTIVE='{{active.Value}}'
            ENABLED='{{enabled.Value}}'
            UNIT='serverdesk-fixture.service'
            action="$1"
            case "$action" in
              list-units)
                if [ -f "$ACTIVE" ]; then state='active'; sub='running'; else state='inactive'; sub='dead'; fi
                printf '%s loaded %s %s ServerDesk Fixture Service\n' "$UNIT" "$state" "$sub"
                ;;
              list-unit-files)
                if [ -f "$ENABLED" ]; then enabled_state='enabled'; else enabled_state='disabled'; fi
                printf '%s %s enabled\n' "$UNIT" "$enabled_state"
                ;;
              show)
                if [ -f "$ACTIVE" ]; then state='active'; sub='running'; pid='4242'; status='Fixture running'; else state='inactive'; sub='dead'; pid='0'; status='Fixture stopped'; fi
                if [ -f "$ENABLED" ]; then enabled_state='enabled'; else enabled_state='disabled'; fi
                printf 'Id=%s\nDescription=ServerDesk Fixture Service\nLoadState=loaded\nActiveState=%s\nSubState=%s\nUnitFileState=%s\nMainPID=%s\nStatusText=%s\n' "$UNIT" "$state" "$sub" "$enabled_state" "$pid" "$status"
                ;;
              start|restart)
                : > "$ACTIVE"
                ;;
              stop)
                rm -f -- "$ACTIVE"
                ;;
              reload)
                ;;
              enable)
                : > "$ENABLED"
                ;;
              disable)
                rm -f -- "$ENABLED"
                ;;
              *)
                echo 'unsupported fixture action' >&2
                exit 2
                ;;
            esac
            """;

        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        var payload = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(payload, writable: false);
        await fileSystem.UploadAsync(
            stream,
            script,
            payload.Length,
            overwrite: false,
            cancellationToken: cancellationToken);
        await fileSystem.SetPermissionsAsync(
            script,
            RemoteUnixPermissions.FromMode(700),
            cancellationToken);
    }

    private static async Task CleanupAsync(
        ServiceFixture fixture,
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

    private static ServiceFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Systemd fixture",
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
        return new ServiceFixture(profile, commandFactory, fileSystemFactory);
    }

    private sealed record ServiceFixture(
        ServerProfile Profile,
        IRemoteCommandExecutorFactory CommandFactory,
        IRemoteFileSystemFactory FileSystemFactory);

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
