using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using ServerDesk.Agent.Contracts.V1;
using ServerDesk.Application.Agent;
using WireProcessEvent = ServerDesk.Agent.Contracts.V1.ProcessEvent;
using WireServiceEvent = ServerDesk.Agent.Contracts.V1.ServiceEvent;

namespace ServerDesk.Infrastructure.Ssh.Agent;

public sealed class GrpcAgentEventStreamClient : IAgentProcessEventStreamClient, IAgentServiceEventStreamClient, IAsyncDisposable
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
}
