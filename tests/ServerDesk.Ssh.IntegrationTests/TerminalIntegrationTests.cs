using System.Collections.Concurrent;
using System.Text;
using ServerDesk.Application.HostTrust;
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

public sealed class TerminalIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task PtySupportsBidirectionalIoAndPreservesAnsiSequences()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var terminal = CreateTerminal();
        var output = new TerminalOutputProbe();
        terminal.OutputReceived += output.Append;

        await terminal.ConnectAsync(new TerminalSize(100, 30), cancellationToken);
        await terminal.SendAsync(
            "printf '\\033[31mSD_RED\\033[0m\\n'; printf '__SD_ANSI_DONE__\\n'\n",
            cancellationToken);

        var captured = await output.WaitForAsync("__SD_ANSI_DONE__", cancellationToken);

        Assert.Equal(TerminalSessionState.Connected, terminal.State);
        Assert.Contains("\u001b[31mSD_RED\u001b[0m", captured, StringComparison.Ordinal);

        await terminal.DisconnectAsync(cancellationToken);
        Assert.Equal(TerminalSessionState.Disconnected, terminal.State);
    }

    [Fact]
    public async Task ResizePropagatesToRemotePty()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var terminal = CreateTerminal();
        var output = new TerminalOutputProbe();
        terminal.OutputReceived += output.Append;

        await terminal.ConnectAsync(new TerminalSize(80, 24), cancellationToken);
        await terminal.ResizeAsync(new TerminalSize(132, 43, 1320, 860), cancellationToken);
        await terminal.SendAsync("stty size; printf '__SD_SIZE_DONE__\\n'\n", cancellationToken);

        var captured = await output.WaitForAsync("__SD_SIZE_DONE__", cancellationToken);

        Assert.Contains("43 132", NormalizeTerminalText(captured), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BusyPtyDoesNotBlockIndependentSftpChannel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        await using var terminal = fixture.TerminalFactory.Create(fixture.Profile);
        var output = new TerminalOutputProbe();
        terminal.OutputReceived += output.Append;
        await terminal.ConnectAsync(TerminalSize.Default, cancellationToken);

        await terminal.SendAsync("sleep 10; printf '__SD_SLEEP_DONE__\\n'\n", cancellationToken);

        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        var home = await fileSystem.StatAsync(RemotePath.Parse(Home), cancellationToken);

        Assert.Equal(RemoteFileKind.Directory, home.Kind);
        Assert.DoesNotContain("__SD_SLEEP_DONE__", output.Text, StringComparison.Ordinal);

        await terminal.SendAsync("\u0003", cancellationToken);
    }

    [Fact]
    public async Task DisposeClosesTerminalSessionAndStopsFurtherWrites()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var terminal = CreateTerminal();
        await terminal.ConnectAsync(TerminalSize.Default, cancellationToken);

        await terminal.DisposeAsync();

        Assert.Equal(TerminalSessionState.Disconnected, terminal.State);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            terminal.SendAsync("echo should-not-run\n", cancellationToken).AsTask());
    }

    private static IRemoteTerminalSession CreateTerminal()
    {
        var fixture = CreateFixture();
        return fixture.TerminalFactory.Create(fixture.Profile);
    }

    private static TerminalFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "PTY fixture",
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
        return new TerminalFixture(
            profile,
            new SshRemoteTerminalSessionFactory(secretStore, hostTrust, interactivePrompt, options),
            new SftpRemoteFileSystemFactory(secretStore, hostTrust, interactivePrompt, options));
    }

    private static string NormalizeTerminalText(string value) => value.Replace("\r", string.Empty, StringComparison.Ordinal);

    private sealed record TerminalFixture(
        ServerProfile Profile,
        IRemoteTerminalSessionFactory TerminalFactory,
        IRemoteFileSystemFactory FileSystemFactory);

    private sealed class TerminalOutputProbe
    {
        private readonly ConcurrentQueue<string> _chunks = new();
        private readonly SemaphoreSlim _signal = new(0);

        public string Text => string.Concat(_chunks);

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
            throw new InvalidOperationException("Password PTY fixture must not request interactive authentication.");
    }
}
