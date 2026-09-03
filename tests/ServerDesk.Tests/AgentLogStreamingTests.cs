using System.Runtime.CompilerServices;
using Grpc.Core;
using ServerDesk.Agent;
using ServerDesk.Agent.Contracts.V1;
using ServerDesk.Application.Agent;
using ServerDesk.Application.Logs;
using ServerDesk.Infrastructure.Ssh.Agent;
using Xunit;
using ApplicationCapability = ServerDesk.Application.Agent.AgentCapability;
using WireCapability = ServerDesk.Agent.Contracts.V1.AgentCapability;
using WireJournalLogEntry = ServerDesk.Agent.Contracts.V1.JournalLogEntry;

namespace ServerDesk.Tests;

public sealed class AgentLogStreamingTests
{
    [Fact]
    public void JournalObservationUsesFixedLocalFollowCommandWithoutHistory()
    {
        var start = JournalctlLogStreamReader.BuildStartInfo();

        Assert.Equal("journalctl", start.FileName);
        Assert.False(start.UseShellExecute);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal(new[] { "--follow", "--output=json", "--utc", "--no-pager", "--all", "--lines=0" }, start.ArgumentList);
        Assert.Equal("C", start.Environment["LC_ALL"]);
    }

    [Fact]
    public void JournalParserRedactsSecretsSanitizesControlsAndMapsStructuredFields()
    {
        const string raw = "{\"__REALTIME_TIMESTAMP\":\"1700000000000000\",\"PRIORITY\":\"3\",\"MESSAGE\":\"password=hunter2 token:abc123 Authorization: Bearer xyz https://user:pass@example.com/private\\nnext\",\"SYSLOG_IDENTIFIER\":\"app\",\"_SYSTEMD_UNIT\":\"app.service\",\"_PID\":\"42\",\"_HOSTNAME\":\"host-a\"}";

        var reading = JournalctlLogStreamReader.Parse(raw);

        Assert.Equal(ObservedJournalSeverity.Error, reading.Severity);
        Assert.Equal("app", reading.Identifier);
        Assert.Equal("app.service", reading.SystemdUnit);
        Assert.Equal(42, reading.ProcessId);
        Assert.Equal("host-a", reading.Hostname);
        Assert.DoesNotContain("hunter2", reading.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", reading.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer xyz", reading.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("user:pass@", reading.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\n', reading.Message);
        Assert.Contains("[REDACTED]", reading.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void JournalParserSuppressesPrivateKeyMaterialAndBoundsDisplayFields()
    {
        var redacted = JournalctlLogStreamReader.RedactSecrets("prefix -----BEGIN OPENSSH PRIVATE KEY----- sensitive");
        var bounded = JournalctlLogStreamReader.SanitizeField(new string('x', 5000), 128, redactSecrets: true);

        Assert.Equal("[REDACTED SENSITIVE LOG MESSAGE]", redacted);
        Assert.Equal(128, bounded.Length);
    }

    [Fact]
    public async Task AgentAdvertisesLogStreamingOnlyWhenImplementedAndRequested()
    {
        var service = new AgentControlService(
            new AgentRuntimeInfo("1.0.0-test", "linux", "x64", DateTimeOffset.UnixEpoch),
            new NoopMetricsSampler(),
            new EmptyProcessReader(),
            new EmptyServiceReader(),
            new EmptyDockerReader(),
            new SequenceJournalReader([]));
        var request = new NegotiateRequest { Protocol = new ProtocolVersion { Major = 1 }, ClientVersion = "test" };
        for (var value = 1; value <= 5; value++)
        {
            request.RequestedCapabilities.Add((WireCapability)value);
        }

        var response = await service.Negotiate(request, null!);

        Assert.Equal(5, response.Capabilities.Count);
        for (var value = 1; value <= 5; value++)
        {
            Assert.Contains(response.Capabilities, capability => (int)capability == value);
        }
    }

    [Fact]
    public async Task JournalStreamWritesSequentiallyAndHonorsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var reader = new SequenceJournalReader(
        [
            new ObservedJournalLog(DateTimeOffset.UnixEpoch.AddSeconds(1), ObservedJournalSeverity.Info, "one", "app", "app.service", 10, "host"),
            new ObservedJournalLog(DateTimeOffset.UnixEpoch.AddSeconds(2), ObservedJournalSeverity.Warning, "two", "app", "app.service", 10, "host"),
        ]);
        var service = new AgentControlService(
            new AgentRuntimeInfo("1.0.0-test", "linux", "x64", DateTimeOffset.UnixEpoch),
            new NoopMetricsSampler(),
            new EmptyProcessReader(),
            new EmptyServiceReader(),
            new EmptyDockerReader(),
            reader);
        var writer = new CancellingLogWriter(cancellation);

        await service.StreamJournalLogsCoreAsync(new LogStreamRequest(), writer, cancellation.Token);

        var item = Assert.Single(writer.Entries);
        Assert.Equal(1, reader.YieldCount);
        Assert.Equal("one", item.Message);
        Assert.Equal(7, (int)item.Severity);
    }

    [Fact]
    public async Task UnsupportedLogCapabilityDegradesWithoutStartingPollingOrStream()
    {
        var source = new AgentEventSourceService(new AgentConnectionProbeService());
        await using var client = new FakeLogClient(advertiseLog: false);

        var selection = await source.SelectJournalLogsAsync(client, client, TestContext.Current.CancellationToken);

        Assert.Equal(AgentConnectionState.Unsupported, selection.State);
        Assert.Null(selection.Stream);
        Assert.Equal(0, client.StreamCalls);
        Assert.Contains("polling fallback is disabled", selection.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void JournalWireMappingReusesExistingLogEntryAndRejectsUnsafeValues()
    {
        var wire = new WireJournalLogEntry
        {
            CapturedUnixMs = 1000,
            Severity = (JournalLogSeverity)4,
            Message = "safe",
            Identifier = "app",
            SystemdUnit = "app.service",
            ProcessId = 42,
            Hostname = "host",
        };

        var mapped = GrpcAgentEventStreamClient.MapJournalLogEntry(wire);

        Assert.Equal(LogSeverity.Error, mapped.Severity);
        Assert.Equal(ServerLogSource.Journal, mapped.Source);
        Assert.Equal("safe", mapped.Message);
        Assert.Equal(42, mapped.ProcessId);
        Assert.Null(mapped.Cursor);

        wire.Message = new string('x', 4097);
        var exception = Assert.Throws<AgentTransportException>(() => GrpcAgentEventStreamClient.MapJournalLogEntry(wire));
        Assert.Equal(AgentConnectionState.Failed, exception.State);
        Assert.Equal("Agent journal log entry is invalid.", exception.Message);
    }

    private sealed class NoopMetricsSampler : IAgentMetricsSampler
    {
        public ValueTask<AgentMetricsReading> CaptureAsync(TimeSpan samplingInterval, CancellationToken cancellationToken = default) =>
            ValueTask.FromException<AgentMetricsReading>(new InvalidOperationException("Not used by log tests."));
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

    private sealed class EmptyDockerReader : IAgentDockerEventReader
    {
        public async IAsyncEnumerable<ObservedDockerEvent> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield break;
        }
    }

    private sealed class SequenceJournalReader : IAgentJournalLogReader
    {
        private readonly IReadOnlyList<ObservedJournalLog> _entries;

        public SequenceJournalReader(IReadOnlyList<ObservedJournalLog> entries)
        {
            _entries = entries;
        }

        public int YieldCount { get; private set; }

        public async IAsyncEnumerable<ObservedJournalLog> StreamAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var item in _entries)
            {
                cancellationToken.ThrowIfCancellationRequested();
                YieldCount++;
                yield return item;
                await Task.Yield();
            }
        }
    }

    private sealed class CancellingLogWriter : IServerStreamWriter<WireJournalLogEntry>
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingLogWriter(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public List<WireJournalLogEntry> Entries { get; } = [];

        public WriteOptions? WriteOptions { get; set; }

        public Task WriteAsync(WireJournalLogEntry message)
        {
            Entries.Add(message);
            _cancellation.Cancel();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeLogClient : IAgentTransportClient, IAgentLogStreamClient
    {
        private readonly bool _advertiseLog;

        public FakeLogClient(bool advertiseLog)
        {
            _advertiseLog = advertiseLog;
        }

        public int StreamCalls { get; private set; }

        public ValueTask<AgentPeerInfo> NegotiateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlySet<ApplicationCapability> capabilities = _advertiseLog
                ? new HashSet<ApplicationCapability> { ApplicationCapability.LogStreaming }
                : new HashSet<ApplicationCapability>();
            return ValueTask.FromResult(new AgentPeerInfo(AgentProtocolVersion.Current, "test", capabilities, "linux", "x64"));
        }

        public ValueTask<AgentHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new AgentHealthSnapshot(AgentConnectionState.Available, DateTimeOffset.UtcNow));

        public async IAsyncEnumerable<LogEntry> StreamJournalLogsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            StreamCalls++;
            await Task.Yield();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
