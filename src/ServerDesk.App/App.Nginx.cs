using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Localization;
using ServerDesk.Application.Nginx;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class App
{
    internal void OpenNginxInventory(ServerProfile profile, bool connected, Window owner)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(owner);
        var provider = _serviceProvider ?? throw new InvalidOperationException("ServerDesk services are not initialized.");
        var window = new NginxInventoryWindow(
            provider.GetRequiredService<INginxInventoryService>(),
            provider.GetRequiredService<ILocalizationService>(),
            profile,
            connected)
        {
            Owner = owner,
        };
        window.Show();
    }

    internal bool OpenNginxSiteEditor(ServerProfile profile, NginxSiteInfo site, Window owner)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(site);
        ArgumentNullException.ThrowIfNull(owner);
        var provider = _serviceProvider ?? throw new InvalidOperationException("ServerDesk services are not initialized.");
        var window = new NginxSiteEditorWindow(
            provider.GetRequiredService<INginxSiteEditingService>(),
            provider.GetRequiredService<ILocalizationService>(),
            profile,
            site)
        {
            Owner = owner,
        };
        _ = window.ShowDialog();
        return window.WasApplied;
    }
}
