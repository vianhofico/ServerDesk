using Grpc.Core;
using ServerDesk.Agent;
using ServerDesk.Agent.Contracts.V1;
using ServerDesk.Application.Agent;
using ServerDesk.Infrastructure.Ssh.Agent;
using Xunit;
using ApplicationCapability = ServerDesk.Application.Agent.AgentCapability;
using WireCapability = ServerDesk.Agent.Contracts.V1.AgentCapability;

namespace ServerDesk.Tests;

public sealed class AgentEventStreamingTests
{
    [Fact]
    public void EventIntervalsAreStrictlyBounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), AgentControlService.ResolveEventInterval(0));
        Assert.Equal(TimeSpan.FromMilliseconds(500), AgentControlService.ResolveEventInterval(500));
        Assert.Equal(TimeSpan.FromSeconds(10), AgentControlService.ResolveEventInterval(10000));
        Assert.Throws<RpcException>(() => AgentControlService.ResolveEventInterval(499));
        Assert.Throws<RpcException>(() => AgentControlService.ResolveEventInterval(10001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentEventStreamOptions(TimeSpan.FromMilliseconds(499)).Validate());
    }

    [Fact]
    public void ProcessDiffDetectsStartExitAndPidReuseWithoutBaselineBurst()
    {
        IReadOnlyDictionary<int, string> baseline = new Dictionary<int, string> { [10] = "stable", [20] = "old" };
        Assert.Empty(AgentEventDiff.Processes(baseline, baseline, DateTimeOffset.UnixEpoch));

        IReadOnlyDictionary<int, string> current = new Dictionary<int, string> { [20] = "new", [30] = "started" };
        var events = AgentEventDiff.Processes(baseline, current, DateTimeOffset.UnixEpoch);

        Assert.Equal(4, events.Count);
        Assert.Contains(events, item => !item.Started && item.ProcessId == 10 && item.Name == "stable");
        Assert.Contains(events, item => !item.Started && item.ProcessId == 20 && item.Name == "old");
        Assert.Contains(events, item => item.Started && item.ProcessId == 20 && item.Name == "new");
        Assert.Contains(events, item => item.Started && item.ProcessId == 30 && item.Name == "started");
    }

    [Fact]
    public void SystemdObservationIsFixedReadOnlyAndNormalizesStates()
    {
        var start = SystemdServiceSnapshotReader.BuildStartInfo();
        Assert.Equal("systemctl", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.Equal(new[] { "list-units", "--type=service", "--all", "--no-legend", "--no-pager", "--plain" }, start.ArgumentList);
        var parsed = SystemdServiceSnapshotReader.Parse(
            "alpha.service loaded active running Alpha\n" +
            "beta.service loaded failed failed Beta\n" +
            "gamma.service loaded mystery dead Gamma\n");
        Assert.Equal(ObservedServiceState.Active, parsed["alpha.service"]);
        Assert.Equal(ObservedServiceState.Failed, parsed["beta.service"]);
        Assert.Equal(ObservedServiceState.Unknown, parsed["gamma.service"]);
    }

    [Fact]
    public async Task AgentAdvertisesOnlyImplementedRequestedRealtimeCapabilities()
    {
        var service = new AgentControlService(
            new AgentRuntimeInfo("1.0.0-test", "linux", "x64", DateTimeOffset.UnixEpoch),
            new NoopMetricsSampler(),
            new SequenceProcessReader([]),
            new SequenceServiceReader([]));
        var request = new NegotiateRequest { Protocol = new ProtocolVersion { Major = 1 }, ClientVersion = "test" };
        for (var value = 1; value <= 5; value++)
        {
            request.RequestedCapabilities.Add((WireCapability)value);
        }

        var response = await service.Negotiate(request, null!);
        Assert.Equal(3, response.Capabilities.Count);
        Assert.Contains(response.Capabilities, capability => (int)capability == 1);
        Assert.Contains(response.Capabilities, capability => (int)capability == 2);
        Assert.Contains(response.Capabilities, capability => (int)capability == 3);
        Assert.DoesNotContain(response.Capabilities, capability => (int)capability is 4 or 5);
    }

    [Fact]
    public async Task ProcessStreamUsesBaselineThenWritesOnlyDiffAndHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new SequenceProcessReader(
        [
            new Dictionary<int, string> { [10] = "stable" },
            new Dictionary<int, string> { [10] = "stable", [20] = "worker" },
        ]);
        var service = new AgentControlService(
            new AgentRuntimeInfo("1.0.0-test", "linux", "x64", DateTimeOffset.UnixEpoch),
            new NoopMetricsSampler(),
            reader,
            new SequenceServiceReader([]));
        var writer = new CancellingProcessWriter(cancellation);

        await service.StreamProcessEventsCoreAsync(
            new EventStreamRequest { IntervalMs = 500 },
            writer,
            cancellation.Token);

        var item = Assert.Single(writer.Events);
        Assert.Equal(20, item.ProcessId);
        Assert.Equal(1, (int)item.Kind);
        Assert.Equal(2, reader.CaptureCount);
    }

    [Fact]
    public async Task UnsupportedEventCapabilityDegradesWithoutPollingFallback()
    {
        var source = new AgentEventSourceService(new AgentConnectionProbeService());
        await using var client = new FakeEventClient(advertiseProcess: false, advertiseService: false);
        var process = await source.SelectProcessEventsAsync(client, client, AgentEventStreamOptions.Default, TestContext.Current.CancellationToken);
        var service = await source.SelectServiceEventsAsync(client, client, AgentEventStreamOptions.Default, TestContext.Current.CancellationToken);

        Assert.Equal(AgentConnectionState.Unsupported, process.State);
        Assert.Equal(AgentConnectionState.Unsupported, service.State);
        Assert.Null(process.Stream);
        Assert.Null(service.Stream);
        Assert.Equal(0, client.ProcessStreamCalls);
        Assert.Equal(0, client.ServiceStreamCalls);
        Assert.Contains("polling fallback is disabled", process.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EventWireMappingRejectsUnsafeOrInvalidValues()
    {
        var process = new ProcessEvent { Kind = (ProcessEventKind)1, ProcessId = 42, Name = "worker", CapturedUnixMs = 1000 };
        var mapped = GrpcAgentEventStreamClient.MapProcessEvent(process);
        Assert.Equal(AgentProcessEventKind.Started, mapped.Kind);
        Assert.Equal("worker", mapped.Name);

        var invalid = new ServiceEvent
        {
            Unit = "not-a-service",
            PreviousState = (ServiceState)2,
            CurrentState = (ServiceState)1,
            CapturedUnixMs = 1000,
        };
        var exception = Assert.Throws<AgentTransportException>(() => GrpcAgentEventStreamClient.MapServiceEvent(invalid));
        Assert.Equal(AgentConnectionState.Failed, exception.State);
        Assert.Equal("Agent service event is invalid.", exception.Message);
    }

    private sealed class NoopMetricsSampler : IAgentMetricsSampler
    {
        public ValueTask<AgentMetricsReading> CaptureAsync(TimeSpan samplingInterval, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<AgentMetricsReading>(new InvalidOperationException("Not used by event tests."));
    }

    private sealed class SequenceProcessReader : IAgentProcessSnapshotReader
    {
        private readonly Queue<IReadOnlyDictionary<int, string>> _snapshots;
        public SequenceProcessReader(IEnumerable<IReadOnlyDictionary<int, string>> snapshots) => _snapshots = new Queue<IReadOnlyDictionary<int, string>>(snapshots);
        public int CaptureCount { get; private set; }
        public ValueTask<IReadOnlyDictionary<int, string>> CaptureAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            return ValueTask.FromResult(_snapshots.Dequeue());
        }
    }

    private sealed class SequenceServiceReader : IAgentServiceSnapshotReader
    {
        private readonly Queue<IReadOnlyDictionary<string, ObservedServiceState>> _snapshots;
        public SequenceServiceReader(IEnumerable<IReadOnlyDictionary<string, ObservedServiceState>> snapshots) => _snapshots = new Queue<IReadOnlyDictionary<string, ObservedServiceState>>(snapshots);
        public ValueTask<IReadOnlyDictionary<string, ObservedServiceState>> CaptureAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_snapshots.Dequeue());
        }
    }

    private sealed class CancellingProcessWriter : IServerStreamWriter<ProcessEvent>
    {
        private readonly CancellationTokenSource _cancellation;
        public CancellingProcessWriter(CancellationTokenSource cancellation) => _cancellation = cancellation;
        public List<ProcessEvent> Events { get; } = [];
        public WriteOptions? WriteOptions { get; set; }
        public Task WriteAsync(ProcessEvent message)
        {
            Events.Add(message);
            _cancellation.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeEventClient : IAgentTransportClient, IAgentProcessEventStreamClient, IAgentServiceEventStreamClient
    {
        private readonly bool _advertiseProcess;
        private readonly bool _advertiseService;
        public FakeEventClient(bool advertiseProcess, bool advertiseService)
        {
            _advertiseProcess = advertiseProcess;
            _advertiseService = advertiseService;
        }
        public int ProcessStreamCalls { get; private set; }
        public int ServiceStreamCalls { get; private set; }
        public ValueTask<AgentPeerInfo> NegotiateAsync(CancellationToken cancellationToken = default)
        {
            var capabilities = new HashSet<ApplicationCapability>();
            if (_advertiseProcess) capabilities.Add(ApplicationCapability.ProcessEvents);
            if (_advertiseService) capabilities.Add(ApplicationCapability.ServiceEvents);
            return ValueTask.FromResult(new AgentPeerInfo(AgentProtocolVersion.Current, "test", capabilities, "linux", "x64"));
        }
        public ValueTask<AgentHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AgentHealthSnapshot(AgentConnectionState.Available, DateTimeOffset.UtcNow));
        public async IAsyncEnumerable<AgentProcessEvent> StreamProcessEventsAsync(AgentEventStreamOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ProcessStreamCalls++;
            await Task.Yield();
            yield break;
        }
        public async IAsyncEnumerable<AgentServiceEvent> StreamServiceEventsAsync(AgentEventStreamOptions options, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            ServiceStreamCalls++;
            await Task.Yield();
            yield break;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
