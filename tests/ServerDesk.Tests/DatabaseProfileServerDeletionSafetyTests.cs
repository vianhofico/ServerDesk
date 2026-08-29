using Microsoft.Data.Sqlite;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.Databases;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseProfileServerDeletionSafetyTests
{
    [Fact]
    public async Task ServerDeleteBlockedByDatabaseProfileRestoresSshSecretAndKeepsDatabaseSecret()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);

        var servers = new SqliteProfileRepository(factory);
        var databaseProfiles = new SqliteDatabaseProfileRepository(factory);
        var secrets = new MemorySecretStore();
        var serverService = new ServerProfileService(servers, secrets);
        var databaseService = new DatabaseProfileService(databaseProfiles, servers, secrets);

        var server = await serverService.CreateAsync(
            new ServerProfileSpec(
                "Database host",
                "db.example.invalid",
                22,
                "dev",
                "production",
                ServerAuthenticationKind.Password,
                null),
            "ssh-password",
            cancellationToken);
        var databaseProfile = await databaseService.CreateAsync(
            server.Id,
            new DatabaseProfileSpec(
                "PostgreSQL",
                DatabaseEngineKind.PostgreSql,
                "127.0.0.1",
                5432,
                "appdb",
                "appuser",
                DatabaseAuthenticationKind.Password),
            "database-password",
            cancellationToken);

        await Assert.ThrowsAsync<SqliteException>(async () =>
            await serverService.DeleteAsync(server.Id, cancellationToken));

        Assert.NotNull(await servers.GetAsync(server.Id, cancellationToken));
        Assert.NotNull(await databaseProfiles.GetAsync(databaseProfile.Id, cancellationToken));
        Assert.Equal(
            "ssh-password",
            await secrets.GetAsync(server.CredentialReference!.Value, cancellationToken));
        Assert.Equal(
            "database-password",
            await secrets.GetAsync(databaseProfile.CredentialReference!.Value, cancellationToken));
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public ValueTask SetAsync(
            SecretReference reference,
            string secret,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values[reference.Value] = secret;
            return ValueTask.CompletedTask;
        }

        public ValueTask<string?> GetAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_values.TryGetValue(reference.Value, out var value) ? value : null);
        }

        public ValueTask DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _values.Remove(reference.Value);
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
