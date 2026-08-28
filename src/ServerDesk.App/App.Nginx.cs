using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Localization;
using ServerDesk.Application.Nginx;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class App
{
    internal void OpenNginxInventory(ServerProfile profile, bool connected, Window owner)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(owner);
        var provider = _serviceProvider ?? throw new InvalidOperationException("ServerDesk services are not initialized.");
        var service = new NginxInventoryService(
            provider.GetRequiredService<IRemoteCommandExecutorFactory>(),
            NginxInventoryOptions.Default);
        var window = new NginxInventoryWindow(
            service,
            provider.GetRequiredService<ILocalizationService>(),
            profile,
            connected)
        {
            Owner = owner,
        };
        window.Show();
    }
}
