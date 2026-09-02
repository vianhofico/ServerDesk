using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Localization;
using ServerDesk.Application.Dashboard;
using ServerDesk.Application.Profiles;

namespace ServerDesk.App;

public partial class App
{
    internal void OpenGlobalDashboard(
        Func<IReadOnlyList<GlobalDashboardTarget>> targetsProvider,
        Window owner)
    {
        ArgumentNullException.ThrowIfNull(targetsProvider);
        ArgumentNullException.ThrowIfNull(owner);

        var provider = _serviceProvider ?? throw new InvalidOperationException("ServerDesk services are not initialized.");
        var refreshService = new MultiServerDashboardRefreshService(
            provider.GetRequiredService<IServerDashboardService>(),
            MultiServerDashboardRefreshOptions.Default);
        var window = new GlobalDashboardWindow(
            refreshService,
            provider.GetRequiredService<IServerProfileOrganizationService>(),
            provider.GetRequiredService<ILocalizationService>(),
            targetsProvider)
        {
            Owner = owner,
        };
        window.Show();
    }
}
