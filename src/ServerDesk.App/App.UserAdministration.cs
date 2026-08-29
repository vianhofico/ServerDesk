using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Localization;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.UserAdministration;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class App
{
    internal void OpenUserAdministration(ServerProfile profile, bool connected, Window owner)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(owner);
        var provider = _serviceProvider ?? throw new InvalidOperationException("ServerDesk services are not initialized.");
        var commandFactory = provider.GetRequiredService<IRemoteCommandExecutorFactory>();
        var fileSystemFactory = provider.GetRequiredService<IRemoteFileSystemFactory>();
        var audit = provider.GetRequiredService<IOperationAudit>();

        var userOptions = UserAdministrationOptions.Default;
        var userService = new AuditedUserAdministrationService(
            new UserAdministrationService(commandFactory, userOptions),
            audit);

        var keyOptions = AuthorizedKeyAdministrationOptions.Default;
        var rawKeyService = new AuthorizedKeyAdministrationService(
            commandFactory,
            fileSystemFactory,
            keyOptions);
        var keyService = new AuditedAuthorizedKeyAdministrationService(
            new GuardedAuthorizedKeyAdministrationService(
                rawKeyService,
                commandFactory,
                keyOptions),
            audit);

        var window = new UserAdministrationWindow(
            userService,
            keyService,
            provider.GetRequiredService<ILocalizationService>(),
            profile,
            connected)
        {
            Owner = owner,
        };
        window.Show();
    }
}
