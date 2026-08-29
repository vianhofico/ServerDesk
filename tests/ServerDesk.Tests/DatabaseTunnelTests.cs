using System.Net;
using ServerDesk.Application.Databases;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Profiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseTunnelTests
{
    [Fact]
    public async Task OpenCreatesEphemeralLoopbackLocalForwardToExactRemoteEndpoint()
    {
        var server = ServerProfile.Create("DB host", "ssh.example.invalid", 22, "dev");
        var servers = new MemoryProfileRepository(server);
        var sessions = new CapturingPortForwardSessionFactory(boundPort: 49152);
        var service = new DatabaseTunnelService(servers, sessions);
        var profile = DatabaseConnectionProfile.Create(
            Guid.NewGuid(),
            server.Id,
            "Postgres",
            DatabaseEngineKind.PostgreSql,
            "127.0.0.1",
            5432,
            "appdb",
            "app",
            DatabaseAuthenticationKind.None,
            null);

        await using var lease = await service.OpenAsync(profile, TestContext.Current.CancellationToken);

        var forward = Assert.IsType<PortForwardProfile>(sessions.LastProfile);
        Assert.Equal(PortForwardKind.Local, forward.Kind);
        Assert.Equal(DatabaseTunnelService.LoopbackHost, forward.BindHost);
        Assert.Equal(0, forward.BindPort);
        Assert.Equal(profile.RemoteHost, forward.DestinationHost);
        Assert.Equal(profile.RemotePort, forward.DestinationPort);
        Assert.Equal(DatabaseTunnelService.LoopbackHost, lease.Endpoint.LocalHost);
        Assert.Equal(49152, lease.Endpoint.LocalPort);
        Assert.Equal(profile.Id, lease.Endpoint.DatabaseProfileId);
    }

    [Fact]
    public async Task DisposingLeaseStopsAndDisposesForwardSession()
    {
        var server = ServerProfile.Create("DB host", "ssh.example.invalid", 22, "dev");
        var sessions = new CapturingPortForwardSessionFactory(boundPort: 49153);
        var service = new DatabaseTunnelService(new MemoryProfileRepository(server), sessions);
        var profile = Profile(server.Id);

        var lease = await service.OpenAsync(profile, TestContext.Current.CancellationToken);
        await lease.DisposeAsync();

        Assert.NotNull(sessions.LastSession);
        Assert.Equal(1, sessions.LastSession.StopCalls);
        Assert.Equal(1, sessions.LastSession.DisposeCalls);
        Assert.Equal(PortForwardSessionState.Stopped, sessions.LastSession.State);
    }

    [Fact]
    public async Task MissingServerFailsClosedBeforeCreatingForward()
    {
        var sessions = new CapturingPortForwardSessionFactory(boundPort: 49154);
        var service = new DatabaseTunnelService(new MemoryProfileRepository(), sessions);

        var exception = await Assert.ThrowsAsync<DatabaseTunnelException>(async () =>
            await service.OpenAsync(Profile(Guid.NewGuid()), TestContext.Current.CancellationToken));

        Assert.Equal(RemoteErrorCode.InvalidEndpoint, exception.Error.Code);
        Assert.Null(sessions.LastProfile);
    }

    [Fact]
    public async Task ConnectivityRejectsNonLoopbackEndpointBeforeEngineProbe()
    {
        var profile = Profile(Guid.NewGuid());
        var tunnel = new FixedTunnelService(
            new DatabaseTunnelEndpoint(profile.Id, "0.0.0.0", 6543, profile.RemoteHost, profile.RemotePort));
        var probe = new CapturingEngineProbe();
        var service = new DatabaseTunnelConnectivityService(
            tunnel,
            DatabaseTunnelTestOptions.Default,
            probe);

        var result = await service.TestAsync(profile, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.InvalidEndpoint, result.Error!.Code);
        Assert.Contains("loopback", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, probe.Calls);
    }

    [Fact]
    public async Task ConnectivityUsesSelectedEngineProbeAndDoesNotClaimAuthentication()
    {
        var profile = DatabaseConnectionProfile.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Remote impossible endpoint",
            DatabaseEngineKind.PostgreSql,
            "203.0.113.254",
            5432,
            "appdb",
            "app",
            DatabaseAuthenticationKind.None,
            null);
        var tunnel = new FixedTunnelService(
            new DatabaseTunnelEndpoint(profile.Id, "127.0.0.1", 6543, profile.RemoteHost, profile.RemotePort));
        var probe = new CapturingEngineProbe();
        var service = new DatabaseTunnelConnectivityService(
            tunnel,
            new DatabaseTunnelTestOptions(TimeSpan.FromSeconds(2)),
            probe);

        var result = await service.TestAsync(profile, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Endpoint);
        Assert.Equal(6543, result.Endpoint.LocalPort);
        Assert.Equal(1, probe.Calls);
        Assert.Equal(DatabaseEngineKind.PostgreSql, probe.LastEngine);
        Assert.Equal(IPAddress.Loopback, probe.LastAddress);
        Assert.Equal(6543, probe.LastPort);
        Assert.Contains("credentials are not tested", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ConnectivityPropagatesEngineProbeFailureWithoutDirectFallback()
    {
        var profile = Profile(Guid.NewGuid());
        var tunnel = new FixedTunnelService(
            new DatabaseTunnelEndpoint(profile.Id, "127.0.0.1", 6544, profile.RemoteHost, profile.RemotePort));
        var expected = new RemoteError(RemoteErrorCode.ConnectionFailed, "fixture engine unreachable");
        var probe = new CapturingEngineProbe(new DatabaseEngineProbeResult(false, expected.Message, expected));
        var service = new DatabaseTunnelConnectivityService(
            tunnel,
            DatabaseTunnelTestOptions.Default,
            probe);

        var result = await service.TestAsync(profile, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Endpoint);
        Assert.Equal(expected, result.Error);
        Assert.Equal(1, probe.Calls);
    }

    private static DatabaseConnectionProfile Profile(Guid serverId) =>
        DatabaseConnectionProfile.Create(
            Guid.NewGuid(),
            serverId,
            "Redis",
            DatabaseEngineKind.Redis,
            "127.0.0.1",
            6379,
            null,
            null,
            DatabaseAuthenticationKind.None,
            null);

    private sealed class MemoryProfileRepository : IProfileRepository
    {
        private readonly Dictionary<Guid, ServerProfile> _profiles = [];

        public MemoryProfileRepository(params ServerProfile[] profiles)
        {
            foreach (var profile in profiles)
            {
                _profiles[profile.Id] = profile;
            }
        }

        public ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ServerProfile> profiles = _profiles.Values.ToArray();
            return ValueTask.FromResult(profiles);
        }

        public ValueTask<ServerProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles.TryGetValue(id, out var profile);
            return ValueTask.FromResult(profile);
        }

        public ValueTask UpsertAsync(ServerProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles[profile.Id] = profile;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles.Remove(id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingPortForwardSessionFactory : IPortForwardSessionFactory
    {
        private readonly int _boundPort;

        public CapturingPortForwardSessionFactory(int boundPort) => _boundPort = boundPort;

        public PortForwardProfile? LastProfile { get; private set; }
        public FakePortForwardSession? LastSession { get; private set; }

        public IPortForwardSession Create(ServerProfile serverProfile, PortForwardProfile forwardProfile)
        {
            LastProfile = forwardProfile;
            LastSession = new FakePortForwardSession(forwardProfile.Id, _boundPort);
            return LastSession;
        }
    }

    private sealed class FakePortForwardSession : IPortForwardSession
    {
        private readonly int _configuredPort;

        public FakePortForwardSession(Guid id, int configuredPort)
        {
            ForwardProfileId = id;
            _configuredPort = configuredPort;
        }

        public Guid ForwardProfileId { get; }
        public PortForwardSessionState State { get; private set; } = PortForwardSessionState.Created;
        public int BoundPort { get; private set; }
        public RemoteError? LastError { get; private set; }
        public int StopCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public event Action<PortForwardSessionState>? StateChanged;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BoundPort = _configuredPort;
            State = PortForwardSessionState.Active;
            StateChanged?.Invoke(State);
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            StopCalls++;
            BoundPort = 0;
            State = PortForwardSessionState.Stopped;
            StateChanged?.Invoke(State);
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            DisposeCalls++;
            BoundPort = 0;
            State = PortForwardSessionState.Stopped;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedTunnelService : IDatabaseTunnelService
    {
        private readonly DatabaseTunnelEndpoint _endpoint;

        public FixedTunnelService(DatabaseTunnelEndpoint endpoint) => _endpoint = endpoint;

        public ValueTask<IDatabaseTunnelLease> OpenAsync(
            DatabaseConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IDatabaseTunnelLease>(new FixedLease(_endpoint));
        }
    }

    private sealed class FixedLease : IDatabaseTunnelLease
    {
        public FixedLease(DatabaseTunnelEndpoint endpoint) => Endpoint = endpoint;

        public DatabaseTunnelEndpoint Endpoint { get; }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingEngineProbe : IDatabaseEngineConnectivityProbe
    {
        private readonly DatabaseEngineProbeResult _result;

        public CapturingEngineProbe(DatabaseEngineProbeResult? result = null)
        {
            _result = result ?? new DatabaseEngineProbeResult(true, "fixture protocol success", null);
        }

        public int Calls { get; private set; }
        public DatabaseEngineKind? LastEngine { get; private set; }
        public IPAddress? LastAddress { get; private set; }
        public int LastPort { get; private set; }

        public Task<DatabaseEngineProbeResult> ProbeAsync(
            DatabaseEngineKind engine,
            IPAddress localAddress,
            int localPort,
            TimeSpan timeout,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastEngine = engine;
            LastAddress = localAddress;
            LastPort = localPort;
            return Task.FromResult(_result);
        }
    }
}
