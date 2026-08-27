using Microsoft.Data.Sqlite;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.History;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Routing;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ProfileOrganizationHistoryTests
{
    [Fact]
    public async Task OrganizationSurvivesRestartAndSearchesNameHostGroupTagAndEnvironment()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);

        var profiles = new SqliteProfileRepository(factory);
        var organizationRepository = new SqliteServerProfileOrganizationRepository(factory);
        var production = ServerProfile.Create("API production", "api.internal", 22, "deploy", "production");
        var staging = ServerProfile.Create("Worker staging", "worker.internal", 22, "ops", "staging");
        await profiles.UpsertAsync(production, cancellationToken);
        await profiles.UpsertAsync(staging, cancellationToken);

        var service = new ServerProfileOrganizationService(profiles, organizationRepository);
        await service.SaveAsync(production.Id, "Core", "api, critical", true, cancellationToken);
        await service.SaveAsync(staging.Id, "Jobs", "worker, batch", false, cancellationToken);

        var restartedService = new ServerProfileOrganizationService(
            new SqliteProfileRepository(factory),
            new SqliteServerProfileOrganizationRepository(factory));
        var loaded = await restartedService.GetAsync(production.Id, cancellationToken);
        Assert.Equal("Core", loaded.GroupName);
        Assert.Equal(["api", "critical"], loaded.Tags);
        Assert.True(loaded.IsFavorite);

        Assert.Single(await restartedService.SearchAsync(new ServerProfileSearchFilter(Query: "api.internal"), cancellationToken));
        Assert.Single(await restartedService.SearchAsync(new ServerProfileSearchFilter(GroupName: "core"), cancellationToken));
        Assert.Single(await restartedService.SearchAsync(new ServerProfileSearchFilter(Tag: "CRITICAL"), cancellationToken));
        Assert.Single(await restartedService.SearchAsync(new ServerProfileSearchFilter(Environment: "production"), cancellationToken));
        Assert.Single(await restartedService.SearchAsync(new ServerProfileSearchFilter(FavoritesOnly: true), cancellationToken));

        await using var connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(server_profile_organization);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        Assert.DoesNotContain(columns, name => name.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, name => name.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, name => name.Contains("credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task HistoryIsBoundedAndRetainedAsSnapshotAfterServerDelete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);
        var profiles = new SqliteProfileRepository(factory);
        var history = new SqliteConnectionHistoryRepository(factory);
        var profile = ServerProfile.Create("Audit target", "audit.internal", 22, "deploy", "production");
        await profiles.UpsertAsync(profile, cancellationToken);

        var started = DateTimeOffset.UtcNow.AddMinutes(-10);
        for (var index = 0; index < ConnectionHistoryPolicy.MaxEntries + 5; index++)
        {
            var attemptStarted = started.AddMilliseconds(index);
            await history.AppendAsync(
                new ConnectionHistoryEntry(
                    Guid.NewGuid(),
                    profile.Id,
                    profile.Name,
                    $"{profile.Username}@{profile.Host}:{profile.Port}",
                    "Direct",
                    attemptStarted,
                    attemptStarted.AddMilliseconds(20),
                    ConnectionAttemptOutcome.Connected,
                    null),
                cancellationToken);
        }

        var bounded = await history.ListRecentAsync(ConnectionHistoryPolicy.MaxEntries, cancellationToken);
        Assert.Equal(ConnectionHistoryPolicy.MaxEntries, bounded.Count);

        await profiles.DeleteAsync(profile.Id, cancellationToken);
        var retained = await history.ListRecentAsync(ConnectionHistoryPolicy.MaxEntries, cancellationToken);
        Assert.All(retained, entry => Assert.Null(entry.ServerProfileId));
        Assert.All(retained, entry => Assert.Equal("Audit target", entry.ProfileName));
        Assert.All(retained, entry => Assert.Equal("deploy@audit.internal:22", entry.Endpoint));
    }

    [Theory]
    [InlineData(null, false, ConnectionAttemptOutcome.Connected)]
    [InlineData(null, true, ConnectionAttemptOutcome.Cancelled)]
    [InlineData(RemoteErrorCode.AuthenticationFailed, false, ConnectionAttemptOutcome.AuthenticationFailed)]
    [InlineData(RemoteErrorCode.HostKeyUnknown, false, ConnectionAttemptOutcome.HostTrustFailed)]
    [InlineData(RemoteErrorCode.ConnectionFailed, false, ConnectionAttemptOutcome.NetworkFailed)]
    public async Task SessionDecoratorRecordsExplicitSecretSafeOutcomes(
        RemoteErrorCode? failureCode,
        bool cancel,
        ConnectionAttemptOutcome expectedOutcome)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("History target", "target.internal", 22, "deploy", "production");
        var proxyReference = SecretReference.ForProxyRoute(profile.Id);
        var route = ServerConnectionRoute.Proxy(
            profile.Id,
            ServerConnectionRouteKind.Socks5Proxy,
            "proxy.internal",
            1080,
            "proxy-user",
            proxyReference);
        var history = new InMemoryConnectionHistoryRepository();
        var profileRepository = new InMemoryProfileRepository(profile);
        var routeRepository = new InMemoryRouteRepository(route);
        var factory = new ConnectionHistoryRemoteSessionFactory(
            new FakeRemoteSessionFactory(failureCode, cancel),
            history,
            routeRepository,
            profileRepository);
        await using var session = factory.Create(profile);

        if (cancel)
        {
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await session.ConnectAsync(cancellationToken));
        }
        else if (failureCode is not null)
        {
            await Assert.ThrowsAsync<RemoteSessionException>(async () =>
                await session.ConnectAsync(cancellationToken));
        }
        else
        {
            await session.ConnectAsync(cancellationToken);
        }

        var entry = Assert.Single(history.Entries);
        Assert.Equal(expectedOutcome, entry.Outcome);
        Assert.Equal(cancel ? RemoteErrorCode.OperationCancelled : failureCode, entry.FailureCode);
        Assert.Equal("SOCKS5 proxy via proxy.internal:1080", entry.RouteSummary);
        Assert.DoesNotContain("proxy-user", entry.RouteSummary, StringComparison.Ordinal);
        Assert.DoesNotContain(proxyReference.Value, entry.RouteSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingProfileRemovesPrimaryAndProxySecrets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profiles = new InMemoryProfileRepository();
        var secrets = new InMemorySecretStore();
        var routes = new InMemoryRouteRepository();
        var service = new ServerProfileService(profiles, secrets, routes);
        var profile = await service.CreateAsync(
            new ServerProfileSpec(
                "Production",
                "server.internal",
                22,
                "deploy",
                "production",
                ServerAuthenticationKind.Password,
                null),
            "server-password",
            cancellationToken);
        var proxyReference = SecretReference.ForProxyRoute(profile.Id);
        await secrets.SetAsync(proxyReference, "proxy-password", cancellationToken);
        await routes.UpsertAsync(
            ServerConnectionRoute.Proxy(
                profile.Id,
                ServerConnectionRouteKind.HttpProxy,
                "proxy.internal",
                8080,
                "proxy-user",
                proxyReference),
            cancellationToken);

        await service.DeleteAsync(profile.Id, cancellationToken);

        Assert.Null(await secrets.GetAsync(SecretReference.ForServerProfile(profile.Id), cancellationToken));
        Assert.Null(await secrets.GetAsync(proxyReference, cancellationToken));
        Assert.Null(await profiles.GetAsync(profile.Id, cancellationToken));
    }

    private sealed class FakeRemoteSessionFactory : IRemoteSessionFactory
    {
        private readonly RemoteErrorCode? _failureCode;
        private readonly bool _cancel;

        public FakeRemoteSessionFactory(RemoteErrorCode? failureCode, bool cancel)
        {
            _failureCode = failureCode;
            _cancel = cancel;
        }

        public IRemoteSession Create(ServerProfile profile) => new FakeRemoteSession(profile.Id, _failureCode, _cancel);
    }

    private sealed class FakeRemoteSession : IRemoteSession
    {
        private readonly RemoteErrorCode? _failureCode;
        private readonly bool _cancel;

        public FakeRemoteSession(Guid serverProfileId, RemoteErrorCode? failureCode, bool cancel)
        {
            ServerProfileId = serverProfileId;
            _failureCode = failureCode;
            _cancel = cancel;
        }

        public Guid ServerProfileId { get; }

        public RemoteSessionState State { get; private set; } = RemoteSessionState.Created;

        public RemoteError? LastError { get; private set; }

        public string? ServerVersion => null;

        public DateTimeOffset? ConnectedAtUtc { get; private set; }

        public event Action<RemoteSessionState>? StateChanged
        {
            add { }
            remove { }
        }

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            if (_cancel)
            {
                State = RemoteSessionState.Disconnected;
                throw new OperationCanceledException(cancellationToken);
            }

            if (_failureCode is not null)
            {
                LastError = new RemoteError(_failureCode.Value, "synthetic failure");
                State = RemoteSessionState.Faulted;
                throw new RemoteSessionException(LastError);
            }

            State = RemoteSessionState.Connected;
            ConnectedAtUtc = DateTimeOffset.UtcNow;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            State = RemoteSessionState.Disconnected;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class InMemoryConnectionHistoryRepository : IConnectionHistoryRepository
    {
        public List<ConnectionHistoryEntry> Entries { get; } = [];

        public ValueTask AppendAsync(ConnectionHistoryEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<ConnectionHistoryEntry>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ConnectionHistoryEntry> result = Entries.TakeLast(limit).Reverse().ToArray();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InMemoryRouteRepository : IConnectionRouteRepository
    {
        private readonly Dictionary<Guid, ServerConnectionRoute> _routes = [];

        public InMemoryRouteRepository(ServerConnectionRoute? route = null)
        {
            if (route is not null)
            {
                _routes[route.ServerProfileId] = route;
            }
        }

        public ValueTask<ServerConnectionRoute?> GetAsync(Guid serverProfileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _routes.TryGetValue(serverProfileId, out var route);
            return ValueTask.FromResult(route);
        }

        public ValueTask UpsertAsync(ServerConnectionRoute route, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _routes[route.ServerProfileId] = route;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(Guid serverProfileId, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _routes.Remove(serverProfileId);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryProfileRepository : IProfileRepository
    {
        private readonly Dictionary<Guid, ServerProfile> _profiles = [];

        public InMemoryProfileRepository(params ServerProfile[] profiles)
        {
            foreach (var profile in profiles)
            {
                _profiles[profile.Id] = profile;
            }
        }

        public ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ServerProfile> result = _profiles.Values.ToArray();
            return ValueTask.FromResult(result);
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

    private sealed class InMemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);

        public ValueTask SetAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _secrets[reference.Value] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> GetAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_secrets.TryGetValue(reference.Value, out var secret) ? secret : null);
        }

        public ValueTask DeleteAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _secrets.Remove(reference.Value);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TemporaryAppPaths : IAppPaths, IDisposable
    {
        public TemporaryAppPaths()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), "ServerDesk.Tests", Guid.NewGuid().ToString("N"));
            DataDirectory = Path.Combine(RootDirectory, "data");
            LogsDirectory = Path.Combine(RootDirectory, "logs");
            SettingsFilePath = Path.Combine(RootDirectory, "settings.json");
            DatabaseFilePath = Path.Combine(DataDirectory, "serverdesk.db");
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogsDirectory);
        }

        public string RootDirectory { get; }

        public string DataDirectory { get; }

        public string LogsDirectory { get; }

        public string SettingsFilePath { get; }

        public string DatabaseFilePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, true);
            }
        }
    }
}
