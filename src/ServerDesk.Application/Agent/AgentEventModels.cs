namespace ServerDesk.Application.Agent;

public sealed record AgentEventStreamOptions(TimeSpan ObservationInterval)
{
    public static TimeSpan MinimumObservationInterval { get; } = TimeSpan.FromMilliseconds(500);

    public static TimeSpan MaximumObservationInterval { get; } = TimeSpan.FromSeconds(10);

    public static AgentEventStreamOptions Default { get; } = new(TimeSpan.FromSeconds(1));

    public void Validate()
    {
        if (ObservationInterval < MinimumObservationInterval || ObservationInterval > MaximumObservationInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ObservationInterval),
                "Agent event observation interval must be between 500 milliseconds and 10 seconds.");
        }
    }
}

public enum AgentProcessEventKind
{
    Started,
    Exited,
}

public enum AgentServiceState
{
    Unknown,
    Active,
    Inactive,
    Activating,
    Deactivating,
    Failed,
}

public sealed record AgentProcessEvent(
    AgentProcessEventKind Kind,
    int ProcessId,
    string Name,
    DateTimeOffset CapturedAtUtc);

public sealed record AgentServiceEvent(
    string Unit,
    AgentServiceState PreviousState,
    AgentServiceState CurrentState,
    DateTimeOffset CapturedAtUtc);

public interface IAgentProcessEventStreamClient
{
    IAsyncEnumerable<AgentProcessEvent> StreamProcessEventsAsync(
        AgentEventStreamOptions options,
        CancellationToken cancellationToken = default);
}

public interface IAgentServiceEventStreamClient
{
    IAsyncEnumerable<AgentServiceEvent> StreamServiceEventsAsync(
        AgentEventStreamOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record AgentProcessEventSourceSelection(
    AgentConnectionState State,
    string? Detail,
    IAsyncEnumerable<AgentProcessEvent>? Stream);

public sealed record AgentServiceEventSourceSelection(
    AgentConnectionState State,
    string? Detail,
    IAsyncEnumerable<AgentServiceEvent>? Stream);

public sealed class AgentEventSourceService
{
    private readonly AgentConnectionProbeService _probeService;

    public AgentEventSourceService(AgentConnectionProbeService probeService)
    {
        _probeService = probeService ?? throw new ArgumentNullException(nameof(probeService));
    }

    public async ValueTask<AgentProcessEventSourceSelection> SelectProcessEventsAsync(
        IAgentTransportClient transportClient,
        IAgentProcessEventStreamClient eventClient,
        AgentEventStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transportClient);
        ArgumentNullException.ThrowIfNull(eventClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var negotiation = await _probeService.NegotiateAsync(transportClient, cancellationToken).ConfigureAwait(false);
        var state = negotiation.GetCapabilityState(AgentCapability.ProcessEvents);
        return state == AgentConnectionState.Available
            ? new AgentProcessEventSourceSelection(state, null, eventClient.StreamProcessEventsAsync(options, cancellationToken))
            : new AgentProcessEventSourceSelection(state, Detail("process", state), null);
    }

    public async ValueTask<AgentServiceEventSourceSelection> SelectServiceEventsAsync(
        IAgentTransportClient transportClient,
        IAgentServiceEventStreamClient eventClient,
        AgentEventStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transportClient);
        ArgumentNullException.ThrowIfNull(eventClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var negotiation = await _probeService.NegotiateAsync(transportClient, cancellationToken).ConfigureAwait(false);
        var state = negotiation.GetCapabilityState(AgentCapability.ServiceEvents);
        return state == AgentConnectionState.Available
            ? new AgentServiceEventSourceSelection(state, null, eventClient.StreamServiceEventsAsync(options, cancellationToken))
            : new AgentServiceEventSourceSelection(state, Detail("service", state), null);
    }

    private static string Detail(string stream, AgentConnectionState state) =>
        state == AgentConnectionState.Unsupported
            ? $"Agent {stream} events are unsupported; realtime polling fallback is disabled."
            : $"Agent {stream} event state is {state}; realtime polling fallback is disabled.";
}
