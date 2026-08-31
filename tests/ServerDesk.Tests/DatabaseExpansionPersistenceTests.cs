using Microsoft.Data.Sqlite;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.Databases;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseExpansionPersistenceTests
{
    [Fact]
    public async Task SqlServerAndMongoDbProfilesRoundTripOnCurrentSchema()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);
        var serverRepository = new SqliteProfileRepository(factory);
        var databaseRepository = new SqliteDatabaseProfileRepository(factory);
        var server = ServerProfile.Create("Database host", "db.example.invalid", 22, "dev");
        await serverRepository.UpsertAsync(server, cancellationToken);

        var sqlServerId = Guid.NewGuid();
        var sqlServer = DatabaseConnectionProfile.Create(
            sqlServerId,
            server.Id,
            "SQL Server",
            DatabaseEngineKind.SqlServer,
            "127.0.0.1",
            1433,
            "appdb",
            "sa",
            DatabaseAuthenticationKind.Password,
            SecretReference.ForDatabaseProfile(sqlServerId));
        await databaseRepository.UpsertAsync(sqlServer, cancellationToken);

        var mongoId = Guid.NewGuid();
        var mongo = DatabaseConnectionProfile.Create(
            mongoId,
            server.Id,
            "MongoDB",
            DatabaseEngineKind.MongoDb,
            "127.0.0.1",
            27017,
            "appdb",
            "serverdesk",
            DatabaseAuthenticationKind.Password,
            SecretReference.ForDatabaseProfile(mongoId),
            "admin",
            DatabaseTlsMode.Required);
        await databaseRepository.UpsertAsync(mongo, cancellationToken);

        var loadedSqlServer = Assert.IsType<DatabaseConnectionProfile>(
            await databaseRepository.GetAsync(sqlServerId, cancellationToken));
        Assert.Equal(DatabaseEngineKind.SqlServer, loadedSqlServer.Engine);
        Assert.Null(loadedSqlServer.AuthenticationDatabase);
        Assert.Equal(DatabaseTlsMode.Disabled, loadedSqlServer.TlsMode);

        var loadedMongo = Assert.IsType<DatabaseConnectionProfile>(
            await databaseRepository.GetAsync(mongoId, cancellationToken));
        Assert.Equal(DatabaseEngineKind.MongoDb, loadedMongo.Engine);
        Assert.Equal("admin", loadedMongo.AuthenticationDatabase);
        Assert.Equal(DatabaseTlsMode.Required, loadedMongo.TlsMode);
        Assert.Equal(27017, DatabaseConnectionProfile.DefaultPortFor(DatabaseEngineKind.MongoDb));
        Assert.Equal(SqliteDatabaseInitializer.CurrentSchemaVersion, await initializer.GetSchemaVersionAsync(cancellationToken));
    }

    [Fact]
    public async Task SqlServerAndMongoDbVerifiedManifestsPersistWithExpandedEnumRanges()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);
        var serverRepository = new SqliteProfileRepository(factory);
        var server = ServerProfile.Create("Database host", "db.example.invalid", 22, "dev");
        await serverRepository.UpsertAsync(server, cancellationToken);
        var manifests = new SqliteDatabaseBackupManifestRepository(factory);
        var verifiedAt = DateTimeOffset.UtcNow;

        var sqlServer = Manifest(
            server.Id,
            Guid.NewGuid(),
            DatabaseEngineKind.SqlServer,
            DatabaseBackupFormat.SqlServerNative,
            "/tmp/serverdesk-sqlserver.bak",
            verifiedAt);
        var mongo = Manifest(
            server.Id,
            Guid.NewGuid(),
            DatabaseEngineKind.MongoDb,
            DatabaseBackupFormat.MongoDbArchive,
            "/tmp/serverdesk-mongodb.archive.gz",
            verifiedAt);

        await manifests.AddAsync(sqlServer, cancellationToken);
        await manifests.AddAsync(mongo, cancellationToken);

        Assert.Equal(DatabaseEngineKind.SqlServer, (await manifests.GetAsync(sqlServer.BackupId, cancellationToken))!.Engine);
        var loadedMongo = Assert.IsType<DatabaseBackupManifest>(await manifests.GetAsync(mongo.BackupId, cancellationToken));
        Assert.Equal(DatabaseEngineKind.MongoDb, loadedMongo.Engine);
        Assert.Equal(DatabaseBackupFormat.MongoDbArchive, loadedMongo.Format);
    }

    [Fact]
    public async Task DatabaseProfileSchemaStoresMongoOptionsButNoCredentialOrUriValues()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);

        await using var connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(database_profiles);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("authentication_database", columns);
        Assert.Contains("tls_mode", columns);
        Assert.Contains("credential_reference", columns);
        Assert.DoesNotContain(columns, column => column.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("uri", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("connection_string", StringComparison.OrdinalIgnoreCase));
    }

    private static DatabaseBackupManifest Manifest(
        Guid serverId,
        Guid profileId,
        DatabaseEngineKind engine,
        DatabaseBackupFormat format,
        string path,
        DateTimeOffset now) =>
        new(
            Guid.NewGuid(),
            serverId,
            profileId,
            engine,
            "appdb",
            "serverdesk",
            RemotePath.Parse(path),
            format,
            engine == DatabaseEngineKind.MongoDb ? "mongodump" : "sqlcmd",
            "test-version",
            now,
            new DatabaseBackupVerificationEvidence(
                1024,
                new string('A', 64),
                "verified fixture",
                now),
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
