using Microsoft.Data.Sqlite;
using ServerDesk.Application.Abstractions;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ServerDesk.Tests;

public sealed class PortForwardingTests
{
    [Fact]
    public void ProfileRejectsUnsafeOrInvalidEndpoints()
    {
        var serverId = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() => PortForwardProfile.Create(
            serverId,
            "Unsafe wildcard",
            PortForwardKind.Local,
            "0.0.0.0",
            5432,
            "127.0.0.1",
            5432));
        Assert.Throws<ArgumentException>(() => PortForwardProfile.Create(
            serverId,
            "SOCKS with destination",
            PortForwardKind.Dynamic,
            "127.0.0.1",
            1080,
            "example.com",
            443));
        Assert.Throws<ArgumentOutOfRangeException>(() => PortForwardProfile.Create(
            serverId,
            "Missing destination port",
            PortForwardKind.Local,
            "127.0.0.1",
            5432,
            "127.0.0.1",
            null));
    }

    [Fact]
    public void CollisionRulesMatchWhereListenerActuallyLives()
    {
        var firstServer = Guid.NewGuid();
        var secondServer = Guid.NewGuid();
        var local = PortForwardProfile.Create(
            firstServer,
            "Local DB",
            PortForwardKind.Local,
            "127.0.0.1",
            15432,
            "127.0.0.1",
            5432);
        var socksOnOtherServer = PortForwardProfile.Create(
            secondServer,
            "SOCKS",
            PortForwardKind.Dynamic,
            "127.0.0.1",
            15432);
        var remoteSameServer = PortForwardProfile.Create(
            firstServer,
            "Remote A",
            PortForwardKind.Remote,
            "127.0.0.1",
            19090,
            "127.0.0.1",
            8080);
        var remoteOtherServer = PortForwardProfile.Create(
            secondServer,
            "Remote B",
            PortForwardKind.Remote,
            "127.0.0.1",
            19090,
            "127.0.0.1",
            8080);
        var automatic = PortForwardProfile.Create(
            firstServer,
            "Automatic",
            PortForwardKind.Local,
            "127.0.0.1",
            0,
            "127.0.0.1",
            5432);

        Assert.True(local.ConflictsWith(socksOnOtherServer));
        Assert.False(remoteSameServer.ConflictsWith(remoteOtherServer));
        Assert.False(local.ConflictsWith(automatic));
    }

    [Fact]
    public async Task SqliteRoundTripStoresOnlyForwardMetadataAndCascadesWithServer()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);

        var serverRepository = new SqliteProfileRepository(factory);
        var forwardRepository = new SqlitePortForwardProfileRepository(factory);
        var server = ServerProfile.Create("DB host", "127.0.0.1", 22, "dev");
        await serverRepository.UpsertAsync(server, cancellationToken);
        var forward = PortForwardProfile.Create(
            server.Id,
            "PostgreSQL",
            PortForwardKind.Local,
            "127.0.0.1",
            15432,
            "127.0.0.1",
            5432);

        await forwardRepository.UpsertAsync(forward, cancellationToken);
        var loaded = await forwardRepository.GetAsync(forward.Id, cancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(forward.Id, loaded.Id);
        Assert.Equal(forward.ServerProfileId, loaded.ServerProfileId);
        Assert.Equal(forward.Name, loaded.Name);
        Assert.Equal(forward.Kind, loaded.Kind);
        Assert.Equal(forward.BindHost, loaded.BindHost);
        Assert.Equal(forward.BindPort, loaded.BindPort);
        Assert.Equal(forward.DestinationHost, loaded.DestinationHost);
        Assert.Equal(forward.DestinationPort, loaded.DestinationPort);
        Assert.Equal(
            SqliteDatabaseInitializer.CurrentSchemaVersion,
            await initializer.GetSchemaVersionAsync(cancellationToken));

        await using (var connection = factory.Create())
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(port_forward_profiles);";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var columns = new List<string>();
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }

            Assert.DoesNotContain(columns, column => column.Contains("password", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(columns, column => column.Contains("secret", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(columns, column => column.Contains("credential", StringComparison.OrdinalIgnoreCase));
        }

        await serverRepository.DeleteAsync(server.Id, cancellationToken);
        Assert.Null(await forwardRepository.GetAsync(forward.Id, cancellationToken));
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
