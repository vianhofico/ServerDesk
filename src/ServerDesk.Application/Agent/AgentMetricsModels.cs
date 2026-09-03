using ServerDesk.Application.Dashboard;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Agent;

public sealed record AgentMetricsStreamOptions(TimeSpan SamplingInterval)
{
    public static TimeSpan MinimumSamplingInterval { get; } = TimeSpan.FromMilliseconds(250);

    public static TimeSpan MaximumSamplingInterval { get; } = TimeSpan.FromSeconds(10);

    public static AgentMetricsStreamOptions Default { get; } = new(TimeSpan.FromSeconds(1));

    public void Validate()
    {
        if (SamplingInterval < MinimumSamplingInterval || SamplingInterval > MaximumSamplingInterval)
        {
            throw new ArgumentOutOfRangeException(
                nameof(SamplingInterval),
                "Agent metrics sampling interval must be between 250 milliseconds and 10 seconds.");
        }
    }
}

public sealed record AgentMetricSample(
    AgentDataSource Source,
    DateTimeOffset CapturedAtUtc,
    double? CpuUtilizationPercent,
    long? MemoryTotalBytes,
    long? MemoryAvailableBytes,
    long? MemoryUsedBytes,
    double? MemoryUsedPercent,
    double? LoadOneMinute,
    double? LoadFiveMinutes,
    double? LoadFifteenMinutes);

public interface IAgentMetricsStreamClient
{
    IAsyncEnumerable<AgentMetricSample> StreamMetricsAsync(
        AgentMetricsStreamOptions options,
        CancellationToken cancellationToken = default);
}

public sealed record AgentMetricsSourceSelection(
    AgentDataSource Source,
    AgentConnectionState AgentState,
    string? Detail,
    IAsyncEnumerable<AgentMetricSample>? Stream,
    AgentMetricSample? Snapshot);

public sealed class AgentMetricsSourceService
{
    private readonly AgentConnectionProbeService _probeService;
    private readonly IServerDashboardService _dashboardService;

    public AgentMetricsSourceService(
        AgentConnectionProbeService probeService,
        IServerDashboardService dashboardService)
    {
        _probeService = probeService ?? throw new ArgumentNullException(nameof(probeService));
        _dashboardService = dashboardService ?? throw new ArgumentNullException(nameof(dashboardService));
    }

    public async ValueTask<AgentMetricsSourceSelection> SelectAsync(
        ServerProfile profile,
        IAgentTransportClient transportClient,
        IAgentMetricsStreamClient metricsClient,
        AgentMetricsStreamOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(transportClient);
        ArgumentNullException.ThrowIfNull(metricsClient);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var negotiation = await _probeService.NegotiateAsync(transportClient, cancellationToken).ConfigureAwait(false);
        if (negotiation.GetCapabilityState(AgentCapability.MetricsStreaming) == AgentConnectionState.Available)
        {
            return new AgentMetricsSourceSelection(
                AgentDataSource.Agent,
                AgentConnectionState.Available,
                null,
                metricsClient.StreamMetricsAsync(options, cancellationToken),
                null);
        }

        var dashboard = await _dashboardService.GetAsync(profile, cancellationToken).ConfigureAwait(false);
        var detail = negotiation.State == AgentConnectionState.Available
            ? "Agent metrics streaming is unsupported; using one agentless dashboard snapshot."
            : $"Agent metrics state is {negotiation.State}; using one agentless dashboard snapshot.";
        return new AgentMetricsSourceSelection(
            AgentDataSource.Agentless,
            negotiation.State == AgentConnectionState.Available ? AgentConnectionState.Unsupported : negotiation.State,
            detail,
            null,
            MapDashboardSnapshot(dashboard));
    }

    internal static AgentMetricSample MapDashboardSnapshot(ServerDashboardSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var cpu = snapshot.Cpu.Status == DashboardSectionStatus.Available ? snapshot.Cpu.Value : null;
        var memory = snapshot.Memory.Status == DashboardSectionStatus.Available ? snapshot.Memory.Value : null;
        var load = snapshot.Load.Status == DashboardSectionStatus.Available ? snapshot.Load.Value : null;
        return new AgentMetricSample(
            AgentDataSource.Agentless,
            snapshot.CapturedAtUtc,
            cpu?.UtilizationPercent,
            memory?.TotalBytes,
            memory?.AvailableBytes,
            memory?.UsedBytes,
            memory?.UsedPercent,
            load?.OneMinute,
            load?.FiveMinutes,
            load?.FifteenMinutes);
    }
}
