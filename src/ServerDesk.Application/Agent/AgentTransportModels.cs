namespace ServerDesk.Application.Agent;

public enum AgentDataSource
{
    Agentless,
    Agent,
}

public enum AgentConnectionState
{
    Available,
    Unsupported,
    Disconnected,
    Incompatible,
    Failed,
}

public enum AgentCapability
{
    MetricsStreaming,
    ProcessEvents,
    ServiceEvents,
    DockerEvents,
    LogStreaming,
}

public readonly record struct AgentProtocolVersion(int Major, int Minor)
{
    public static AgentProtocolVersion Current { get; } = new(1, 0);

    public void Validate()
    {
        if (Major < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Major));
        }

        if (Minor < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(Minor));
        }
    }
}

public sealed record AgentPeerInfo(
    AgentProtocolVersion Protocol,
    string AgentVersion,
    IReadOnlySet<AgentCapability> Capabilities,
    string? Platform = null,
    string? Architecture = null);

public sealed record AgentNegotiationResult(
    AgentConnectionState State,
    AgentProtocolVersion Protocol,
    string? AgentVersion,
    IReadOnlySet<AgentCapability> Capabilities,
    string? Detail = null)
{
    public AgentConnectionState GetCapabilityState(AgentCapability capability) =>
        State == AgentConnectionState.Available && Capabilities.Contains(capability)
            ? AgentConnectionState.Available
            : State == AgentConnectionState.Available
                ? AgentConnectionState.Unsupported
                : State;
}

public sealed record AgentHealthSnapshot(
    AgentConnectionState State,
    DateTimeOffset CheckedAtUtc,
    string? AgentVersion = null,
    string? Detail = null);

public interface IAgentTransportClient : IAsyncDisposable
{
    ValueTask<AgentPeerInfo> NegotiateAsync(CancellationToken cancellationToken = default);

    ValueTask<AgentHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default);
}

public static class AgentCompatibilityPolicy
{
    public static AgentNegotiationResult Evaluate(
        AgentProtocolVersion clientProtocol,
        IEnumerable<AgentCapability> clientCapabilities,
        AgentPeerInfo peer)
    {
        ArgumentNullException.ThrowIfNull(clientCapabilities);
        ArgumentNullException.ThrowIfNull(peer);
        clientProtocol.Validate();
        peer.Protocol.Validate();

        if (string.IsNullOrWhiteSpace(peer.AgentVersion))
        {
            return new AgentNegotiationResult(
                AgentConnectionState.Failed,
                peer.Protocol,
                null,
                EmptyCapabilities(),
                "Agent version metadata is missing.");
        }

        if (clientProtocol.Major != peer.Protocol.Major)
        {
            return new AgentNegotiationResult(
                AgentConnectionState.Incompatible,
                peer.Protocol,
                peer.AgentVersion,
                EmptyCapabilities(),
                "Agent protocol major version is incompatible.");
        }

        var supportedByClient = NormalizeCapabilities(clientCapabilities);
        var supportedByPeer = NormalizeCapabilities(peer.Capabilities);
        supportedByClient.IntersectWith(supportedByPeer);
        return new AgentNegotiationResult(
            AgentConnectionState.Available,
            peer.Protocol,
            peer.AgentVersion,
            supportedByClient);
    }

    public static IReadOnlySet<AgentCapability> KnownCapabilities { get; } =
        new HashSet<AgentCapability>(Enum.GetValues<AgentCapability>());

    private static HashSet<AgentCapability> NormalizeCapabilities(IEnumerable<AgentCapability> capabilities)
    {
        var normalized = new HashSet<AgentCapability>();
        foreach (var capability in capabilities)
        {
            if (Enum.IsDefined(capability))
            {
                normalized.Add(capability);
            }
        }

        return normalized;
    }

    private static IReadOnlySet<AgentCapability> EmptyCapabilities() => new HashSet<AgentCapability>();
}
