using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using ServerDesk.Agent.Contracts.V1;
using ServerDesk.Application.Agent;
using WireDockerEvent = ServerDesk.Agent.Contracts.V1.DockerEvent;
using WireProcessEvent = ServerDesk.Agent.Contracts.V1.ProcessEvent;
using WireServiceEvent = ServerDesk.Agent.Contracts.V1.ServiceEvent;

namespace ServerDesk.Infrastructure.Ssh.Agent;

public sealed class GrpcAgentEventStreamClient : IAgentProcessEventStreamClient, IAgentServiceEventStreamClient, IAgentDockerEventStreamClient, IAsyncDisposable
{
    private readonly GrpcChannel _channel;
    private readonly AgentControl.AgentControlClient _client;

    public GrpcAgentEventStreamClient(int localPort)
    {
        if (localPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(localPort));
        }

        _channel = GrpcChannel.ForAddress($"http://127.0.0.1:{localPort}");
        _client = new AgentControl.AgentControlClient(_channel);
    }

    public async IAsyncEnumerable<AgentProcessEvent> StreamProcessEventsAsync(
        AgentEventStreamOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        using var call = _client.StreamProcessEvents(
            new EventStreamRequest { IntervalMs = checked((uint)options.ObservationInterval.TotalMilliseconds) },
            cancellationToken: cancellationToken);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Agent process event stream was cancelled.", exception, cancellationToken);
            }
            catch (RpcException exception)
            {
                throw GrpcAgentTransportClient.MapRpcException(exception);
            }

            if (!hasNext)
            {
                yield break;
            }

            yield return MapProcessEvent(call.ResponseStream.Current);
        }
    }

    public async IAsyncEnumerable<AgentServiceEvent> StreamServiceEventsAsync(
        AgentEventStreamOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        using var call = _client.StreamServiceEvents(
            new EventStreamRequest { IntervalMs = checked((uint)options.ObservationInterval.TotalMilliseconds) },
            cancellationToken: cancellationToken);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Agent service event stream was cancelled.", exception, cancellationToken);
            }
            catch (RpcException exception)
            {
                throw GrpcAgentTransportClient.MapRpcException(exception);
            }

            if (!hasNext)
            {
                yield break;
            }

            yield return MapServiceEvent(call.ResponseStream.Current);
        }
    }

    public async IAsyncEnumerable<AgentDockerEvent> StreamDockerEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var call = _client.StreamDockerEvents(new DockerEventsRequest(), cancellationToken: cancellationToken);
        while (true)
        {
            bool hasNext;
            try
            {
                hasNext = await call.ResponseStream.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException exception) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Agent Docker event stream was cancelled.", exception, cancellationToken);
            }
            catch (RpcException exception)
            {
                throw GrpcAgentTransportClient.MapRpcException(exception);
            }

            if (!hasNext)
            {
                yield break;
            }

            yield return MapDockerEvent(call.ResponseStream.Current);
        }
    }

    public ValueTask DisposeAsync()
    {
        _channel.Dispose();
        return ValueTask.CompletedTask;
    }

    internal static AgentProcessEvent MapProcessEvent(WireProcessEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            if (value.ProcessId <= 0 || string.IsNullOrWhiteSpace(value.Name) || value.Name.Length > 256)
            {
                throw new InvalidOperationException();
            }

            var kind = (int)value.Kind switch
            {
                1 => AgentProcessEventKind.Started,
                2 => AgentProcessEventKind.Exited,
                _ => throw new InvalidOperationException(),
            };
            return new AgentProcessEvent(kind, value.ProcessId, value.Name, DateTimeOffset.FromUnixTimeMilliseconds(value.CapturedUnixMs));
        }
        catch (Exception exception) when (exception is not AgentTransportException)
        {
            throw new AgentTransportException(AgentConnectionState.Failed, "Agent process event is invalid.", exception);
        }
    }

    internal static AgentServiceEvent MapServiceEvent(WireServiceEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            if (string.IsNullOrWhiteSpace(value.Unit) || value.Unit.Length > 256 || !value.Unit.EndsWith(".service", StringComparison.Ordinal))
            {
                throw new InvalidOperationException();
            }

            var previous = MapServiceState(value.PreviousState);
            var current = MapServiceState(value.CurrentState);
            if (previous == current)
            {
                throw new InvalidOperationException();
            }

            return new AgentServiceEvent(value.Unit, previous, current, DateTimeOffset.FromUnixTimeMilliseconds(value.CapturedUnixMs));
        }
        catch (Exception exception) when (exception is not AgentTransportException)
        {
            throw new AgentTransportException(AgentConnectionState.Failed, "Agent service event is invalid.", exception);
        }
    }

    internal static AgentDockerEvent MapDockerEvent(WireDockerEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        try
        {
            string? objectId = null;
            if (!string.IsNullOrWhiteSpace(value.ObjectId))
            {
                objectId = value.ObjectId.Trim();
                if (objectId.Length > 256 || objectId.Any(char.IsControl))
                {
                    throw new InvalidOperationException();
                }
            }

            return new AgentDockerEvent(
                MapDockerObjectType(value.ObjectType),
                MapDockerEventKind(value.Kind),
                objectId,
                DateTimeOffset.FromUnixTimeMilliseconds(value.CapturedUnixMs));
        }
        catch (Exception exception) when (exception is not AgentTransportException)
        {
            throw new AgentTransportException(AgentConnectionState.Failed, "Agent Docker event is invalid.", exception);
        }
    }

    private static AgentServiceState MapServiceState(ServiceState value) =>
        (int)value switch
        {
            0 => AgentServiceState.Unknown,
            1 => AgentServiceState.Active,
            2 => AgentServiceState.Inactive,
            3 => AgentServiceState.Activating,
            4 => AgentServiceState.Deactivating,
            5 => AgentServiceState.Failed,
            _ => throw new InvalidOperationException(),
        };

    private static AgentDockerObjectType MapDockerObjectType(DockerObjectType value) =>
        (int)value switch
        {
            0 => AgentDockerObjectType.Unknown,
            1 => AgentDockerObjectType.Container,
            2 => AgentDockerObjectType.Image,
            3 => AgentDockerObjectType.Volume,
            4 => AgentDockerObjectType.Network,
            5 => AgentDockerObjectType.Daemon,
            _ => throw new InvalidOperationException(),
        };

    private static AgentDockerEventKind MapDockerEventKind(DockerEventKind value) =>
        (int)value switch
        {
            0 => AgentDockerEventKind.Unknown,
            1 => AgentDockerEventKind.Create,
            2 => AgentDockerEventKind.Start,
            3 => AgentDockerEventKind.Stop,
            4 => AgentDockerEventKind.Die,
            5 => AgentDockerEventKind.Destroy,
            6 => AgentDockerEventKind.Pause,
            7 => AgentDockerEventKind.Unpause,
            8 => AgentDockerEventKind.Restart,
            9 => AgentDockerEventKind.Rename,
            10 => AgentDockerEventKind.HealthStatus,
            11 => AgentDockerEventKind.Attach,
            12 => AgentDockerEventKind.Detach,
            13 => AgentDockerEventKind.Kill,
            14 => AgentDockerEventKind.Oom,
            15 => AgentDockerEventKind.Update,
            16 => AgentDockerEventKind.Connect,
            17 => AgentDockerEventKind.Disconnect,
            18 => AgentDockerEventKind.Pull,
            19 => AgentDockerEventKind.Push,
            20 => AgentDockerEventKind.Tag,
            21 => AgentDockerEventKind.Untag,
            22 => AgentDockerEventKind.Delete,
            23 => AgentDockerEventKind.Mount,
            24 => AgentDockerEventKind.Unmount,
            25 => AgentDockerEventKind.Reload,
            26 => AgentDockerEventKind.Prune,
            27 => AgentDockerEventKind.ExecCreated,
            28 => AgentDockerEventKind.ExecStarted,
            29 => AgentDockerEventKind.ExecDied,
            _ => throw new InvalidOperationException(),
        };
}
