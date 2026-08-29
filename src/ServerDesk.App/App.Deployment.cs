using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Localization;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Deployment;
using ServerDesk.Application.Docker;
using ServerDesk.Application.Git;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Services;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class App
{
    internal void OpenDeployment(ServerProfile profile, bool connected, Window owner)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(owner);
        var provider = _serviceProvider ?? throw new InvalidOperationException("ServerDesk services are not initialized.");
        var options = DeploymentOptions.Default;
        var serviceManager = provider.GetRequiredService<IServerServiceManager>();
        var healthRunner = new DeploymentHealthCheckRunner(
            provider.GetRequiredService<IRemoteCommandExecutorFactory>(),
            serviceManager,
            provider.GetRequiredService<IDockerInventoryService>(),
            options);
        var orchestration = new DeploymentOrchestrationService(
            provider.GetRequiredService<IGitOperationsService>(),
            provider.GetRequiredService<IDockerComposeService>(),
            serviceManager,
            healthRunner,
            provider.GetRequiredService<IOperationAudit>(),
            options);
        var window = new DeploymentWindow(
            orchestration,
            provider.GetRequiredService<ILocalizationService>(),
            profile,
            connected)
        {
            Owner = owner,
        };
        window.Show();
    }
}
