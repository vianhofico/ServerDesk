using System.Globalization;
using System.Text;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Logs;
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

public sealed class ServerLogIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";
    private static readonly string DeniedDirectory = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_DENIED_PATH") ?? "/tmp/serverdesk-sftp-denied";

    [Fact]
    public async Task FileTailJournalCursorCancellationAndChannelIsolationWorkThroughSsh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var token = Guid.NewGuid().ToString("N");
        var journalScript = RemotePath.Parse($"{Home}/serverdesk-journalctl-{token}");
        var fileLog = RemotePath.Parse($"{Home}/serverdesk-log-{token}.log");
        await InstallFixtureAsync(fixture, journalScript, fileLog, cancellationToken);
        var service = new ServerLogService(
            fixture.CommandFactory,
            ServerLogOptions.Default with
            {
                JournalExecutable = journalScript.Value,
                FollowPollInterval = TimeSpan.FromMilliseconds(50),
                CommandTimeout = TimeSpan.FromSeconds(15),
            });

        try
        {
            var fileResult = await service.ReadFileTailAsync(
                fixture.Profile,
                fileLog.Value,
                2,
                cancellationToken);
            Assert.True(fileResult.IsSuccess, fileResult.Error?.Message);
            Assert.Equal(["second", "third"], fileResult.Entries.Select(entry => entry.Message));

            var missing = await service.ReadFileTailAsync(
                fixture.Profile,
                $"{Home}/serverdesk-missing-{token}.log",
                10,
                cancellationToken);
            Assert.Equal(RemoteErrorCode.PathNotFound, missing.Error?.Code);

            var denied = await service.ReadFileTailAsync(
                fixture.Profile,
                $"{DeniedDirectory}/secret.log",
                10,
                cancellationToken);
            Assert.Equal(RemoteErrorCode.PermissionDenied, denied.Error?.Code);

            var recent = await service.ReadJournalAsync(
                fixture.Profile,
                20,
                "serverdesk-fixture.service",
                cancellationToken);
            Assert.True(recent.IsSuccess, recent.Error?.Message);
            Assert.Equal(2, recent.Entries.Count);
            Assert.Equal("cursor-2", recent.LastCursor);

            var incremental = await service.ReadJournalAfterCursorAsync(
                fixture.Profile,
                recent.LastCursor!,
                20,
                "serverdesk-fixture.service",
                cancellationToken);
            Assert.True(incremental.IsSuccess, incremental.Error?.Message);
            var next = Assert.Single(incremental.Entries);
            Assert.Equal("cursor-3", next.Cursor);
            Assert.Equal("incremental event", next.Message);

            using var slowCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var slowRead = service.ReadJournalAfterCursorAsync(
                fixture.Profile,
                "slow",
                20,
                "serverdesk-fixture.service",
                slowCancellation.Token);
            await Task.Delay(150, cancellationToken);

            await using var independentExecutor = fixture.CommandFactory.Create(fixture.Profile);
            var independent = await independentExecutor.ExecuteAsync(
                RemoteCommandSpec.ReadOnly("printf", "channel-ok"),
                cancellationToken);
            Assert.True(independent.IsSuccess, independent.Error?.Message);
            Assert.Equal("channel-ok", independent.Command?.StandardOutput);

            slowCancellation.Cancel();
            var cancelled = await slowRead;
            Assert.Equal(RemoteErrorCode.OperationCancelled, cancelled.Error?.Code);
        }
        finally
        {
            await CleanupAsync(fixture, [journalScript, fileLog], CancellationToken.None);
        }
    }

    private static async Task InstallFixtureAsync(
        LogFixture fixture,
        RemotePath journalScript,
        RemotePath fileLog,
        CancellationToken cancellationToken)
    {
        const string scriptContent = """
            #!/bin/sh
            after=''
            unit=''
            while [ "$#" -gt 0 ]; do
              case "$1" in
                --after-cursor)
                  after="$2"
                  shift 2
                  ;;
                --unit)
                  unit="$2"
                  shift 2
                  ;;
                --lines)
                  shift 2
                  ;;
                --no-pager|--output=json|--utc)
                  shift
                  ;;
                *)
                  shift
                  ;;
              esac
            done
            if [ -n "$unit" ] && [ "$unit" != 'serverdesk-fixture.service' ]; then
              exit 0
            fi
            case "$after" in
              '')
                printf '%s\n' \
                  '{"__REALTIME_TIMESTAMP":"1763000000123456","__CURSOR":"cursor-1","PRIORITY":"6","MESSAGE":"recent event","SYSLOG_IDENTIFIER":"fixture","_SYSTEMD_UNIT":"serverdesk-fixture.service","_PID":"7001","_HOSTNAME":"openssh-fixture"}' \
                  '{"__REALTIME_TIMESTAMP":"1763000001123456","__CURSOR":"cursor-2","PRIORITY":"4","MESSAGE":"recent warning","SYSLOG_IDENTIFIER":"fixture","_SYSTEMD_UNIT":"serverdesk-fixture.service","_PID":"7001","_HOSTNAME":"openssh-fixture"}'
                ;;
              'cursor-2')
                printf '%s\n' \
                  '{"__REALTIME_TIMESTAMP":"1763000002123456","__CURSOR":"cursor-3","PRIORITY":"3","MESSAGE":"incremental event","SYSLOG_IDENTIFIER":"fixture","_SYSTEMD_UNIT":"serverdesk-fixture.service","_PID":"7001","_HOSTNAME":"openssh-fixture"}'
                ;;
              'slow')
                sleep 10
                printf '%s\n' \
                  '{"__REALTIME_TIMESTAMP":"1763000003123456","__CURSOR":"cursor-slow","PRIORITY":"6","MESSAGE":"slow event","SYSLOG_IDENTIFIER":"fixture","_SYSTEMD_UNIT":"serverdesk-fixture.service","_PID":"7001","_HOSTNAME":"openssh-fixture"}'
                ;;
            esac
            """;

        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        await UploadAsync(fileSystem, journalScript, scriptContent, cancellationToken);
        await fileSystem.SetPermissionsAsync(
            journalScript,
            RemoteUnixPermissions.FromMode(700),
            cancellationToken);
        await UploadAsync(fileSystem, fileLog, "first\nsecond\nthird\n", cancellationToken);
    }

    private static async Task UploadAsync(
        IRemoteFileSystem fileSystem,
        RemotePath path,
        string content,
        CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(payload, writable: false);
        await fileSystem.UploadAsync(
            stream,
            path,
            payload.Length,
            overwrite: false,
            cancellationToken: cancellationToken);
    }

    private static async Task CleanupAsync(
        LogFixture fixture,
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

    private static LogFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Logs fixture",
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
        return new LogFixture(profile, commandFactory, fileSystemFactory);
    }

    private sealed record LogFixture(
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
