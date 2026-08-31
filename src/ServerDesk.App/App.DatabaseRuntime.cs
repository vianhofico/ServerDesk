using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Localization;
using ServerDesk.Application.Databases;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class App
{
    internal void OpenDatabaseRuntime(ServerProfile profile, bool connected, Window owner)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(owner);
        var provider = _serviceProvider ?? throw new InvalidOperationException("ServerDesk services are not initialized.");
        var remoteCommands = provider.GetRequiredService<IRemoteCommandExecutorFactory>();
        var sqlServerRuntime = new SqlServerAwareDatabaseRuntimeService(
            provider.GetRequiredService<IDatabaseRuntimeService>(),
            remoteCommands,
            DatabaseRuntimeOptions.Default);
        var runtimeService = new MongoDbAwareDatabaseRuntimeService(
            sqlServerRuntime,
            remoteCommands,
            DatabaseRuntimeOptions.Default);
        var window = new DatabaseRuntimeWindow(
            runtimeService,
            provider.GetRequiredService<ILocalizationService>(),
            profile,
            connected)
        {
            Owner = owner,
        };
        window.Show();
    }
}
