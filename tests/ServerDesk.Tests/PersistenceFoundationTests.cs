using Microsoft.Data.Sqlite;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.Settings;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Persistence.Sqlite;
using ServerDesk.Platform.Windows;
using Xunit;

namespace ServerDesk.Tests;

public sealed class PersistenceFoundationTests
{
    [Fact]
    public async Task SqliteProfileRoundTripPersistsOnlyCredentialReference()
    {
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var repository = new SqliteProfileRepository(factory);
        var reference = SecretReference.Create("ssh-password");
        var profile = ServerProfile.Create(
            "Production",
            "prod.example.com",
            22,
            "deploy",
            "production",
            reference);

        await repository.UpsertAsync(profile);
        var loaded = await repository.GetAsync(profile.Id);

        Assert.NotNull(loaded);
        Assert.Equal(profile, loaded);
        Assert.Equal(SqliteDatabaseInitializer.CurrentSchemaVersion, await initializer.GetSchemaVersionAsync());
    }

    [Fact]
    public async Task SqliteSchemaContainsNoRawSecretColumns()
    {
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync();

        await using var connection = factory.Create();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(server_profiles);";
        await using var reader = await command.ExecuteReaderAsync();

        var columns = new List<string>();
        while (await reader.ReadAsync())
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("credential_reference", columns);
        Assert.False(columns.Any(column => column.Contains("password", StringComparison.OrdinalIgnoreCase)));
        Assert.False(columns.Any(column => column.Contains("passphrase", StringComparison.OrdinalIgnoreCase)));
        Assert.False(columns.Any(column => column.Contains("private_key", StringComparison.OrdinalIgnoreCase)));
        Assert.False(columns.Any(column => column.Contains("sudo_secret", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task OperationAuditRoundTripKeepsSafeSummaryData()
    {
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync();

        var audit = new SqliteOperationAudit(factory);
        var entry = OperationAuditEntry.Create(
            "foundation",
            "Initialized local metadata store",
            OperationRisk.ReadOnly,
            OperationOutcome.Succeeded,
            "local");

        await audit.AppendAsync(entry);
        var recent = await audit.ListRecentAsync(10);

        var loaded = Assert.Single(recent);
        Assert.Equal(entry, loaded);
    }

    [Fact]
    public async Task JsonSettingsRoundTripPersistsThemePreference()
    {
        using var paths = new TemporaryAppPaths();
        var store = new JsonAppSettingsStore(paths);

        await store.SaveAsync(new AppSettings(AppThemePreference.Dark));
        var loaded = await store.LoadAsync();

        Assert.Equal(AppThemePreference.Dark, loaded.ThemePreference);
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
