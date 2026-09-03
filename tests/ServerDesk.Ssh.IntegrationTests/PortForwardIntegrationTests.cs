using System.Net;
using System.Net.Sockets;
using System.Text;
using ServerDesk.Application.Agent;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using ServerDesk.Infrastructure.Ssh.Agent;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class PortForwardIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_SSH_PORT") ?? "2222",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string Password = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly int HttpPort = int.Parse(
        Environment.GetEnvironmentVariable("SERVERDESK_FORWARD_HTTP_PORT") ?? "18080",
        System.Globalization.CultureInfo.InvariantCulture);
    private static readonly string Home = Environment.GetEnvironmentVariable("SERVERDESK_SFTP_HOME") ?? $"/home/{Username}";

    [Fact]
    public async Task LocalForwardReachesRemoteLoopbackServiceWithoutBlockingSftp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var profile = PortForwardProfile.Create(
            fixture.Profile.Id,
            "HTTP local",
            PortForwardKind.Local,
            "127.0.0.1",
            0,
            "127.0.0.1",
            HttpPort);
        await using var forward = fixture.ForwardFactory.Create(fixture.Profile, profile);

        await forward.StartAsync(cancellationToken);
        var response = await GetHttpAsync(forward.BoundPort, cancellationToken);

        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        var home = await fileSystem.StatAsync(RemotePath.Parse(Home), cancellationToken);

        Assert.Equal(PortForwardSessionState.Active, forward.State);
        Assert.InRange(forward.BoundPort, 1, 65535);
        Assert.Contains("serverdesk-forward-ok", response, StringComparison.Ordinal);
        Assert.Equal(RemoteFileKind.Directory, home.Kind);

        await forward.StopAsync(cancellationToken);
        Assert.Equal(PortForwardSessionState.Stopped, forward.State);
        Assert.Equal(0, forward.BoundPort);
    }

    [Fact]
    public async Task EphemeralAgentTunnelReachesRemoteLoopbackWithoutBlockingSftp()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var tunnelFactory = new SshAgentTunnelSessionFactory(fixture.ForwardFactory);
        await using var tunnel = tunnelFactory.Create(fixture.Profile, HttpPort);

        await tunnel.StartAsync(cancellationToken);
        var response = await GetHttpAsync(tunnel.LocalPort, cancellationToken);

        await using var fileSystem = fixture.FileSystemFactory.Create(fixture.Profile);
        await fileSystem.ConnectAsync(cancellationToken);
        var home = await fileSystem.StatAsync(RemotePath.Parse(Home), cancellationToken);

        Assert.Equal(AgentTunnelState.Active, tunnel.State);
        Assert.InRange(tunnel.LocalPort, 1, 65535);
        Assert.Contains("serverdesk-forward-ok", response, StringComparison.Ordinal);
        Assert.Equal(RemoteFileKind.Directory, home.Kind);

        await tunnel.StopAsync(cancellationToken);
        Assert.Equal(AgentTunnelState.Stopped, tunnel.State);
        Assert.Equal(0, tunnel.LocalPort);
    }

    [Fact]
    public async Task RemoteForwardAcceptsConnectionsOnSshServerLoopback()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var profile = PortForwardProfile.Create(
            fixture.Profile.Id,
            "HTTP remote",
            PortForwardKind.Remote,
            "127.0.0.1",
            0,
            "127.0.0.1",
            HttpPort);
        await using var forward = fixture.ForwardFactory.Create(fixture.Profile, profile);

        await forward.StartAsync(cancellationToken);
        var response = await GetHttpAsync(forward.BoundPort, cancellationToken);

        Assert.Equal(PortForwardSessionState.Active, forward.State);
        Assert.Contains("serverdesk-forward-ok", response, StringComparison.Ordinal);

        await forward.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task DynamicForwardProvidesWorkingSocks5Tunnel()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture();
        var profile = PortForwardProfile.Create(
            fixture.Profile.Id,
            "SOCKS",
            PortForwardKind.Dynamic,
            "127.0.0.1",
            0);
        await using var forward = fixture.ForwardFactory.Create(fixture.Profile, profile);

        await forward.StartAsync(cancellationToken);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, forward.BoundPort, cancellationToken);
        await using var stream = client.GetStream();

        await stream.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }, cancellationToken);
        var greeting = new byte[2];
        await stream.ReadExactlyAsync(greeting, cancellationToken);
        Assert.Equal(new byte[] { 0x05, 0x00 }, greeting);

        var request = new byte[]
        {
            0x05, 0x01, 0x00, 0x01,
            127, 0, 0, 1,
            checked((byte)(HttpPort >> 8)), checked((byte)(HttpPort & 0xff)),
        };
        await stream.WriteAsync(request, cancellationToken);
        var responseHeader = new byte[4];
        await stream.ReadExactlyAsync(responseHeader, cancellationToken);
        Assert.Equal(0x05, responseHeader[0]);
        Assert.Equal(0x00, responseHeader[1]);
        await ConsumeSocksAddressAsync(stream, responseHeader[3], cancellationToken);

        var httpRequest = Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(httpRequest, cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var httpResponse = await reader.ReadToEndAsync(cancellationToken);

        Assert.Contains("serverdesk-forward-ok", httpResponse, StringComparison.Ordinal);
        await forward.StopAsync(cancellationToken);
    }

    [Fact]
    public async Task LocalPortCollisionMapsToPortInUse()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var occupiedPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        var fixture = CreateFixture();
        var profile = PortForwardProfile.Create(
            fixture.Profile.Id,
            "Collision",
            PortForwardKind.Local,
            "127.0.0.1",
            occupiedPort,
            "127.0.0.1",
            HttpPort);
        await using var forward = fixture.ForwardFactory.Create(fixture.Profile, profile);

        var exception = await Assert.ThrowsAsync<PortForwardSessionException>(() =>
            forward.StartAsync(cancellationToken).AsTask());

        Assert.Equal(RemoteErrorCode.PortInUse, exception.Error.Code);
        Assert.Equal(PortForwardSessionState.Faulted, forward.State);
    }

    private static async Task<string> GetHttpAsync(int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, cancellationToken);
        await using var stream = client.GetStream();
        var request = Encoding.ASCII.GetBytes("GET / HTTP/1.0\r\nHost: localhost\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request, cancellationToken);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    private static async Task ConsumeSocksAddressAsync(
        NetworkStream stream,
        byte addressType,
        CancellationToken cancellationToken)
    {
        switch (addressType)
        {
            case 0x01:
                await ReadAndDiscardAsync(stream, 4 + 2, cancellationToken);
                break;
            case 0x04:
                await ReadAndDiscardAsync(stream, 16 + 2, cancellationToken);
                break;
            case 0x03:
                var length = new byte[1];
                await stream.ReadExactlyAsync(length, cancellationToken);
                await ReadAndDiscardAsync(stream, length[0] + 2, cancellationToken);
                break;
            default:
                throw new InvalidOperationException($"Unexpected SOCKS5 address type {addressType}.");
        }
    }

    private static async Task ReadAndDiscardAsync(
        NetworkStream stream,
        int length,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer, cancellationToken);
    }

    private static ForwardFixture CreateFixture()
    {
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Forward fixture",
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
        return new ForwardFixture(
            profile,
            new SshPortForwardSessionFactory(secretStore, hostTrust, interactivePrompt, options),
            new SftpRemoteFileSystemFactory(secretStore, hostTrust, interactivePrompt, options));
    }

    private sealed record ForwardFixture(
        ServerProfile Profile,
        IPortForwardSessionFactory ForwardFactory,
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
            throw new InvalidOperationException("Password forwarding fixture must not request interactive authentication.");
    }
}
