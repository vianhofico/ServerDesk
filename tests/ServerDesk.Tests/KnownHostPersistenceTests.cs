using Microsoft.Data.Sqlite;
using ServerDesk.Application.Abstractions;
using ServerDesk.Domain.Security;
using ServerDesk.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ServerDesk.Tests;

public sealed class KnownHostPersistenceTests
{
    [Fact]
    public async Task KnownHostRoundTripSurvivesCurrentSchema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);
        var repository = new SqliteKnownHostRepository(factory);
        var observation = HostKeyObservation.Create(
            "EXAMPLE.com",
            22,
            "ssh-ed25519",
            HostKeyFingerprint.FromHostKey("host-key"u8));
        var record = KnownHostRecord.Trust(observation, DateTimeOffset.Parse("2026-08-27T00:00:00Z"));

        await repository.UpsertAsync(record, cancellationToken);
        var loaded = await repository.ListForEndpointAsync("example.COM", 22, cancellationToken);

        Assert.Equal(SqliteDatabaseInitializer.CurrentSchemaVersion, await initializer.GetSchemaVersionAsync(cancellationToken));
        Assert.Equal(record, Assert.Single(loaded));
    }

    [Fact]
    public async Task DeleteEndpointRemovesAllAlgorithmsForThatEndpoint()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);
        var repository = new SqliteKnownHostRepository(factory);

        await repository.UpsertAsync(
            KnownHostRecord.Trust(HostKeyObservation.Create(
                "example.com",
                22,
                "ssh-ed25519",
                HostKeyFingerprint.FromHostKey("key-a"u8))),
            cancellationToken);
        await repository.UpsertAsync(
            KnownHostRecord.Trust(HostKeyObservation.Create(
                "example.com",
                22,
                "rsa-sha2-512",
                HostKeyFingerprint.FromHostKey("key-b"u8))),
            cancellationToken);

        await repository.DeleteEndpointAsync("EXAMPLE.COM", 22, cancellationToken);

        Assert.Empty(await repository.ListForEndpointAsync("example.com", 22, cancellationToken));
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
