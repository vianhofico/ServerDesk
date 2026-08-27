using System.Collections.Concurrent;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Application.Terminal;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class ChannelConcurrencyCertificationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task BusyPtyDoesNotBlockSftpOrDashboardCommandChannel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        await using var terminal = fixture.TerminalFactory.Create(fixture.Profile);
        var output = new TerminalOutputProbe();
        terminal.OutputReceived += output.Append;
        await terminal.ConnectAsync(TerminalSize.Default, cancellationToken);

        await terminal.SendAsync(
            "printf '\\137\\137SD_BUSY_PTY\\137\\137\\n'; sleep 10\n",
            cancellationToken);
        await output.WaitForAsync("__SD_BUSY_PTY__", cancellationToken);

        using var independentTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        independentTimeout.CancelAfter(TimeSpan.FromSeconds(6));

        var sftpTask = VerifySftpAsync(fixture, independentTimeout.Token);
        var commandTask = VerifyCommandAsync(fixture, independentTimeout.Token);
        await Task.WhenAll(sftpTask, commandTask);

        Assert.Equal(RemoteFileKind.Directory, sftpTask.Result.Kind);
        Assert.True(commandTask.Result.IsSuccess);
        Assert.Equal(0, commandTask.Result.Command!.ExitCode);
        Assert.Contains("Linux", commandTask.Result.Command.StandardOutput, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(TerminalSessionState.Connected, terminal.State);

        await terminal.SendAsync("\u0003", cancellationToken);
    }

    private static async Task<RemoteFileEntry> VerifySftpAsync(
        ConcurrencyFixture fixture,
        CancellationToken cancellationToken)
    {
        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        return await fileSystem.StatAsync(RemotePath.Parse(Home), cancellationToken);
    }

    private static async Task<RemoteExecutionResult> VerifyCommandAsync(
        ConcurrencyFixture fixture,
        CancellationToken cancellationToken)
    {
        await using var executor = fixture.CommandFactory.Create(fixture.Profile);
        return await executor.ExecuteAsync(
            RemoteCommandSpec.ReadOnly("uname", "-s"),
            cancellationToken);
    }

    private static ConcurrencyFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Concurrency fixture",
            Host,
            Port,
            Username,
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var secretStore = new MemorySecretStore(reference, Password);
        var hostTrust = new TrustOnceHostTrustService();
        var interactivePrompt = new RejectInteractivePrompt();
        var options = new SshSessionOptions(
            TimeSpan.FromSeconds(8),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));

        return new ConcurrencyFixture(
            profile,
            new SshRemoteTerminalSessionFactory(secretStore, hostTrust, interactivePrompt, options),
            new SftpRemoteFileSystemFactory(secretStore, hostTrust, interactivePrompt, options),
            new SshRemoteCommandExecutorFactory(secretStore, hostTrust, interactivePrompt, options));
    }

    private sealed record ConcurrencyFixture(
        ServerProfile Profile,
        IRemoteTerminalSessionFactory TerminalFactory,
        IRemoteFileSystemFactory FileSystemFactory,
        IRemoteCommandExecutorFactory CommandFactory);

    private sealed class TerminalOutputProbe
    {
        private readonly ConcurrentQueue<string> _chunks = new();
        private readonly SemaphoreSlim _signal = new(0);

        private string Text => string.Concat(_chunks);

        public void Append(string chunk)
        {
            _chunks.Enqueue(chunk);
            _signal.Release();
        }

        public async Task<string> WaitForAsync(string marker, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            while (true)
            {
                var text = Text;
                if (text.Contains(marker, StringComparison.Ordinal))
                {
                    return text;
                }

                await _signal.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
        }
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

        public ValueTask SetAsync(
            SecretReference reference,
            string secret,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(reference == _reference ? _secret : null);
        }

        public ValueTask DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default) =>
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
            throw new InvalidOperationException("Password concurrency fixture must not request interactive authentication.");
    }
}
