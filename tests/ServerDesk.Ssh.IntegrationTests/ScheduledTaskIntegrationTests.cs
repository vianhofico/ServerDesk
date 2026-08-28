using System.Globalization;
using System.Text;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.ScheduledTasks;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class ScheduledTaskIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222", CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task CronAndTimerStateCrossOpenSshAndSftpWithVerification()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var token = Guid.NewGuid().ToString("N");
        var scriptPath = RemotePath.Parse($"{Home}/serverdesk-tasks-{token}");
        await InstallFixtureAsync(fixture, scriptPath, cancellationToken);

        try
        {
            var service = new ScheduledTaskService(
                new ExecutableRewriteFactory(fixture.CommandFactory, scriptPath.Value),
                fixture.FileSystemFactory,
                ScheduledTaskOptions.Default);

            var initial = await service.InspectAsync(fixture.Profile, cancellationToken);
            Assert.True(initial.IsSuccess, initial.Error?.Message);
            Assert.True(initial.Snapshot!.CronAvailable);
            Assert.True(initial.Snapshot.SystemdAvailable);
            var timer = Assert.Single(initial.Snapshot.Tasks, task => task.Kind == ScheduledTaskKind.SystemdTimer);
            Assert.False(timer.Enabled);

            const string candidate = "MAILTO=ops@example.invalid\n30 2 * * * /srv/new-job\n@reboot /srv/bootstrap\n";
            var applied = await service.ApplyRawCrontabAsync(
                fixture.Profile,
                candidate,
                initial.Snapshot.RawCrontab,
                cancellationToken);
            Assert.True(applied.IsSuccess, applied.Error?.Message);
            Assert.Equal(candidate, applied.VerifiedSnapshot!.RawCrontab);

            var enabled = await service.SetEnabledAsync(
                fixture.Profile,
                timer,
                true,
                applied.VerifiedSnapshot.RawCrontab,
                cancellationToken);
            Assert.True(enabled.IsSuccess, enabled.Error?.Message);
            var enabledTimer = Assert.Single(enabled.VerifiedSnapshot!.Tasks, task => task.Kind == ScheduledTaskKind.SystemdTimer);
            Assert.True(enabledTimer.Enabled);
            Assert.True(enabledTimer.Active);

            var history = await service.ReadHistoryAsync(fixture.Profile, enabledTimer, cancellationToken);
            Assert.True(history.IsSuccess, history.Error?.Message);
            Assert.Contains("completed", history.Text, StringComparison.Ordinal);

            var source = await service.ReadRawSourceAsync(fixture.Profile, enabledTimer, cancellationToken);
            Assert.True(source.IsSuccess, source.Error?.Message);
            Assert.Contains("OnCalendar", source.Text, StringComparison.Ordinal);

            var disabled = await service.SetEnabledAsync(
                fixture.Profile,
                enabledTimer,
                false,
                enabled.VerifiedSnapshot.RawCrontab,
                cancellationToken);
            Assert.True(disabled.IsSuccess, disabled.Error?.Message);
            var disabledTimer = Assert.Single(disabled.VerifiedSnapshot!.Tasks, task => task.Kind == ScheduledTaskKind.SystemdTimer);
            Assert.False(disabledTimer.Enabled);
            Assert.False(disabledTimer.Active);
        }
        finally
        {
            await CleanupAsync(
                fixture,
                [scriptPath, RemotePath.Parse(scriptPath.Value + ".cron"), RemotePath.Parse(scriptPath.Value + ".timer")],
                CancellationToken.None);
        }
    }

    private static async Task InstallFixtureAsync(TaskFixture fixture, RemotePath path, CancellationToken cancellationToken)
    {
        var content = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "scheduled-tasks-fixture.sh"), cancellationToken);
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        var bytes = Encoding.UTF8.GetBytes(content);
        await using var stream = new MemoryStream(bytes, writable: false);
        await fileSystem.UploadAsync(stream, path, bytes.Length, overwrite: false, cancellationToken: cancellationToken);
        await fileSystem.SetPermissionsAsync(path, RemoteUnixPermissions.FromMode(700), cancellationToken);
    }

    private static async Task CleanupAsync(TaskFixture fixture, IReadOnlyList<RemotePath> paths, CancellationToken cancellationToken)
    {
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        try
        {
            await fileSystem.ConnectAsync(cancellationToken);
            foreach (var path in paths)
            {
                try { await fileSystem.DeleteFileAsync(path, cancellationToken); } catch { }
            }
        }
        catch { }
    }

    private static TaskFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(profileId, "Scheduled tasks fixture", Host, Port, Username, credentialReference: reference, authenticationKind: ServerAuthenticationKind.Password);
        var secretStore = new MemorySecretStore(reference, Password);
        var trust = new TrustOnceHostTrustService();
        var prompt = new RejectInteractivePrompt();
        var options = new SshSessionOptions(TimeSpan.FromSeconds(8), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(250));
        return new TaskFixture(profile, new SshRemoteCommandExecutorFactory(secretStore, trust, prompt, options), new SftpRemoteFileSystemFactory(secretStore, trust, prompt, options));
    }

    private sealed record TaskFixture(ServerProfile Profile, IRemoteCommandExecutorFactory CommandFactory, IRemoteFileSystemFactory FileSystemFactory);

    private sealed class ExecutableRewriteFactory : IRemoteCommandExecutorFactory
    {
        private readonly IRemoteCommandExecutorFactory _inner;
        private readonly string _fixtureExecutable;
        public ExecutableRewriteFactory(IRemoteCommandExecutorFactory inner, string fixtureExecutable) { _inner = inner; _fixtureExecutable = fixtureExecutable; }
        public IRemoteCommandExecutor Create(ServerProfile profile) => new ExecutableRewriteExecutor(_inner.Create(profile), _fixtureExecutable);
    }

    private sealed class ExecutableRewriteExecutor : IRemoteCommandExecutor
    {
        private readonly IRemoteCommandExecutor _inner;
        private readonly string _fixtureExecutable;
        public ExecutableRewriteExecutor(IRemoteCommandExecutor inner, string fixtureExecutable) { _inner = inner; _fixtureExecutable = fixtureExecutable; }
        public Guid ServerProfileId => _inner.ServerProfileId;
        public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default)
        {
            if (command.Executable is not ("crontab" or "systemctl" or "journalctl")) return _inner.ExecuteAsync(command, cancellationToken);
            var arguments = new List<string>(command.Arguments.Count + 1) { command.Executable };
            arguments.AddRange(command.Arguments);
            return _inner.ExecuteAsync(command with { Executable = _fixtureExecutable, Arguments = arguments }, cancellationToken);
        }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly SecretReference _reference;
        private readonly string _secret;
        public MemorySecretStore(SecretReference reference, string secret) { _reference = reference; _secret = secret; }
        public ValueTask SetAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<string?> GetAsync(SecretReference reference, CancellationToken cancellationToken = default) { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<string?>(reference == _reference ? _secret : null); }
        public ValueTask DeleteAsync(SecretReference reference, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class TrustOnceHostTrustService : IHostTrustService
    {
        public ValueTask<HostTrustVerification> VerifyAsync(HostKeyObservation observation, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(new HostTrustVerification(HostTrustOutcome.TrustedOnce, observation, [])); }
    }

    private sealed class RejectInteractivePrompt : IInteractiveAuthenticationPrompt
    {
        public ValueTask<IReadOnlyList<string>?> PromptAsync(InteractiveAuthenticationChallenge challenge, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Password fixture must not request keyboard-interactive authentication.");
    }
}
