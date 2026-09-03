using System.Net;
using Grpc.Core;
using ServerDesk.Agent;
using ServerDesk.Agent.Contracts.V1;
using ServerDesk.Application.Agent;
using ServerDesk.Application.Dashboard;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh.Agent;
using Xunit;
using ApplicationCapability = ServerDesk.Application.Agent.AgentCapability;
using WireCapability = ServerDesk.Agent.Contracts.V1.AgentCapability;

namespace ServerDesk.Tests;

public sealed class AgentHostAndTransportTests
{
    [Fact]
    public void ListenerConfigurationIsStructurallyLoopbackOnly()
    {
        var options = AgentListenerOptions.FromPort(41371);
        var endpoint = options.CreateEndpoint();

        Assert.Equal(IPAddress.Loopback, endpoint.Address);
        Assert.True(IPAddress.IsLoopback(endpoint.Address));
        Assert.Equal(41371, endpoint.Port);
        var property = Assert.Single(typeof(AgentListenerOptions).GetProperties());
        Assert.Equal(nameof(AgentListenerOptions.Port), property.Name);
    }

    [Fact]
    public void MetricsIntervalsAreStrictlyBounded()
    {
        Assert.Equal(TimeSpan.FromSeconds(1), AgentControlService.ResolveMetricsInterval(0));
        Assert.Equal(TimeSpan.FromMilliseconds(250), AgentControlService.ResolveMetricsInterval(250));
        Assert.Equal(TimeSpan.FromSeconds(10), AgentControlService.ResolveMetricsInterval(10000));
        Assert.Throws<RpcException>(() => AgentControlService.ResolveMetricsInterval(249));
        Assert.Throws<RpcException>(() => AgentControlService.ResolveMetricsInterval(10001));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AgentMetricsStreamOptions(TimeSpan.FromMilliseconds(249)).Validate());
    }

    [Fact]
    public async Task AgentAdvertisesOnlyImplementedMetricsCapability()
    {
        var runtime = new AgentRuntimeInfo("1.0.0-test", "linux", "x64", DateTimeOffset.UnixEpoch);
        var service = new AgentControlService(runtime, new FixedMetricsSampler());
        var request = new NegotiateRequest
        {
            Protocol = new ProtocolVersion { Major = 1, Minor = 0 },
            ClientVersion = "1.0.0-test",
        };
        for (var value = 1; value <= 5; value++)
        {
            request.RequestedCapabilities.Add((WireCapability)value);
        }

        var response = await service.Negotiate(request, null!);
        var capability = Assert.Single(response.Capabilities);

        Assert.Equal(1, (int)capability);
        Assert.Equal((uint)1, response.Protocol.Major);
        Assert.Equal((uint)0, response.Protocol.Minor);
    }

    [Fact]
    public void LinuxProcParserCalculatesFiniteCpuMemoryAndLoadMetrics()
    {
        var firstCpu = LinuxMetricsParser.ParseCpu("cpu  100 0 50 850 0 0 0 0 0 0\n");
        var secondCpu = LinuxMetricsParser.ParseCpu("cpu  160 0 90 950 0 0 0 0 0 0\n");
        var cpu = LinuxMetricsParser.CalculateCpuUtilization(firstCpu, secondCpu);
        var memory = LinuxMetricsParser.ParseMemory("MemTotal:       1000 kB\nMemAvailable:    400 kB\n");
        var load = LinuxMetricsParser.ParseLoad("0.25 0.50 0.75 1/100 123\n");

        Assert.Equal(50d, cpu, 6);
        Assert.Equal(1_024_000L, memory.TotalBytes);
        Assert.Equal(409_600L, memory.AvailableBytes);
        Assert.Equal(614_400L, memory.UsedBytes);
        Assert.Equal(60d, memory.UsedPercent, 6);
        Assert.Equal(0.25d, load.OneMinute, 6);
        Assert.Equal(0.50d, load.FiveMinutes, 6);
        Assert.Equal(0.75d, load.FifteenMinutes, 6);
    }

    [Fact]
    public async Task MetricsStreamWritesSequentialSamplesUntilCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var runtime = new AgentRuntimeInfo("1.0.0-test", "linux", "x64", DateTimeOffset.UnixEpoch);
        var sampler = new FixedMetricsSampler();
        var service = new AgentControlService(runtime, sampler);
        var writer = new CancellingMetricsWriter(cancellation, 2);

        await service.StreamMetricsCoreAsync(
            new StreamMetricsRequest { IntervalMs = 250 },
            writer,
            cancellation.Token);

        Assert.Equal(2, writer.Samples.Count);
        Assert.Equal(2, sampler.CaptureCount);
        Assert.All(writer.Samples, sample => Assert.InRange(sample.CpuUtilizationPercent, 0d, 100d));
    }

    [Fact]
    public void GrpcPeerMappingIgnoresUnknownCapabilities()
    {
        var response = new NegotiateResponse
        {
            Protocol = new ProtocolVersion { Major = 1, Minor = 0 },
            AgentVersion = "1.0.0-test",
            Platform = "linux",
            Architecture = "x64",
        };
        response.Capabilities.Add((WireCapability)1);
        response.Capabilities.Add((WireCapability)999);

        var peer = GrpcAgentTransportClient.MapPeer(response);

        var capability = Assert.Single(peer.Capabilities);
        Assert.Equal(ApplicationCapability.MetricsStreaming, capability);
    }

    [Fact]
    public void GrpcMetricsMappingRejectsInconsistentWireSample()
    {
        var invalid = new MetricsSample
        {
            CapturedUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CpuUtilizationPercent = 20,
            MemoryTotalBytes = 100,
            MemoryAvailableBytes = 80,
            MemoryUsedBytes = 50,
            MemoryUsedPercent = 20,
            LoadOneMinute = 0.1,
            LoadFiveMinutes = 0.2,
            LoadFifteenMinutes = 0.3,
        };

        var exception = Assert.Throws<AgentTransportException>(() => GrpcAgentTransportClient.MapMetric(invalid));

        Assert.Equal(AgentConnectionState.Failed, exception.State);
        Assert.DoesNotContain("memory", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MetricsSourceUsesAgentStreamWithoutCallingDashboardFallback()
    {
        var dashboard = new CountingDashboardService(CreateDashboardSnapshot());
        var source = new AgentMetricsSourceService(new AgentConnectionProbeService(), dashboard);
        await using var transport = new FakeAgentClient(AgentConnectionState.Available, advertiseMetrics: true);
        var options = AgentMetricsStreamOptions.Default;

        var selection = await source.SelectAsync(
            CreateProfile(),
            transport,
            transport,
            options,
            TestContext.Current.CancellationToken);
        var samples = new List<AgentMetricSample>();
        await foreach (var sample in selection.Stream!.WithCancellation(TestContext.Current.CancellationToken))
        {
            samples.Add(sample);
        }

        Assert.Equal(AgentDataSource.Agent, selection.Source);
        Assert.Equal(2, samples.Count);
        Assert.Equal(0, dashboard.CallCount);
    }

    [Fact]
    public async Task UnsupportedMetricsFallsBackToSingleAgentlessDashboardSnapshot()
    {
        var dashboard = new CountingDashboardService(CreateDashboardSnapshot());
        var source = new AgentMetricsSourceService(new AgentConnectionProbeService(), dashboard);
        await using var transport = new FakeAgentClient(AgentConnectionState.Available, advertiseMetrics: false);

        var selection = await source.SelectAsync(
            CreateProfile(),
            transport,
            transport,
            AgentMetricsStreamOptions.Default,
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentDataSource.Agentless, selection.Source);
        Assert.Equal(AgentConnectionState.Unsupported, selection.AgentState);
        Assert.Null(selection.Stream);
        Assert.NotNull(selection.Snapshot);
        Assert.Equal(1, dashboard.CallCount);
        Assert.Equal(35d, selection.Snapshot.CpuUtilizationPercent);
    }

    [Fact]
    public async Task AgentTunnelFactoryCreatesOnlyEphemeralLoopbackLocalForward()
    {
        var profile = CreateProfile();
        var forwardFactory = new CapturingForwardFactory();
        var tunnelFactory = new SshAgentTunnelSessionFactory(forwardFactory);
        await using var tunnel = tunnelFactory.Create(profile, 41371);

        var captured = Assert.IsType<PortForwardProfile>(forwardFactory.CapturedProfile);
        Assert.Equal(PortForwardKind.Local, captured.Kind);
        Assert.Equal("127.0.0.1", captured.BindHost);
        Assert.Equal(0, captured.BindPort);
        Assert.Equal("127.0.0.1", captured.DestinationHost);
        Assert.Equal(41371, captured.DestinationPort);

        await tunnel.StartAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentTunnelState.Active, tunnel.State);
        Assert.Equal(61234, tunnel.LocalPort);

        await tunnel.StopAsync(TestContext.Current.CancellationToken);
        Assert.Equal(AgentTunnelState.Stopped, tunnel.State);
        Assert.Equal(0, tunnel.LocalPort);
    }

    private static ServerProfile CreateProfile() =>
        ServerProfile.Create(
            Guid.NewGuid(),
            "Agent fixture",
            "example.test",
            22,
            "serverdesk",
            credentialReference: null,
            authenticationKind: ServerAuthenticationKind.Password);

    private static ServerDashboardSnapshot CreateDashboardSnapshot() =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            DashboardSection<CpuMetrics>.Available(new CpuMetrics(35, 4)),
            DashboardSection<LoadMetrics>.Available(new LoadMetrics(0.1, 0.2, 0.3)),
            DashboardSection<UptimeMetrics>.Missing("not needed"),
            DashboardSection<MemoryMetrics>.Available(new MemoryMetrics(1000, 400, 600, 60, 0, 0, 0, null)),
            DashboardSection<NetworkMetrics>.Missing("not needed"),
            DashboardSection<IReadOnlyList<FileSystemMetrics>>.Missing("not needed"),
            []);

    private sealed class FixedMetricsSampler : IAgentMetricsSampler
    {
        public int CaptureCount { get; private set; }

        public ValueTask<AgentMetricsReading> CaptureAsync(
            TimeSpan samplingInterval,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CaptureCount++;
            return ValueTask.FromResult(new AgentMetricsReading(
                DateTimeOffset.UtcNow,
                25,
                1000,
                400,
                600,
                60,
                0.1,
                0.2,
                0.3));
        }
    }

    private sealed class CancellingMetricsWriter : IServerStreamWriter<MetricsSample>
    {
        private readonly CancellationTokenSource _cancellation;
        private readonly int _cancelAfter;

        public CancellingMetricsWriter(CancellationTokenSource cancellation, int cancelAfter)
        {
            _cancellation = cancellation;
            _cancelAfter = cancelAfter;
        }

        public List<MetricsSample> Samples { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(MetricsSample message)
        {
            Samples.Add(message);
            if (Samples.Count >= _cancelAfter)
            {
                _cancellation.Cancel();
            }

            return Task.CompletedTask;
        }
    }

    private sealed class FakeAgentClient : IAgentTransportClient, IAgentMetricsStreamClient
    {
        private readonly AgentConnectionState _state;
        private readonly bool _advertiseMetrics;

        public FakeAgentClient(AgentConnectionState state, bool advertiseMetrics)
        {
            _state = state;
            _advertiseMetrics = advertiseMetrics;
        }

        public ValueTask<AgentPeerInfo> NegotiateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_state != AgentConnectionState.Available)
            {
                return ValueTask.FromException<AgentPeerInfo>(new AgentTransportException(_state, "Agent unavailable."));
            }

            IReadOnlySet<ApplicationCapability> capabilities = _advertiseMetrics
                ? new HashSet<ApplicationCapability> { ApplicationCapability.MetricsStreaming }
                : new HashSet<ApplicationCapability>();
            return ValueTask.FromResult(new AgentPeerInfo(
                AgentProtocolVersion.Current,
                "1.0.0-test",
                capabilities,
                "linux",
                "x64"));
        }

        public ValueTask<AgentHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AgentHealthSnapshot(_state, DateTimeOffset.UtcNow));

        public async IAsyncEnumerable<AgentMetricSample> StreamMetricsAsync(
            AgentMetricsStreamOptions options,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            options.Validate();
            for (var index = 0; index < 2; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new AgentMetricSample(
                    AgentDataSource.Agent,
                    DateTimeOffset.UtcNow,
                    20 + index,
                    1000,
                    400,
                    600,
                    60,
                    0.1,
                    0.2,
                    0.3);
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingDashboardService : IServerDashboardService
    {
        private readonly ServerDashboardSnapshot _snapshot;

        public CountingDashboardService(ServerDashboardSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public int CallCount { get; private set; }

        public ValueTask<ServerDashboardSnapshot> GetAsync(
            ServerProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(_snapshot with { ServerProfileId = profile.Id });
        }
    }

    private sealed class CapturingForwardFactory : IPortForwardSessionFactory
    {
        public PortForwardProfile? CapturedProfile { get; private set; }

        public IPortForwardSession Create(ServerProfile serverProfile, PortForwardProfile forwardProfile)
        {
            CapturedProfile = forwardProfile;
            return new FakeForwardSession(forwardProfile.Id);
        }
    }

    private sealed class FakeForwardSession : IPortForwardSession
    {
        public FakeForwardSession(Guid id)
        {
            ForwardProfileId = id;
        }

        public Guid ForwardProfileId { get; }

        public PortForwardSessionState State { get; private set; } = PortForwardSessionState.Created;

        public int BoundPort { get; private set; }

        public ServerDesk.Domain.Errors.RemoteError? LastError => null;

        public event Action<PortForwardSessionState>? StateChanged
        {
            add { }
            remove { }
        }

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = PortForwardSessionState.Active;
            BoundPort = 61234;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = PortForwardSessionState.Stopped;
            BoundPort = 0;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = PortForwardSessionState.Stopped;
            BoundPort = 0;
            return ValueTask.CompletedTask;
        }
    }
}
