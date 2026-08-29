using Microsoft.Data.Sqlite;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.Databases;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseBackupPersistenceTests
{
    [Fact]
    public async Task VerifiedManifestRoundTripsWithoutSecretBearingColumns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);
        var server = ServerProfile.Create("Backup host", "db.example.invalid", 22, "operator");
        await new SqliteProfileRepository(factory).UpsertAsync(server, cancellationToken);
        var repository = new SqliteDatabaseBackupManifestRepository(factory);
        var manifest = VerifiedManifest(server.Id);

        await repository.AddAsync(manifest, cancellationToken);
        var loaded = await repository.GetAsync(manifest.BackupId, cancellationToken);
        var history = await repository.ListForServerAsync(server.Id, cancellationToken);

        Assert.Equal(manifest, loaded);
        Assert.Equal(manifest, Assert.Single(history));

        await using var connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(database_backup_manifests);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("sha256", columns);
        Assert.Contains("structural_check", columns);
        Assert.Contains("is_verified", columns);
        Assert.DoesNotContain(columns, column => column.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("credential", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("connection_string", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnverifiedManifestIsRejectedBeforePersistence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);
        var server = ServerProfile.Create("Backup host", "db.example.invalid", 22, "operator");
        await new SqliteProfileRepository(factory).UpsertAsync(server, cancellationToken);
        var repository = new SqliteDatabaseBackupManifestRepository(factory);
        var unverified = VerifiedManifest(server.Id) with { IsVerified = false };

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await repository.AddAsync(unverified, cancellationToken));
        Assert.Empty(await repository.ListForServerAsync(server.Id, cancellationToken));
    }

    private static DatabaseBackupManifest VerifiedManifest(Guid serverProfileId) =>
        new(
            Guid.NewGuid(),
            serverProfileId,
            Guid.NewGuid(),
            DatabaseEngineKind.PostgreSql,
            "appdb",
            "appuser",
            RemotePath.Parse("/var/backups/serverdesk/app.dump"),
            DatabaseBackupFormat.PostgreSqlCustom,
            "pg_dump",
            "pg_dump (PostgreSQL) 18.6",
            DateTimeOffset.UtcNow.AddSeconds(-1),
            new DatabaseBackupVerificationEvidence(
                4096,
                new string('A', 64),
                "pg_restore --list parsed the PostgreSQL custom archive successfully",
                DateTimeOffset.UtcNow),
            true);

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
