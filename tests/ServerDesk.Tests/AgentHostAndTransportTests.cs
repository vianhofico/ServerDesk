using System.Net;
using Grpc.Core;
using ServerDesk.Agent;
using ServerDesk.Agent.Contracts.V1;
using ServerDesk.Application.Agent;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh.Agent;
using Xunit;
using ApplicationCapability = ServerDesk.Application.Agent.AgentCapability;
using WireCapability = ServerDesk.Agent.Contracts.V1.AgentCapability;

namespace ServerDesk.Tests;

public sealed class AgentHostAndTransportTests
{
    [Fact]
    public void ListenerConfigurationIsStructurallyLoopbackOnly()
    {
        var options = AgentListenerOptions.FromPort(41371);
        var endpoint = options.CreateEndpoint();

        Assert.Equal(IPAddress.Loopback, endpoint.Address);
        Assert.True(IPAddress.IsLoopback(endpoint.Address));
        Assert.Equal(41371, endpoint.Port);
        var property = Assert.Single(typeof(AgentListenerOptions).GetProperties());
        Assert.Equal(nameof(AgentListenerOptions.Port), property.Name);
    }

    [Fact]
    public void ListenerRejectsInvalidPorts()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentListenerOptions.FromPort(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => AgentListenerOptions.FromPort(65536));
    }

    [Fact]
    public async Task AgentControlAdvertisesNoUnimplementedStreamingCapabilities()
    {
        var runtime = new AgentRuntimeInfo("1.0.0-test", "linux", "x64", DateTimeOffset.UnixEpoch);
        var service = new AgentControlService(runtime);
        var request = new NegotiateRequest
        {
            Protocol = new ProtocolVersion { Major = 1, Minor = 0 },
            ClientVersion = "1.0.0-test",
        };
        for (var value = 1; value <= 5; value++)
        {
            request.RequestedCapabilities.Add((WireCapability)value);
        }

        var response = await service.Negotiate(request, null!);
        var health = await service.Health(new HealthRequest(), null!);

        Assert.Empty(response.Capabilities);
        Assert.Equal((uint)1, response.Protocol.Major);
        Assert.Equal((uint)0, response.Protocol.Minor);
        Assert.Equal(1, (int)health.State);
    }

    [Fact]
    public void GrpcPeerMappingIgnoresUnknownCapabilities()
    {
        var response = new NegotiateResponse
        {
            Protocol = new ProtocolVersion { Major = 1, Minor = 0 },
            AgentVersion = "1.0.0-test",
            Platform = "linux",
            Architecture = "x64",
        };
        response.Capabilities.Add((WireCapability)1);
        response.Capabilities.Add((WireCapability)999);

        var peer = GrpcAgentTransportClient.MapPeer(response);

        var capability = Assert.Single(peer.Capabilities);
        Assert.Equal(ApplicationCapability.MetricsStreaming, capability);
    }

    [Fact]
    public void GrpcUnavailableIsMappedToSanitizedDisconnectedState()
    {
        var rpcException = new RpcException(new Status(StatusCode.Unavailable, "sensitive remote diagnostic"));

        var mapped = GrpcAgentTransportClient.MapRpcException(rpcException);

        Assert.Equal(AgentConnectionState.Disconnected, mapped.State);
        Assert.DoesNotContain("sensitive", mapped.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProbeServiceReturnsExplicitDisconnectedNegotiationState()
    {
        var service = new AgentConnectionProbeService();
        await using var transport = new ThrowingTransportClient(AgentConnectionState.Disconnected);

        var result = await service.NegotiateAsync(transport, TestContext.Current.CancellationToken);

        Assert.Equal(AgentConnectionState.Disconnected, result.State);
        Assert.Empty(result.Capabilities);
    }

    [Fact]
    public async Task AgentTunnelFactoryCreatesOnlyEphemeralLoopbackLocalForward()
    {
        var profile = ServerProfile.Create(
            Guid.NewGuid(),
            "Agent tunnel fixture",
            "example.test",
            22,
            "serverdesk",
            credentialReference: null,
            authenticationKind: ServerAuthenticationKind.Password);
        var forwardFactory = new CapturingForwardFactory();
        var tunnelFactory = new SshAgentTunnelSessionFactory(forwardFactory);
        await using var tunnel = tunnelFactory.Create(profile, 41371);

        var captured = Assert.IsType<PortForwardProfile>(forwardFactory.CapturedProfile);
        Assert.Equal(PortForwardKind.Local, captured.Kind);
        Assert.Equal("127.0.0.1", captured.BindHost);
        Assert.Equal(0, captured.BindPort);
        Assert.Equal("127.0.0.1", captured.DestinationHost);
        Assert.Equal(41371, captured.DestinationPort);

        await tunnel.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentTunnelState.Active, tunnel.State);
        Assert.Equal(61234, tunnel.LocalPort);

        await tunnel.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentTunnelState.Stopped, tunnel.State);
        Assert.Equal(0, tunnel.LocalPort);
    }

    private sealed class ThrowingTransportClient : IAgentTransportClient
    {
        private readonly AgentConnectionState _state;

        public ThrowingTransportClient(AgentConnectionState state)
        {
            _state = state;
        }

        public ValueTask<AgentPeerInfo> NegotiateAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromException<AgentPeerInfo>(new AgentTransportException(_state, "Agent transport is unavailable."));

        public ValueTask<AgentHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AgentHealthSnapshot(_state, DateTimeOffset.UtcNow));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingForwardFactory : IPortForwardSessionFactory
    {
        public PortForwardProfile? CapturedProfile { get; private set; }

        public IPortForwardSession Create(ServerProfile serverProfile, PortForwardProfile forwardProfile)
        {
            CapturedProfile = forwardProfile;
            return new FakeForwardSession(forwardProfile.Id);
        }
    }

    private sealed class FakeForwardSession : IPortForwardSession
    {
        public FakeForwardSession(Guid id)
        {
            ForwardProfileId = id;
        }

        public Guid ForwardProfileId { get; }

        public PortForwardSessionState State { get; private set; } = PortForwardSessionState.Created;

        public int BoundPort { get; private set; }

        public ServerDesk.Domain.Errors.RemoteError? LastError => null;

        public event Action<PortForwardSessionState>? StateChanged
        {
            add { }
            remove { }
        }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = PortForwardSessionState.Active;
            BoundPort = 61234;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = PortForwardSessionState.Stopped;
            BoundPort = 0;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = PortForwardSessionState.Stopped;
            BoundPort = 0;
            return ValueTask.CompletedTask;
        }
    }
}
