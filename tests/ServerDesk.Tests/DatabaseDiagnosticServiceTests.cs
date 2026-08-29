using System.Net;
using ServerDesk.Application.Databases;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Secrets;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseDiagnosticServiceTests
{
    [Fact]
    public async Task PasswordDiagnosticsPassOnlyLoopbackTunnelEndpointAndEphemeralSecretToAdapter()
    {
        var profile = PasswordProfile(DatabaseEngineKind.PostgreSql);
        var secrets = new FakeSecretStore { Value = "db-secret" };
        var tunnel = new FakeTunnelService(new DatabaseTunnelEndpoint(
            profile.Id,
            "127.0.0.1",
            49155,
            profile.RemoteHost,
            profile.RemotePort));
        var adapter = new CapturingAdapter(DatabaseEngineKind.PostgreSql);
        var service = new DatabaseDiagnosticService(
            tunnel,
            secrets,
            [adapter],
            DatabaseDiagnosticOptions.Default);

        var result = await service.InspectAsync(profile, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.NotNull(adapter.LastRequest);
        Assert.Equal(profile.Id, adapter.LastRequest.ProfileId);
        Assert.Equal(DatabaseEngineKind.PostgreSql, adapter.LastRequest.Engine);
        Assert.Equal(IPAddress.Loopback, adapter.LastRequest.LocalAddress);
        Assert.Equal(49155, adapter.LastRequest.LocalPort);
        Assert.Equal(profile.DatabaseName, adapter.LastRequest.DatabaseName);
        Assert.Equal(profile.Username, adapter.LastRequest.Username);
        Assert.Equal("db-secret", adapter.LastRequest.Secret);
        Assert.Equal(1, secrets.GetCalls);
        Assert.Equal(1, tunnel.OpenCalls);
        Assert.Equal(1, tunnel.DisposeCalls);
    }

    [Fact]
    public async Task MissingSecretFailsBeforeOpeningTunnel()
    {
        var profile = PasswordProfile(DatabaseEngineKind.PostgreSql);
        var secrets = new FakeSecretStore { Value = null };
        var tunnel = new FakeTunnelService(LoopbackEndpoint(profile));
        var adapter = new CapturingAdapter(DatabaseEngineKind.PostgreSql);
        var service = new DatabaseDiagnosticService(tunnel, secrets, [adapter], DatabaseDiagnosticOptions.Default);

        var result = await service.InspectAsync(profile, TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(DatabaseDiagnosticFailureKind.SecretUnavailable, result.Failure!.Kind);
        Assert.Equal(0, tunnel.OpenCalls);
        Assert.Equal(0, adapter.Calls);
    }

    [Fact]
    public async Task SecretStoreFailureIsSanitizedAndFailsBeforeTunnel()
    {
        var profile = PasswordProfile(DatabaseEngineKind.PostgreSql);
        var secrets = new FakeSecretStore { ThrowOnGet = true };
        var tunnel = new FakeTunnelService(LoopbackEndpoint(profile));
        var adapter = new CapturingAdapter(DatabaseEngineKind.PostgreSql);
        var service = new DatabaseDiagnosticService(tunnel, secrets, [adapter], DatabaseDiagnosticOptions.Default);

        var result = await service.InspectAsync(profile, TestContext.Current.CancellationToken);

        Assert.Equal(DatabaseDiagnosticFailureKind.SecretUnavailable, result.Failure!.Kind);
        Assert.DoesNotContain("fixture-secret", result.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, tunnel.OpenCalls);
        Assert.Equal(0, adapter.Calls);
    }

    [Fact]
    public async Task NonLoopbackTunnelFailsClosedBeforeAdapterAndStillDisposesLease()
    {
        var profile = NoPasswordProfile(DatabaseEngineKind.Redis);
        var tunnel = new FakeTunnelService(new DatabaseTunnelEndpoint(
            profile.Id,
            "0.0.0.0",
            6379,
            profile.RemoteHost,
            profile.RemotePort));
        var adapter = new CapturingAdapter(DatabaseEngineKind.Redis);
        var service = new DatabaseDiagnosticService(
            tunnel,
            new FakeSecretStore(),
            [adapter],
            DatabaseDiagnosticOptions.Default);

        var result = await service.InspectAsync(profile, TestContext.Current.CancellationToken);

        Assert.Equal(DatabaseDiagnosticFailureKind.NetworkFailed, result.Failure!.Kind);
        Assert.Equal(0, adapter.Calls);
        Assert.Equal(1, tunnel.DisposeCalls);
    }

    [Fact]
    public async Task UnsupportedEngineFailsBeforeSecretOrTunnelAccess()
    {
        var profile = PasswordProfile(DatabaseEngineKind.PostgreSql);
        var secrets = new FakeSecretStore { Value = "db-secret" };
        var tunnel = new FakeTunnelService(LoopbackEndpoint(profile));
        var service = new DatabaseDiagnosticService(
            tunnel,
            secrets,
            [new CapturingAdapter(DatabaseEngineKind.Redis)],
            DatabaseDiagnosticOptions.Default);

        var result = await service.InspectAsync(profile, TestContext.Current.CancellationToken);

        Assert.Equal(DatabaseDiagnosticFailureKind.UnsupportedEngine, result.Failure!.Kind);
        Assert.Equal(0, secrets.GetCalls);
        Assert.Equal(0, tunnel.OpenCalls);
    }

    [Fact]
    public async Task UnexpectedAdapterExceptionDoesNotLeakSecret()
    {
        var profile = PasswordProfile(DatabaseEngineKind.PostgreSql);
        var secrets = new FakeSecretStore { Value = "db-secret-never-display" };
        var tunnel = new FakeTunnelService(LoopbackEndpoint(profile));
        var adapter = new CapturingAdapter(DatabaseEngineKind.PostgreSql) { ThrowOnInspect = true };
        var service = new DatabaseDiagnosticService(tunnel, secrets, [adapter], DatabaseDiagnosticOptions.Default);

        var result = await service.InspectAsync(profile, TestContext.Current.CancellationToken);

        Assert.Equal(DatabaseDiagnosticFailureKind.Unexpected, result.Failure!.Kind);
        Assert.DoesNotContain("db-secret-never-display", result.Failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, tunnel.DisposeCalls);
    }

    [Fact]
    public void DuplicateEngineAdaptersAreRejected()
    {
        var profile = NoPasswordProfile(DatabaseEngineKind.Redis);
        var tunnel = new FakeTunnelService(LoopbackEndpoint(profile));

        Assert.Throws<ArgumentException>(() => new DatabaseDiagnosticService(
            tunnel,
            new FakeSecretStore(),
            [new CapturingAdapter(DatabaseEngineKind.Redis), new CapturingAdapter(DatabaseEngineKind.Redis)],
            DatabaseDiagnosticOptions.Default));
    }

    [Fact]
    public async Task NoPasswordProfileNeverReadsSecretStore()
    {
        var profile = NoPasswordProfile(DatabaseEngineKind.Redis);
        var secrets = new FakeSecretStore { ThrowOnGet = true };
        var tunnel = new FakeTunnelService(LoopbackEndpoint(profile));
        var adapter = new CapturingAdapter(DatabaseEngineKind.Redis);
        var service = new DatabaseDiagnosticService(tunnel, secrets, [adapter], DatabaseDiagnosticOptions.Default);

        var result = await service.InspectAsync(profile, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Failure?.Message);
        Assert.Equal(0, secrets.GetCalls);
        Assert.Null(adapter.LastRequest!.Secret);
    }

    private static DatabaseTunnelEndpoint LoopbackEndpoint(DatabaseConnectionProfile profile) =>
        new(profile.Id, "127.0.0.1", 49155, profile.RemoteHost, profile.RemotePort);

    private static DatabaseConnectionProfile PasswordProfile(DatabaseEngineKind engine)
    {
        var id = Guid.NewGuid();
        return DatabaseConnectionProfile.Create(
            id,
            Guid.NewGuid(),
            $"{engine} profile",
            engine,
            "203.0.113.88",
            DatabaseConnectionProfile.DefaultPortFor(engine),
            engine == DatabaseEngineKind.Redis ? null : "appdb",
            engine == DatabaseEngineKind.Redis ? null : "appuser",
            DatabaseAuthenticationKind.Password,
            SecretReference.ForDatabaseProfile(id));
    }

    private static DatabaseConnectionProfile NoPasswordProfile(DatabaseEngineKind engine) =>
        DatabaseConnectionProfile.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            $"{engine} profile",
            engine,
            "203.0.113.99",
            DatabaseConnectionProfile.DefaultPortFor(engine),
            engine == DatabaseEngineKind.Redis ? null : "appdb",
            null,
            DatabaseAuthenticationKind.None,
            null);

    private sealed class CapturingAdapter : IDatabaseEngineDiagnosticAdapter
    {
        public CapturingAdapter(DatabaseEngineKind engine) => Engine = engine;

        public DatabaseEngineKind Engine { get; }
        public DatabaseEngineDiagnosticRequest? LastRequest { get; private set; }
        public int Calls { get; private set; }
        public bool ThrowOnInspect { get; init; }

        public Task<DatabaseDiagnosticResult> InspectAsync(
            DatabaseEngineDiagnosticRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            LastRequest = request;
            if (ThrowOnInspect)
            {
                throw new InvalidOperationException("fixture adapter failure contains no safe detail");
            }

            return Task.FromResult(DatabaseDiagnosticResult.Success(new DatabaseDiagnosticSnapshot(
                Engine,
                "fixture-version",
                Engine.ToString(),
                "fixture-identity",
                [],
                [],
                [],
                false,
                DateTimeOffset.UtcNow)));
        }
    }

    private sealed class FakeTunnelService : IDatabaseTunnelService
    {
        private readonly DatabaseTunnelEndpoint _endpoint;

        public FakeTunnelService(DatabaseTunnelEndpoint endpoint) => _endpoint = endpoint;

        public int OpenCalls { get; private set; }
        public int DisposeCalls { get; private set; }

        public ValueTask<IDatabaseTunnelLease> OpenAsync(
            DatabaseConnectionProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenCalls++;
            return ValueTask.FromResult<IDatabaseTunnelLease>(new Lease(_endpoint, this));
        }

        private sealed class Lease : IDatabaseTunnelLease
        {
            private FakeTunnelService? _owner;

            public Lease(DatabaseTunnelEndpoint endpoint, FakeTunnelService owner)
            {
                Endpoint = endpoint;
                _owner = owner;
            }

            public DatabaseTunnelEndpoint Endpoint { get; }

            public ValueTask DisposeAsync()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner is not null)
                {
                    owner.DisposeCalls++;
                }

                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed class FakeSecretStore : ISecretStore
    {
        public string? Value { get; init; }
        public bool ThrowOnGet { get; init; }
        public int GetCalls { get; private set; }

        public ValueTask SetAsync(
            SecretReference reference,
            string secret,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
            if (ThrowOnGet)
            {
                throw new InvalidOperationException("fixture-secret-store-error");
            }

            return ValueTask.FromResult(Value);
        }

        public ValueTask DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
