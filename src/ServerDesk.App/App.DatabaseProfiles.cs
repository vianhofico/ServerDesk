using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Localization;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Databases;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Databases;
using ServerDesk.Infrastructure.Persistence.Sqlite;

namespace ServerDesk.App;

public partial class App
{
    internal void OpenDatabaseProfiles(ServerProfile profile, bool connected, Window owner)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(owner);
        var provider = _serviceProvider ?? throw new InvalidOperationException("ServerDesk services are not initialized.");
        var connectionFactory = provider.GetRequiredService<SqliteConnectionFactory>();
        var repository = new SqliteDatabaseProfileRepository(connectionFactory);
        var manifestRepository = new SqliteDatabaseBackupManifestRepository(connectionFactory);
        var secretStore = provider.GetRequiredService<ISecretStore>();
        var remoteCommands = provider.GetRequiredService<IRemoteCommandExecutorFactory>();
        var audit = provider.GetRequiredService<IOperationAudit>();
        var profileService = new DatabaseProfileService(
            repository,
            provider.GetRequiredService<IProfileRepository>(),
            secretStore);
        var tunnelService = new DatabaseTunnelService(
            provider.GetRequiredService<IProfileRepository>(),
            provider.GetRequiredService<IPortForwardSessionFactory>());
        var connectivityService = new DatabaseTunnelConnectivityService(
            tunnelService,
            DatabaseTunnelTestOptions.Default);
        var diagnosticService = new DatabaseDiagnosticService(
            tunnelService,
            secretStore,
            [
                new PostgreSqlDiagnosticAdapter(),
                new MySqlDiagnosticAdapter(),
                new MariaDbDiagnosticAdapter(),
                new RedisDiagnosticAdapter(),
                new SqlServerDiagnosticAdapter(),
                new MongoDbDiagnosticAdapter(),
            ],
            DatabaseDiagnosticOptions.Default);

        var standardBackup = new DatabaseBackupService(
            repository,
            secretStore,
            remoteCommands,
            manifestRepository,
            DatabaseBackupOptions.Default);
        var sqlServerBackup = new SqlServerDatabaseBackupService(
            repository,
            secretStore,
            remoteCommands,
            manifestRepository,
            DatabaseBackupOptions.Default);
        var mongoDbBackup = new MongoDbDatabaseBackupService(
            repository,
            secretStore,
            remoteCommands,
            manifestRepository,
            diagnosticService,
            DatabaseBackupOptions.Default);
        var sqlAwareBackup = new DatabaseBackupServiceRouter(repository, standardBackup, sqlServerBackup);
        var backupService = new HistoryDatabaseBackupService(
            new MongoDbDatabaseBackupServiceRouter(repository, sqlAwareBackup, mongoDbBackup),
            repository,
            audit);

        var standardRestore = new DatabaseRestoreService(
            repository,
            manifestRepository,
            secretStore,
            remoteCommands,
            DatabaseRestoreOptions.Default);
        var sqlServerRestore = new SqlServerDatabaseRestoreService(
            repository,
            manifestRepository,
            secretStore,
            remoteCommands,
            DatabaseRestoreOptions.Default);
        var mongoDbRestore = new MongoDbDatabaseRestoreService(
            repository,
            manifestRepository,
            secretStore,
            remoteCommands,
            diagnosticService,
            DatabaseRestoreOptions.Default);
        var sqlAwareRestore = new DatabaseRestoreServiceRouter(repository, standardRestore, sqlServerRestore);
        var restoreService = new HistoryDatabaseRestoreService(
            new MongoDbDatabaseRestoreServiceRouter(repository, sqlAwareRestore, mongoDbRestore),
            audit);

        var window = new DatabaseProfilesWindow(
            profileService,
            connectivityService,
            diagnosticService,
            provider.GetRequiredService<ILocalizationService>(),
            profile,
            connected)
        {
            Owner = owner,
        };
        window.InitializeBackupWorkflow(backupService, restoreService);
        window.Show();
    }
}
