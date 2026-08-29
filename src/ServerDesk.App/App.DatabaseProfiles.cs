using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Localization;
using ServerDesk.Application.Databases;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Persistence.Sqlite;

namespace ServerDesk.App;

public partial class App
{
    internal void OpenDatabaseProfiles(ServerProfile profile, bool connected, Window owner)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(owner);
        var provider = _serviceProvider ?? throw new InvalidOperationException("ServerDesk services are not initialized.");
        var repository = new SqliteDatabaseProfileRepository(
            provider.GetRequiredService<SqliteConnectionFactory>());
        var profileService = new DatabaseProfileService(
            repository,
            provider.GetRequiredService<IProfileRepository>(),
            provider.GetRequiredService<ISecretStore>());
        var tunnelService = new DatabaseTunnelService(
            provider.GetRequiredService<IProfileRepository>(),
            provider.GetRequiredService<IPortForwardSessionFactory>());
        var connectivityService = new DatabaseTunnelConnectivityService(
            tunnelService,
            DatabaseTunnelTestOptions.Default);
        var window = new DatabaseProfilesWindow(
            profileService,
            connectivityService,
            provider.GetRequiredService<ILocalizationService>(),
            profile,
            connected)
        {
            Owner = owner,
        };
        window.Show();
    }
}
