using Microsoft.Data.Sqlite;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ServerDesk.Tests;

public sealed class PortForwardManagerTests
{
    [Fact]
    public async Task SaveRejectsLocalListenerCollisionAcrossDifferentServers()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var connectionFactory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(connectionFactory);
        await initializer.InitializeAsync(cancellationToken);
        var servers = new SqliteProfileRepository(connectionFactory);
        var forwards = new SqlitePortForwardProfileRepository(connectionFactory);
        var firstServer = ServerProfile.Create("First", "127.0.0.1", 22, "dev");
        var secondServer = ServerProfile.Create("Second", "127.0.0.1", 2222, "dev");
        await servers.UpsertAsync(firstServer, cancellationToken);
        await servers.UpsertAsync(secondServer, cancellationToken);
        await using var manager = new PortForwardManager(forwards, servers, new UnusedSessionFactory());

        await manager.SaveProfileAsync(
            PortForwardProfile.Create(
                firstServer.Id,
                "PostgreSQL A",
                PortForwardKind.Local,
                "127.0.0.1",
                15432,
                "127.0.0.1",
                5432),
            cancellationToken);

        var exception = await Assert.ThrowsAsync<PortForwardSessionException>(() =>
            manager.SaveProfileAsync(
                    PortForwardProfile.Create(
                        secondServer.Id,
                        "PostgreSQL B",
                        PortForwardKind.Dynamic,
                        "127.0.0.1",
                        15432),
                    cancellationToken)
                .AsTask());

        Assert.Equal(RemoteErrorCode.PortInUse, exception.Error.Code);
        Assert.Single(await forwards.ListAsync(cancellationToken));
    }

    [Fact]
    public async Task AutomaticLocalPortsCanBeSavedTogetherBecauseOsChoosesActualPorts()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var connectionFactory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(connectionFactory);
        await initializer.InitializeAsync(cancellationToken);
        var servers = new SqliteProfileRepository(connectionFactory);
        var forwards = new SqlitePortForwardProfileRepository(connectionFactory);
        var server = ServerProfile.Create("Auto", "127.0.0.1", 22, "dev");
        await servers.UpsertAsync(server, cancellationToken);
        await using var manager = new PortForwardManager(forwards, servers, new UnusedSessionFactory());

        await manager.SaveProfileAsync(
            PortForwardProfile.Create(
                server.Id,
                "Auto A",
                PortForwardKind.Local,
                "127.0.0.1",
                0,
                "127.0.0.1",
                5432),
            cancellationToken);
        await manager.SaveProfileAsync(
            PortForwardProfile.Create(
                server.Id,
                "Auto B",
                PortForwardKind.Dynamic,
                "127.0.0.1",
                0),
            cancellationToken);

        Assert.Equal(2, (await forwards.ListAsync(cancellationToken)).Count);
    }

    private sealed class UnusedSessionFactory : IPortForwardSessionFactory
    {
        public IPortForwardSession Create(ServerProfile serverProfile, PortForwardProfile forwardProfile) =>
            throw new InvalidOperationException("Save-profile tests must not create runtime forwarding sessions.");
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
