using Microsoft.Data.Sqlite;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.Routing;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ConnectionRoutingTests
{
    [Fact]
    public void DirectRouteContainsNoProxyOrBastionMetadata()
    {
        var route = ServerConnectionRoute.Direct(Guid.NewGuid());

        Assert.Equal(ServerConnectionRouteKind.Direct, route.Kind);
        Assert.Null(route.ProxyHost);
        Assert.Null(route.ProxyPort);
        Assert.Null(route.ProxyUsername);
        Assert.Null(route.ProxyCredentialReference);
        Assert.Null(route.BastionProfileId);
    }

    [Fact]
    public void BastionRouteRejectsSelfReference()
    {
        var id = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => ServerConnectionRoute.Bastion(id, id));
    }

    [Fact]
    public async Task ProxyRouteRoundTripsThroughCurrentSchemaWithoutSecretValue()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);
        var profileRepository = new SqliteProfileRepository(factory);
        var routeRepository = new SqliteConnectionRouteRepository(factory);
        var profile = ServerProfile.Create("Proxy target", "target.internal", 22, "deploy");
        await profileRepository.UpsertAsync(profile, cancellationToken);
        var reference = SecretReference.ForProxyRoute(profile.Id);
        var route = ServerConnectionRoute.Proxy(
            profile.Id,
            ServerConnectionRouteKind.Socks5Proxy,
            "proxy.internal",
            1080,
            "proxy-user",
            reference);

        await routeRepository.UpsertAsync(route, cancellationToken);
        var loaded = await routeRepository.GetAsync(profile.Id, cancellationToken);

        Assert.Equal(
            SqliteDatabaseInitializer.CurrentSchemaVersion,
            await initializer.GetSchemaVersionAsync(cancellationToken));
        Assert.Equal(route, loaded);

        await using var connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT proxy_credential_reference FROM server_connection_routes WHERE server_profile_id = @id;";
        command.Parameters.AddWithValue("@id", profile.Id.ToString("D"));
        var storedReference = (string?)await command.ExecuteScalarAsync(cancellationToken);
        Assert.Equal(reference.Value, storedReference);
        Assert.DoesNotContain("proxy-password", storedReference ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingServerCascadesRouteMetadata()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);
        var profileRepository = new SqliteProfileRepository(factory);
        var routeRepository = new SqliteConnectionRouteRepository(factory);
        var profile = ServerProfile.Create("Target", "target.internal", 22, "deploy");
        await profileRepository.UpsertAsync(profile, cancellationToken);
        await routeRepository.UpsertAsync(
            ServerConnectionRoute.Proxy(
                profile.Id,
                ServerConnectionRouteKind.HttpProxy,
                "proxy.internal",
                8080),
            cancellationToken);

        await profileRepository.DeleteAsync(profile.Id, cancellationToken);

        Assert.Null(await routeRepository.GetAsync(profile.Id, cancellationToken));
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
