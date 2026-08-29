using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Localization;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Backups;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class App
{
    internal void OpenBackupRestore(ServerProfile profile, bool connected, Window owner)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(owner);
        var provider = _serviceProvider ?? throw new InvalidOperationException("ServerDesk services are not initialized.");
        var service = new AuditedBackupRestoreService(
            new BackupRestoreService(
                provider.GetRequiredService<IRemoteCommandExecutorFactory>(),
                BackupRestoreOptions.Default),
            provider.GetRequiredService<IOperationAudit>());
        var window = new BackupRestoreWindow(
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
