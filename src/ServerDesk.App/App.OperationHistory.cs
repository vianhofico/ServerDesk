using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Localization;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Profiles;

namespace ServerDesk.App;

public partial class App
{
    internal void OpenOperationHistory(Guid? initialServerProfileId, Window owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var provider = _serviceProvider ?? throw new InvalidOperationException("ServerDesk services are not initialized.");
        var window = new OperationHistoryWindow(
            provider.GetRequiredService<IOperationHistoryService>(),
            provider.GetRequiredService<IServerProfileService>(),
            provider.GetRequiredService<ILocalizationService>(),
            initialServerProfileId)
        {
            Owner = owner,
        };
        window.Show();
    }
}
