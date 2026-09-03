using ServerDesk.Application.Logs;

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

public enum AgentDockerObjectType
{
    Unknown,
    Container,
    Image,
    Volume,
    Network,
    Daemon,
}

public enum AgentDockerEventKind
{
    Unknown,
    Create,
    Start,
    Stop,
    Die,
    Destroy,
    Pause,
    Unpause,
    Restart,
    Rename,
    HealthStatus,
    Attach,
    Detach,
    Kill,
    Oom,
    Update,
    Connect,
    Disconnect,
    Pull,
    Push,
    Tag,
    Untag,
    Delete,
    Mount,
    Unmount,
    Reload,
    Prune,
    ExecCreated,
    ExecStarted,
    ExecDied,
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

public sealed record AgentDockerEvent(
    AgentDockerObjectType ObjectType,
    AgentDockerEventKind Kind,
    string? ObjectId,
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

public interface IAgentDockerEventStreamClient
{
    IAsyncEnumerable<AgentDockerEvent> StreamDockerEventsAsync(
        CancellationToken cancellationToken = default);
}

public interface IAgentLogStreamClient
{
    IAsyncEnumerable<LogEntry> StreamJournalLogsAsync(
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

public sealed record AgentDockerEventSourceSelection(
    AgentConnectionState State,
    string? Detail,
    IAsyncEnumerable<AgentDockerEvent>? Stream);

public sealed record AgentLogStreamSourceSelection(
    AgentConnectionState State,
    string? Detail,
    IAsyncEnumerable<LogEntry>? Stream);

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

    public async ValueTask<AgentDockerEventSourceSelection> SelectDockerEventsAsync(
        IAgentTransportClient transportClient,
        IAgentDockerEventStreamClient eventClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transportClient);
        ArgumentNullException.ThrowIfNull(eventClient);
        var negotiation = await _probeService.NegotiateAsync(transportClient, cancellationToken).ConfigureAwait(false);
        var state = negotiation.GetCapabilityState(AgentCapability.DockerEvents);
        return state == AgentConnectionState.Available
            ? new AgentDockerEventSourceSelection(state, null, eventClient.StreamDockerEventsAsync(cancellationToken))
            : new AgentDockerEventSourceSelection(state, Detail("Docker", state), null);
    }

    public async ValueTask<AgentLogStreamSourceSelection> SelectJournalLogsAsync(
        IAgentTransportClient transportClient,
        IAgentLogStreamClient logClient,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(transportClient);
        ArgumentNullException.ThrowIfNull(logClient);
        var negotiation = await _probeService.NegotiateAsync(transportClient, cancellationToken).ConfigureAwait(false);
        var state = negotiation.GetCapabilityState(AgentCapability.LogStreaming);
        return state == AgentConnectionState.Available
            ? new AgentLogStreamSourceSelection(state, null, logClient.StreamJournalLogsAsync(cancellationToken))
            : new AgentLogStreamSourceSelection(state, Detail("journal log", state), null);
    }

    private static string Detail(string stream, AgentConnectionState state) =>
        state == AgentConnectionState.Unsupported
            ? $"Agent {stream} events are unsupported; realtime polling fallback is disabled."
            : $"Agent {stream} event state is {state}; realtime polling fallback is disabled.";
}
