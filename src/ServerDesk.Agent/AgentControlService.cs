using Grpc.Core;
using ServerDesk.Agent.Contracts.V1;

namespace ServerDesk.Agent;

public sealed class AgentControlService : AgentControl.AgentControlBase
{
    internal static TimeSpan MinimumMetricsInterval { get; } = TimeSpan.FromMilliseconds(250);
    internal static TimeSpan MaximumMetricsInterval { get; } = TimeSpan.FromSeconds(10);
    internal static TimeSpan DefaultMetricsInterval { get; } = TimeSpan.FromSeconds(1);
    internal static TimeSpan MinimumEventInterval { get; } = TimeSpan.FromMilliseconds(500);
    internal static TimeSpan MaximumEventInterval { get; } = TimeSpan.FromSeconds(10);
    internal static TimeSpan DefaultEventInterval { get; } = TimeSpan.FromSeconds(1);

    private readonly AgentRuntimeInfo _runtimeInfo;
    private readonly IAgentMetricsSampler _metricsSampler;
    private readonly IAgentProcessSnapshotReader _processSnapshotReader;
    private readonly IAgentServiceSnapshotReader _serviceSnapshotReader;
    private readonly IAgentDockerEventReader? _dockerEventReader;
    private readonly IAgentJournalLogReader? _journalLogReader;
    private readonly bool _advertiseEventCapabilities;
    private readonly bool _advertiseDockerCapability;
    private readonly bool _advertiseLogCapability;

    internal AgentControlService(AgentRuntimeInfo runtimeInfo, IAgentMetricsSampler metricsSampler)
        : this(
            runtimeInfo,
            metricsSampler,
            new LinuxProcessSnapshotReader(),
            new SystemdServiceSnapshotReader(),
            dockerEventReader: null,
            journalLogReader: null,
            advertiseEventCapabilities: false,
            advertiseDockerCapability: false,
            advertiseLogCapability: false)
    {
    }

    public AgentControlService(
        AgentRuntimeInfo runtimeInfo,
        IAgentMetricsSampler metricsSampler,
        IAgentProcessSnapshotReader processSnapshotReader,
        IAgentServiceSnapshotReader serviceSnapshotReader)
        : this(
            runtimeInfo,
            metricsSampler,
            processSnapshotReader,
            serviceSnapshotReader,
            dockerEventReader: null,
            journalLogReader: null,
            advertiseEventCapabilities: true,
            advertiseDockerCapability: false,
            advertiseLogCapability: false)
    {
    }

    public AgentControlService(
        AgentRuntimeInfo runtimeInfo,
        IAgentMetricsSampler metricsSampler,
        IAgentProcessSnapshotReader processSnapshotReader,
        IAgentServiceSnapshotReader serviceSnapshotReader,
        IAgentDockerEventReader dockerEventReader)
        : this(
            runtimeInfo,
            metricsSampler,
            processSnapshotReader,
            serviceSnapshotReader,
            dockerEventReader,
            journalLogReader: null,
            advertiseEventCapabilities: true,
            advertiseDockerCapability: true,
            advertiseLogCapability: false)
    {
    }

    public AgentControlService(
        AgentRuntimeInfo runtimeInfo,
        IAgentMetricsSampler metricsSampler,
        IAgentProcessSnapshotReader processSnapshotReader,
        IAgentServiceSnapshotReader serviceSnapshotReader,
        IAgentDockerEventReader dockerEventReader,
        IAgentJournalLogReader journalLogReader)
        : this(
            runtimeInfo,
            metricsSampler,
            processSnapshotReader,
            serviceSnapshotReader,
            dockerEventReader,
            journalLogReader,
            advertiseEventCapabilities: true,
            advertiseDockerCapability: true,
            advertiseLogCapability: true)
    {
    }

    private AgentControlService(
        AgentRuntimeInfo runtimeInfo,
        IAgentMetricsSampler metricsSampler,
        IAgentProcessSnapshotReader processSnapshotReader,
        IAgentServiceSnapshotReader serviceSnapshotReader,
        IAgentDockerEventReader? dockerEventReader,
        IAgentJournalLogReader? journalLogReader,
        bool advertiseEventCapabilities,
        bool advertiseDockerCapability,
        bool advertiseLogCapability)
    {
        _runtimeInfo = runtimeInfo ?? throw new ArgumentNullException(nameof(runtimeInfo));
        _metricsSampler = metricsSampler ?? throw new ArgumentNullException(nameof(metricsSampler));
        _processSnapshotReader = processSnapshotReader ?? throw new ArgumentNullException(nameof(processSnapshotReader));
        _serviceSnapshotReader = serviceSnapshotReader ?? throw new ArgumentNullException(nameof(serviceSnapshotReader));
        if (advertiseDockerCapability && dockerEventReader is null)
        {
            throw new ArgumentNullException(nameof(dockerEventReader));
        }

        if (advertiseLogCapability && journalLogReader is null)
        {
            throw new ArgumentNullException(nameof(journalLogReader));
        }

        _dockerEventReader = dockerEventReader;
        _journalLogReader = journalLogReader;
        _advertiseEventCapabilities = advertiseEventCapabilities;
        _advertiseDockerCapability = advertiseDockerCapability;
        _advertiseLogCapability = advertiseLogCapability;
    }

    public override Task<NegotiateResponse> Negotiate(NegotiateRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = new NegotiateResponse
        {
            Protocol = new ProtocolVersion { Major = 1, Minor = 0 },
            AgentVersion = _runtimeInfo.Version,
            Platform = _runtimeInfo.Platform,
            Architecture = _runtimeInfo.Architecture,
        };

        if (request.RequestedCapabilities.Contains((AgentCapability)1))
        {
            response.Capabilities.Add((AgentCapability)1);
        }

        if (_advertiseEventCapabilities)
        {
            foreach (var capability in new[] { 2, 3 })
            {
                if (request.RequestedCapabilities.Contains((AgentCapability)capability))
                {
                    response.Capabilities.Add((AgentCapability)capability);
                }
            }
        }

        if (_advertiseDockerCapability && request.RequestedCapabilities.Contains((AgentCapability)4))
        {
            response.Capabilities.Add((AgentCapability)4);
        }

        if (_advertiseLogCapability && request.RequestedCapabilities.Contains((AgentCapability)5))
        {
            response.Capabilities.Add((AgentCapability)5);
        }

        return Task.FromResult(response);
    }

    public override Task<HealthResponse> Health(HealthRequest request, ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new HealthResponse
        {
            State = (AgentHealthState)1,
            AgentVersion = _runtimeInfo.Version,
            StartedUnixMs = _runtimeInfo.StartedAtUtc.ToUnixTimeMilliseconds(),
        });
    }

    public override Task StreamMetrics(StreamMetricsRequest request, IServerStreamWriter<MetricsSample> responseStream, ServerCallContext context) =>
        StreamMetricsCoreAsync(request, responseStream, context.CancellationToken);

    public override Task StreamProcessEvents(EventStreamRequest request, IServerStreamWriter<ProcessEvent> responseStream, ServerCallContext context) =>
        StreamProcessEventsCoreAsync(request, responseStream, context.CancellationToken);

    public override Task StreamServiceEvents(EventStreamRequest request, IServerStreamWriter<ServiceEvent> responseStream, ServerCallContext context) =>
        StreamServiceEventsCoreAsync(request, responseStream, context.CancellationToken);

    public override Task StreamDockerEvents(DockerEventsRequest request, IServerStreamWriter<DockerEvent> responseStream, ServerCallContext context) =>
        StreamDockerEventsCoreAsync(request, responseStream, context.CancellationToken);

    public override Task StreamJournalLogs(LogStreamRequest request, IServerStreamWriter<JournalLogEntry> responseStream, ServerCallContext context) =>
        StreamJournalLogsCoreAsync(request, responseStream, context.CancellationToken);

    internal async Task StreamMetricsCoreAsync(StreamMetricsRequest request, IServerStreamWriter<MetricsSample> responseStream, CancellationToken cancellationToken)
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

    internal async Task StreamProcessEventsCoreAsync(EventStreamRequest request, IServerStreamWriter<ProcessEvent> responseStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        var interval = ResolveEventInterval(request.IntervalMs);
        IReadOnlyDictionary<int, string> previous;
        try
        {
            previous = await _processSnapshotReader.CaptureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            throw new RpcException(new Status(StatusCode.Internal, "Process observation failed."));
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                var current = await _processSnapshotReader.CaptureAsync(cancellationToken).ConfigureAwait(false);
                foreach (var reading in AgentEventDiff.Processes(previous, current, DateTimeOffset.UtcNow))
                {
                    await responseStream.WriteAsync(new ProcessEvent
                    {
                        Kind = (ProcessEventKind)(reading.Started ? 1 : 2),
                        ProcessId = reading.ProcessId,
                        Name = reading.Name,
                        CapturedUnixMs = reading.CapturedAtUtc.ToUnixTimeMilliseconds(),
                    }).ConfigureAwait(false);
                }

                previous = current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException)
            {
                throw;
            }
            catch
            {
                throw new RpcException(new Status(StatusCode.Internal, "Process observation failed."));
            }
        }
    }

    internal async Task StreamServiceEventsCoreAsync(EventStreamRequest request, IServerStreamWriter<ServiceEvent> responseStream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        var interval = ResolveEventInterval(request.IntervalMs);
        IReadOnlyDictionary<string, ObservedServiceState> previous;
        try
        {
            previous = await _serviceSnapshotReader.CaptureAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch
        {
            throw new RpcException(new Status(StatusCode.Internal, "Service observation failed."));
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);
                var current = await _serviceSnapshotReader.CaptureAsync(cancellationToken).ConfigureAwait(false);
                foreach (var reading in AgentEventDiff.Services(previous, current, DateTimeOffset.UtcNow))
                {
                    await responseStream.WriteAsync(new ServiceEvent
                    {
                        Unit = reading.Unit,
                        PreviousState = (ServiceState)(int)reading.PreviousState,
                        CurrentState = (ServiceState)(int)reading.CurrentState,
                        CapturedUnixMs = reading.CapturedAtUtc.ToUnixTimeMilliseconds(),
                    }).ConfigureAwait(false);
                }

                previous = current;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (RpcException)
            {
                throw;
            }
            catch
            {
                throw new RpcException(new Status(StatusCode.Internal, "Service observation failed."));
            }
        }
    }

    internal async Task StreamDockerEventsCoreAsync(
        DockerEventsRequest request,
        IServerStreamWriter<DockerEvent> responseStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        if (_dockerEventReader is null)
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, "Docker event streaming is unavailable."));
        }

        try
        {
            await foreach (var reading in _dockerEventReader.StreamAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(new DockerEvent
                {
                    ObjectType = (DockerObjectType)(int)reading.ObjectType,
                    Kind = (DockerEventKind)(int)reading.Kind,
                    ObjectId = reading.ObjectId ?? string.Empty,
                    CapturedUnixMs = reading.CapturedAtUtc.ToUnixTimeMilliseconds(),
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (RpcException)
        {
            throw;
        }
        catch
        {
            throw new RpcException(new Status(StatusCode.Internal, "Docker observation failed."));
        }
    }

    internal async Task StreamJournalLogsCoreAsync(
        LogStreamRequest request,
        IServerStreamWriter<JournalLogEntry> responseStream,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(responseStream);
        if (_journalLogReader is null)
        {
            throw new RpcException(new Status(StatusCode.Unimplemented, "Journal log streaming is unavailable."));
        }

        try
        {
            await foreach (var reading in _journalLogReader.StreamAsync(cancellationToken).WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                await responseStream.WriteAsync(new JournalLogEntry
                {
                    CapturedUnixMs = reading.CapturedAtUtc.ToUnixTimeMilliseconds(),
                    Severity = (JournalLogSeverity)(int)reading.Severity,
                    Message = reading.Message,
                    Identifier = reading.Identifier,
                    SystemdUnit = reading.SystemdUnit,
                    ProcessId = reading.ProcessId ?? 0,
                    Hostname = reading.Hostname,
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (RpcException)
        {
            throw;
        }
        catch
        {
            throw new RpcException(new Status(StatusCode.Internal, "Journal observation failed."));
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
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Metrics interval must be between 250 and 10000 milliseconds."));
        }

        return interval;
    }

    internal static TimeSpan ResolveEventInterval(uint intervalMs)
    {
        if (intervalMs == 0)
        {
            return DefaultEventInterval;
        }

        var interval = TimeSpan.FromMilliseconds(intervalMs);
        if (interval < MinimumEventInterval || interval > MaximumEventInterval)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Event interval must be between 500 and 10000 milliseconds."));
        }

        return interval;
    }

    private static MetricsSample MapMetrics(AgentMetricsReading reading) => new()
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
