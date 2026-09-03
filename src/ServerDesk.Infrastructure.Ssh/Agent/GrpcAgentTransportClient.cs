using Grpc.Core;
using Grpc.Net.Client;
using ServerDesk.Agent.Contracts.V1;
using ServerDesk.Application.Agent;
using ApplicationCapability = ServerDesk.Application.Agent.AgentCapability;
using WireCapability = ServerDesk.Agent.Contracts.V1.AgentCapability;

namespace ServerDesk.Infrastructure.Ssh.Agent;

public sealed class GrpcAgentTransportClient : IAgentTransportClient
{
    private readonly GrpcChannel _channel;
    private readonly AgentControl.AgentControlClient _client;
    private readonly string _clientVersion;

    public GrpcAgentTransportClient(int localPort, string? clientVersion = null)
    {
        if (localPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort));
        }

        _clientVersion = string.IsNullOrWhiteSpace(clientVersion) ? "0.0.0" : clientVersion.Trim();
        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{localPort}");
        _client = new AgentControl.AgentControlClient(_channel);
    }

    public async ValueTask<AgentPeerInfo> NegotiateAsync(CancellationToken cancellationToken = default)
    {
        var request = new NegotiateRequest
        {
            Protocol = new ProtocolVersion
            {
                Major = checked((uint)AgentProtocolVersion.Current.Major),
                Minor = checked((uint)AgentProtocolVersion.Current.Minor),
            },
            ClientVersion = _clientVersion,
        };
        foreach (var capability in AgentCompatibilityPolicy.KnownCapabilities)
        {
            request.RequestedCapabilities.Add(ToWireCapability(capability));
        }

        try
        {
            var response = await _client.NegotiateAsync(request, cancellationToken: cancellationToken)
                .ResponseAsync
                .ConfigureAwait(false);
            return MapPeer(response);
        }
        catch (RpcException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Agent negotiation was cancelled.", exception, cancellationToken);
        }
        catch (RpcException exception)
        {
            throw MapRpcException(exception);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AgentTransportException(
                AgentConnectionState.Failed,
                "Agent negotiation failed.",
                exception);
        }
    }

    public async ValueTask<AgentHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.HealthAsync(new HealthRequest(), cancellationToken: cancellationToken)
                .ResponseAsync
                .ConfigureAwait(false);
            var state = (int)response.State switch
            {
                1 => AgentConnectionState.Available,
                2 => AgentConnectionState.Failed,
                _ => AgentConnectionState.Failed,
            };
            var detail = (int)response.State == 2 ? "Agent reports degraded health." : null;
            return new AgentHealthSnapshot(state, DateTimeOffset.UtcNow, response.AgentVersion, detail);
        }
        catch (RpcException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Agent health probe was cancelled.", exception, cancellationToken);
        }
        catch (RpcException exception)
        {
            var mapped = MapRpcException(exception);
            return new AgentHealthSnapshot(mapped.State, DateTimeOffset.UtcNow, Detail: mapped.Message);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return new AgentHealthSnapshot(
                AgentConnectionState.Failed,
                DateTimeOffset.UtcNow,
                Detail: "Agent health probe failed.");
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static AgentPeerInfo MapPeer(NegotiateResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.Protocol is null)
        {
            throw new AgentTransportException(
                AgentConnectionState.Failed,
                "Agent negotiation response is missing protocol metadata.");
        }

        var capabilities = new HashSet<ApplicationCapability>();
        foreach (var capability in response.Capabilities)
        {
            var mapped = FromWireCapability(capability);
            if (mapped is not null)
            {
                capabilities.Add(mapped.Value);
            }
        }

        return new AgentPeerInfo(
            new AgentProtocolVersion(
                checked((int)response.Protocol.Major),
                checked((int)response.Protocol.Minor)),
            response.AgentVersion,
            capabilities,
            NullIfWhiteSpace(response.Platform),
            NullIfWhiteSpace(response.Architecture));
    }

    internal static AgentTransportException MapRpcException(RpcException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var state = exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled
            ? AgentConnectionState.Disconnected
            : AgentConnectionState.Failed;
        var message = state == AgentConnectionState.Disconnected
            ? "Agent transport is disconnected."
            : "Agent transport request failed.";
        return new AgentTransportException(state, message, exception);
    }

    private static WireCapability ToWireCapability(ApplicationCapability capability) =>
        capability switch
        {
            ApplicationCapability.MetricsStreaming => (WireCapability)1,
            ApplicationCapability.ProcessEvents => (WireCapability)2,
            ApplicationCapability.ServiceEvents => (WireCapability)3,
            ApplicationCapability.DockerEvents => (WireCapability)4,
            ApplicationCapability.LogStreaming => (WireCapability)5,
            _ => (WireCapability)0,
        };

    private static ApplicationCapability? FromWireCapability(WireCapability capability) =>
        (int)capability switch
        {
            1 => ApplicationCapability.MetricsStreaming,
            2 => ApplicationCapability.ProcessEvents,
            3 => ApplicationCapability.ServiceEvents,
            4 => ApplicationCapability.DockerEvents,
            5 => ApplicationCapability.LogStreaming,
            _ => null,
        };

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
