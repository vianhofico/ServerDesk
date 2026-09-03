using Grpc.Core;
using ServerDesk.Agent.Contracts.V1;

namespace ServerDesk.Agent;

public sealed class AgentControlService : AgentControl.AgentControlBase
{
    internal static TimeSpan MinimumMetricsInterval { get; } = TimeSpan.FromMilliseconds(250);
    internal static TimeSpan MaximumMetricsInterval { get; } = TimeSpan.FromSeconds(10);
    internal static TimeSpan DefaultMetricsInterval { get; } = TimeSpan.FromSeconds(1);

    private readonly AgentRuntimeInfo _runtimeInfo;
    private readonly IAgentMetricsSampler _metricsSampler;

    public AgentControlService(
        AgentRuntimeInfo runtimeInfo,
        IAgentMetricsSampler metricsSampler)
    {
        _runtimeInfo = runtimeInfo ?? throw new ArgumentNullException(nameof(runtimeInfo));
        _metricsSampler = metricsSampler ?? throw new ArgumentNullException(nameof(metricsSampler));
    }

    public override Task<NegotiateResponse> Negotiate(
        NegotiateRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = new NegotiateResponse
        {
            Protocol = new ProtocolVersion
            {
                Major = 1,
                Minor = 0,
            },
            AgentVersion = _runtimeInfo.Version,
            Platform = _runtimeInfo.Platform,
            Architecture = _runtimeInfo.Architecture,
        };

        if (request.RequestedCapabilities.Contains((AgentCapability)1))
        {
            response.Capabilities.Add((AgentCapability)1);
        }

        return Task.FromResult(response);
    }

    public override Task<HealthResponse> Health(
        HealthRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new HealthResponse
        {
            State = (AgentHealthState)1,
            AgentVersion = _runtimeInfo.Version,
            StartedUnixMs = _runtimeInfo.StartedAtUtc.ToUnixTimeMilliseconds(),
        });
    }

    public override Task StreamMetrics(
        StreamMetricsRequest request,
        IServerStreamWriter<MetricsSample> responseStream,
        ServerCallContext context) =>
        StreamMetricsCoreAsync(request, responseStream, context.CancellationToken);

    internal async Task StreamMetricsCoreAsync(
        StreamMetricsRequest request,
        IServerStreamWriter<MetricsSample> responseStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        var interval = ResolveMetricsInterval(request.IntervalMs);
        while (!cancellationToken.IsCancellationRequested)
        {
            AgentMetricsReading reading;
            try
            {
                reading = await _metricsSampler.CaptureAsync(interval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                throw new RpcException(new Status(StatusCode.Internal, "Metrics sampling failed."));
            }

            await responseStream.WriteAsync(MapMetrics(reading)).ConfigureAwait(false);
        }
    }

    internal static TimeSpan ResolveMetricsInterval(uint intervalMs)
    {
        if (intervalMs == 0)
        {
            return DefaultMetricsInterval;
        }

        var interval = TimeSpan.FromMilliseconds(intervalMs);
        if (interval < MinimumMetricsInterval || interval > MaximumMetricsInterval)
        {
            throw new RpcException(new Status(
                StatusCode.InvalidArgument,
                "Metrics interval must be between 250 and 10000 milliseconds."));
        }

        return interval;
    }

    private static MetricsSample MapMetrics(AgentMetricsReading reading) =>
        new()
        {
            CapturedUnixMs = reading.CapturedAtUtc.ToUnixTimeMilliseconds(),
            CpuUtilizationPercent = reading.CpuUtilizationPercent,
            MemoryTotalBytes = checked((ulong)reading.MemoryTotalBytes),
            MemoryAvailableBytes = checked((ulong)reading.MemoryAvailableBytes),
            MemoryUsedBytes = checked((ulong)reading.MemoryUsedBytes),
            MemoryUsedPercent = reading.MemoryUsedPercent,
            LoadOneMinute = reading.LoadOneMinute,
            LoadFiveMinutes = reading.LoadFiveMinutes,
            LoadFifteenMinutes = reading.LoadFifteenMinutes,
        };
}
