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
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);

        var repository = new SqliteProfileRepository(factory);
        var reference = SecretReference.Create("ssh-key-passphrase");
        var profile = ServerProfile.Create(
            "Production",
            "prod.example.com",
            22,
            "deploy",
            "production",
            reference,
            ServerAuthenticationKind.PrivateKey,
            @"C:\Users\dev\.ssh\id_ed25519");

        await repository.UpsertAsync(profile, cancellationToken);
        var loaded = await repository.GetAsync(profile.Id, cancellationToken);

        Assert.NotNull(loaded);
        Assert.Equal(profile, loaded);
        Assert.Equal(
            SqliteDatabaseInitializer.CurrentSchemaVersion,
            await initializer.GetSchemaVersionAsync(cancellationToken));
    }

    [Fact]
    public async Task SqliteSchemaContainsNoRawSecretColumns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);

        await using var connection = factory.Create();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA table_info(server_profiles);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var columns = new List<string>();
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(1));
        }

        Assert.Contains("credential_reference", columns);
        Assert.Contains("authentication_kind", columns);
        Assert.Contains("private_key_path", columns);
        Assert.DoesNotContain(columns, column => column.Equals("password", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("passphrase", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("private_key_secret", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(columns, column => column.Contains("sudo_secret", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task OperationAuditRoundTripKeepsSafeSummaryData()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);

        var audit = new SqliteOperationAudit(factory);
        var entry = OperationAuditEntry.Create(
            "foundation",
            "Initialized local metadata store",
            OperationRisk.ReadOnly,
            OperationOutcome.Succeeded,
            "local");

        await audit.AppendAsync(entry, cancellationToken);
        var recent = await audit.ListRecentAsync(10, cancellationToken);

        var loaded = Assert.Single(recent);
        Assert.Equal(entry, loaded);
    }

    [Fact]
    public async Task JsonSettingsRoundTripPersistsThemeAndStableLanguageCode()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var store = new JsonAppSettingsStore(paths);

        await store.SaveAsync(
            new AppSettings(AppThemePreference.Dark, AppLanguagePreference.Vietnamese),
            cancellationToken);
        var loaded = await store.LoadAsync(cancellationToken);
        var json = await File.ReadAllTextAsync(paths.SettingsFilePath, cancellationToken);

        Assert.Equal(AppThemePreference.Dark, loaded.ThemePreference);
        Assert.Equal(AppLanguagePreference.Vietnamese, loaded.LanguagePreference);
        Assert.Contains("\"languagePreference\": \"vi\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("Tiếng Việt", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JsonSettingsWithoutLanguageRemainBackwardCompatible()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        await File.WriteAllTextAsync(
            paths.SettingsFilePath,
            "{\"themePreference\":2}",
            cancellationToken);
        var store = new JsonAppSettingsStore(paths);

        var loaded = await store.LoadAsync(cancellationToken);

        Assert.Equal(AppThemePreference.Dark, loaded.ThemePreference);
        Assert.Equal(AppLanguagePreference.System, loaded.LanguagePreference);
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
