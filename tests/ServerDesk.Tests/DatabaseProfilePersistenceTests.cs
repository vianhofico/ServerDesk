using Microsoft.Data.Sqlite;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.Databases;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseProfilePersistenceTests
{
    [Fact]
    public async Task DatabaseProfileRoundTripPersistsMetadataAndSecretReferenceOnly()
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
        var id = Guid.NewGuid();
        var reference = SecretReference.ForDatabaseProfile(id);
        var profile = DatabaseConnectionProfile.Create(
            id,
            server.Id,
            "Production PostgreSQL",
            DatabaseEngineKind.PostgreSql,
            "127.0.0.1",
            5432,
            "appdb",
            "appuser",
            DatabaseAuthenticationKind.Password,
            reference);

        await databaseRepository.UpsertAsync(profile, cancellationToken);
        var loaded = await databaseRepository.GetAsync(profile.Id, cancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(profile.Id, loaded.Id);
        Assert.Equal(profile.ServerProfileId, loaded.ServerProfileId);
        Assert.Equal(profile.Name, loaded.Name);
        Assert.Equal(profile.Engine, loaded.Engine);
        Assert.Equal(profile.RemoteHost, loaded.RemoteHost);
        Assert.Equal(profile.RemotePort, loaded.RemotePort);
        Assert.Equal(profile.DatabaseName, loaded.DatabaseName);
        Assert.Equal(profile.Username, loaded.Username);
        Assert.Equal(profile.AuthenticationKind, loaded.AuthenticationKind);
        Assert.Equal(reference, loaded.CredentialReference);
        Assert.Equal(
            SqliteDatabaseInitializer.CurrentSchemaVersion,
            await initializer.GetSchemaVersionAsync(cancellationToken));
    }

    [Fact]
    public async Task DatabaseProfileSchemaHasNoRawSecretOrConnectionStringColumns()
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

        Assert.Contains("credential_reference", columns);
        Assert.Contains("authentication_kind", columns);
        Assert.Contains("remote_host", columns);
        Assert.Contains("remote_port", columns);
        Assert.DoesNotContain(columns, column => column.Contains("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("token", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("connection_string", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("connectionstring", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DeletingServerWithDatabaseProfilesFailsClosedInsteadOfOrphaningSecrets()
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
        var id = Guid.NewGuid();
        await databaseRepository.UpsertAsync(
            DatabaseConnectionProfile.Create(
                id,
                server.Id,
                "Redis",
                DatabaseEngineKind.Redis,
                "127.0.0.1",
                6379,
                null,
                null,
                DatabaseAuthenticationKind.None,
                null),
            cancellationToken);

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await serverRepository.DeleteAsync(server.Id, cancellationToken));

        Assert.NotNull(await serverRepository.GetAsync(server.Id, cancellationToken));
        Assert.Single(await databaseRepository.ListForServerAsync(server.Id, cancellationToken));
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
