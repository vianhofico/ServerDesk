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
        var secretStore = provider.GetRequiredService<ISecretStore>();
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
            ],
            DatabaseDiagnosticOptions.Default);
        var backupService = new AuditedDatabaseBackupService(
            new DatabaseBackupService(
                repository,
                secretStore,
                provider.GetRequiredService<IRemoteCommandExecutorFactory>(),
                new SqliteDatabaseBackupManifestRepository(connectionFactory),
                DatabaseBackupOptions.Default),
            provider.GetRequiredService<IOperationAudit>());
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
        window.InitializeBackupWorkflow(backupService);
        window.Show();
    }
}
