using System.Runtime.CompilerServices;
using Grpc.Core;
using ServerDesk.Agent;
using ServerDesk.Agent.Contracts.V1;
using ServerDesk.Application.Agent;
using ServerDesk.Infrastructure.Ssh.Agent;
using Xunit;
using ApplicationCapability = ServerDesk.Application.Agent.AgentCapability;
using WireCapability = ServerDesk.Agent.Contracts.V1.AgentCapability;

namespace ServerDesk.Tests;

public sealed class AgentDockerEventStreamingTests
{
    [Fact]
    public void DockerObservationUsesFixedLocalReadOnlyCommand()
    {
        var start = DockerCliEventReader.BuildStartInfo();

        Assert.Equal("docker", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal(new[] { "events", "--format", "{{json .}}" }, start.ArgumentList);
        Assert.Equal("C", start.Environment["LC_ALL"]);
    }

    [Fact]
    public void DockerParserNormalizesExecActionWithoutLeakingRawCommandOrAttributes()
    {
        const string raw = "{\"Type\":\"container\",\"Action\":\"exec_create: /bin/sh -c echo secret-token\",\"Actor\":{\"ID\":\"abc123\",\"Attributes\":{\"password\":\"do-not-leak\",\"label\":\"private\"}},\"time\":1700000000}";

        var reading = DockerCliEventReader.Parse(raw);
        var rendered = reading.ToString();

        Assert.Equal(ObservedDockerObjectType.Container, reading.ObjectType);
        Assert.Equal(ObservedDockerEventKind.ExecCreated, reading.Kind);
        Assert.Equal("abc123", reading.ObjectId);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1700000000), reading.CapturedAtUtc);
        Assert.DoesNotContain("secret-token", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("do-not-leak", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DockerParserMapsUnknownTypeAndActionWithoutForwardingRawValues()
    {
        const string raw = "{\"Type\":\"future-secret-type\",\"Action\":\"future-sensitive-action\",\"Actor\":{\"ID\":\"object-1\"},\"time\":1700000001}";

        var reading = DockerCliEventReader.Parse(raw);
        var rendered = reading.ToString();

        Assert.Equal(ObservedDockerObjectType.Unknown, reading.ObjectType);
        Assert.Equal(ObservedDockerEventKind.Unknown, reading.Kind);
        Assert.DoesNotContain("future-secret-type", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("future-sensitive-action", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("exec_create: /bin/sh -c echo sensitive", ObservedDockerEventKind.ExecCreated)]
    [InlineData("exec_start: /usr/bin/python sensitive.py", ObservedDockerEventKind.ExecStarted)]
    [InlineData("exec_die", ObservedDockerEventKind.ExecDied)]
    [InlineData("health_status: healthy", ObservedDockerEventKind.HealthStatus)]
    public void DockerActionNormalizationUsesFixedEnums(string rawAction, ObservedDockerEventKind expected)
    {
        Assert.Equal(expected, DockerCliEventReader.NormalizeEventKind(rawAction));
    }

    [Fact]
    public async Task AgentAdvertisesDockerOnlyWhenImplementedAndRequested()
    {
        var service = new AgentControlService(
            new AgentRuntimeInfo("1.0.0-test", "linux", "x64", DateTimeOffset.UnixEpoch),
            new NoopMetricsSampler(),
            new EmptyProcessReader(),
            new EmptyServiceReader(),
            new SequenceDockerReader([]));
        var request = new NegotiateRequest { Protocol = new ProtocolVersion { Major = 1 }, ClientVersion = "test" };
        for (var value = 1; value <= 5; value++)
        {
            request.RequestedCapabilities.Add((WireCapability)value);
        }

        var response = await service.Negotiate(request, null!);

        Assert.Equal(4, response.Capabilities.Count);
        Assert.Contains(response.Capabilities, capability => (int)capability == 1);
        Assert.Contains(response.Capabilities, capability => (int)capability == 2);
        Assert.Contains(response.Capabilities, capability => (int)capability == 3);
        Assert.Contains(response.Capabilities, capability => (int)capability == 4);
        Assert.DoesNotContain(response.Capabilities, capability => (int)capability == 5);
    }

    [Fact]
    public async Task DockerStreamWritesSequentiallyAndHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new SequenceDockerReader(
        [
            new ObservedDockerEvent(ObservedDockerObjectType.Container, ObservedDockerEventKind.Start, "abc", DateTimeOffset.UnixEpoch.AddSeconds(1)),
            new ObservedDockerEvent(ObservedDockerObjectType.Container, ObservedDockerEventKind.Die, "abc", DateTimeOffset.UnixEpoch.AddSeconds(2)),
        ]);
        var service = new AgentControlService(
            new AgentRuntimeInfo("1.0.0-test", "linux", "x64", DateTimeOffset.UnixEpoch),
            new NoopMetricsSampler(),
            new EmptyProcessReader(),
            new EmptyServiceReader(),
            reader);
        var writer = new CancellingDockerWriter(cancellation);

        await service.StreamDockerEventsCoreAsync(new DockerEventsRequest(), writer, cancellation.Token);

        var item = Assert.Single(writer.Events);
        Assert.Equal(1, reader.YieldCount);
        Assert.Equal(1, (int)item.ObjectType);
        Assert.Equal(2, (int)item.Kind);
        Assert.Equal("abc", item.ObjectId);
    }

    [Fact]
    public async Task UnsupportedDockerCapabilityDegradesWithoutStartingPollingOrStream()
    {
        var source = new AgentEventSourceService(new AgentConnectionProbeService());
        await using var client = new FakeDockerEventClient(advertiseDocker: false);

        var selection = await source.SelectDockerEventsAsync(client, client, TestContext.Current.CancellationToken);

        Assert.Equal(AgentConnectionState.Unsupported, selection.State);
        Assert.Null(selection.Stream);
        Assert.Equal(0, client.StreamCalls);
        Assert.Contains("polling fallback is disabled", selection.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DockerWireMappingRejectsInvalidIdentifierWithSanitizedFailure()
    {
        var invalid = new DockerEvent
        {
            ObjectType = (DockerObjectType)1,
            Kind = (DockerEventKind)1,
            ObjectId = new string('x', 257),
            CapturedUnixMs = 1000,
        };

        var exception = Assert.Throws<AgentTransportException>(() => GrpcAgentEventStreamClient.MapDockerEvent(invalid));

        Assert.Equal(AgentConnectionState.Failed, exception.State);
        Assert.Equal("Agent Docker event is invalid.", exception.Message);
    }

    [Fact]
    public void ApplicationDockerEventModelHasOnlyNormalizedBoundedFields()
    {
        var names = typeof(AgentDockerEvent).GetProperties().Select(property => property.Name).ToArray();

        Assert.Equal(new[] { "ObjectType", "Kind", "ObjectId", "CapturedAtUtc" }, names);
        Assert.DoesNotContain(names, name => name.Contains("Action", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Attribute", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Label", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(names, name => name.Contains("Command", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class NoopMetricsSampler : IAgentMetricsSampler
    {
        public ValueTask<AgentMetricsReading> CaptureAsync(TimeSpan samplingInterval, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<AgentMetricsReading>(new InvalidOperationException("Not used by Docker event tests."));
    }

    private sealed class EmptyProcessReader : IAgentProcessSnapshotReader
    {
        public ValueTask<IReadOnlyDictionary<int, string>> CaptureAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyDictionary<int, string>>(new Dictionary<int, string>());
    }

    private sealed class EmptyServiceReader : IAgentServiceSnapshotReader
    {
        public ValueTask<IReadOnlyDictionary<string, ObservedServiceState>> CaptureAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyDictionary<string, ObservedServiceState>>(new Dictionary<string, ObservedServiceState>());
    }

    private sealed class SequenceDockerReader : IAgentDockerEventReader
    {
        private readonly IReadOnlyList<ObservedDockerEvent> _events;

        public SequenceDockerReader(IReadOnlyList<ObservedDockerEvent> events)
        {
            _events = events;
        }

        public int YieldCount { get; private set; }

        public async IAsyncEnumerable<ObservedDockerEvent> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in _events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                YieldCount++;
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class CancellingDockerWriter : IServerStreamWriter<DockerEvent>
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingDockerWriter(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public List<DockerEvent> Events { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(DockerEvent message)
        {
            Events.Add(message);
            _cancellation.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeDockerEventClient : IAgentTransportClient, IAgentDockerEventStreamClient
    {
        private readonly bool _advertiseDocker;

        public FakeDockerEventClient(bool advertiseDocker)
        {
            _advertiseDocker = advertiseDocker;
        }

        public int StreamCalls { get; private set; }

        public ValueTask<AgentPeerInfo> NegotiateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlySet<ApplicationCapability> capabilities = _advertiseDocker
                ? new HashSet<ApplicationCapability> { ApplicationCapability.DockerEvents }
                : new HashSet<ApplicationCapability>();
            return ValueTask.FromResult(new AgentPeerInfo(AgentProtocolVersion.Current, "test", capabilities, "linux", "x64"));
        }

        public ValueTask<AgentHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AgentHealthSnapshot(AgentConnectionState.Available, DateTimeOffset.UtcNow));

        public async IAsyncEnumerable<AgentDockerEvent> StreamDockerEventsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            await Task.Yield();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
