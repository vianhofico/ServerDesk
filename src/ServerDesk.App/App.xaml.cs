using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Localization;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Capabilities;
using ServerDesk.Application.Dashboard;
using ServerDesk.Application.Docker;
using ServerDesk.Application.EnvironmentFiles;
using ServerDesk.Application.Firewall;
using ServerDesk.Application.Git;
using ServerDesk.Application.History;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Logs;
using ServerDesk.Application.Networking;
using ServerDesk.Application.Nginx;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Processes;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Routing;
using ServerDesk.Application.ScheduledTasks;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Services;
using ServerDesk.Application.Sessions;
using ServerDesk.Application.Settings;
using ServerDesk.Application.Storage;
using ServerDesk.Application.Terminal;
using ServerDesk.Application.Tls;
using ServerDesk.Infrastructure.Persistence.Sqlite;
using ServerDesk.Infrastructure.Ssh;
using ServerDesk.Platform.Windows;

namespace ServerDesk.App;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            var services = new ServiceCollection();
            ConfigureServices(services);
            _serviceProvider = services.BuildServiceProvider(
                new ServiceProviderOptions
                {
                    ValidateOnBuild = true,
                    ValidateScopes = true,
                });

            var databaseInitializer = _serviceProvider.GetRequiredService<SqliteDatabaseInitializer>();
            await databaseInitializer.InitializeAsync().ConfigureAwait(true);

            var shellViewModel = _serviceProvider.GetRequiredService<ShellViewModel>();
            await shellViewModel.InitializeAsync().ConfigureAwait(true);

            var window = _serviceProvider.GetRequiredService<MainWindow>();
            MainWindow = window;
            window.Show();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                $"ServerDesk could not start.\n\n{exception.Message}",
                "ServerDesk startup error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown(-1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_serviceProvider?.GetService<PortForwardManager>() is { } portForwardManager)
        {
            try
            {
                portForwardManager.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        if (_serviceProvider?.GetService<ShellViewModel>() is { } shellViewModel)
        {
            try
            {
                shellViewModel.ShutdownAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        if (_serviceProvider is not null)
        {
            try
            {
                _serviceProvider.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            catch
            {
            }
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAppPaths, WindowsAppPaths>();
        services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
        services.AddSingleton<ISystemThemeDetector, WindowsSystemThemeDetector>();
        services.AddSingleton<ISystemCultureDetector, WindowsSystemCultureDetector>();
        services.AddSingleton<ISecretStore, WindowsCredentialSecretStore>();

        services.AddSingleton<SqliteConnectionFactory>();
        services.AddSingleton<SqliteDatabaseInitializer>();
        services.AddSingleton<IProfileRepository, SqliteProfileRepository>();
        services.AddSingleton<IKnownHostRepository, SqliteKnownHostRepository>();
        services.AddSingleton<IPortForwardProfileRepository, SqlitePortForwardProfileRepository>();
        services.AddSingleton<IConnectionRouteRepository, SqliteConnectionRouteRepository>();
        services.AddSingleton<IServerProfileOrganizationRepository, SqliteServerProfileOrganizationRepository>();
        services.AddSingleton<IConnectionHistoryRepository, SqliteConnectionHistoryRepository>();
        services.AddSingleton<IOperationAudit, SqliteOperationAudit>();

        services.AddSingleton<IServerProfileService, ServerProfileService>();
        services.AddSingleton<IServerConnectionRouteService, ServerConnectionRouteService>();
        services.AddSingleton<IServerProfileOrganizationService, ServerProfileOrganizationService>();
        services.AddSingleton<IHostTrustPrompt, WpfHostTrustPrompt>();
        services.AddSingleton<IHostTrustService, HostTrustService>();
        services.AddSingleton<IInteractiveAuthenticationPrompt, WpfInteractiveAuthenticationPrompt>();
        services.AddSingleton(SshSessionOptions.Default);
        services.AddSingleton<RouteAwareRemoteSessionFactory>();
        services.AddSingleton<IRemoteSessionFactory>(provider =>
            new ConnectionHistoryRemoteSessionFactory(
                provider.GetRequiredService<RouteAwareRemoteSessionFactory>(),
                provider.GetRequiredService<IConnectionHistoryRepository>(),
                provider.GetRequiredService<IConnectionRouteRepository>(),
                provider.GetRequiredService<IProfileRepository>()));
        services.AddSingleton<IRemoteCommandExecutorFactory, RouteAwareRemoteCommandExecutorFactory>();
        services.AddSingleton<IRemoteFileSystemFactory, RouteAwareSftpRemoteFileSystemFactory>();
        services.AddSingleton<IRemoteFileEditorService, GuardedRemoteFileEditorService>();
        services.AddSingleton<IRemoteTerminalSessionFactory, RouteAwareRemoteTerminalSessionFactory>();
        services.AddSingleton<IPortForwardSessionFactory, RouteAwarePortForwardSessionFactory>();
        services.AddSingleton<PortForwardManager>();
        services.AddSingleton(ServerCapabilityOptions.Default);
        services.AddSingleton<IServerCapabilityService, ServerCapabilityService>();
        services.AddSingleton(ServerDashboardOptions.Default);
        services.AddSingleton<IServerDashboardService, ServerDashboardService>();
        services.AddSingleton<ServerProcessService>();
        services.AddSingleton<IServerProcessService>(provider =>
            new AuditedServerProcessService(
                provider.GetRequiredService<ServerProcessService>(),
                provider.GetRequiredService<IOperationAudit>()));
        services.AddSingleton(SystemdServiceOptions.Default);
        services.AddSingleton<SystemdServiceManager>();
        services.AddSingleton<IServerServiceManager>(provider =>
            new AuditedServerServiceManager(
                provider.GetRequiredService<SystemdServiceManager>(),
                provider.GetRequiredService<IOperationAudit>()));
        services.AddSingleton<IServerStorageService, ServerStorageService>();
        services.AddSingleton(ServerNetworkOptions.Default);
        services.AddSingleton<IServerNetworkService, ServerNetworkService>();
        services.AddSingleton(ServerLogOptions.Default);
        services.AddSingleton<IServerLogService, ServerLogService>();
        services.AddSingleton(FirewallInventoryOptions.Default);
        services.AddSingleton<IFirewallManager, FirewallInventoryService>();
        services.AddSingleton(DockerInventoryOptions.Default);
        services.AddSingleton<IDockerInventoryService, DockerInventoryService>();
        services.AddSingleton(DockerContainerDiagnosticsOptions.Default);
        services.AddSingleton<IDockerContainerDiagnosticsService, DockerContainerDiagnosticsService>();
        services.AddSingleton(DockerContainerActionOptions.Default);
        services.AddSingleton<DockerContainerActionService>();
        services.AddSingleton<IDockerContainerActionService>(provider =>
            new AuditedDockerContainerActionService(
                provider.GetRequiredService<DockerContainerActionService>(),
                provider.GetRequiredService<IOperationAudit>()));
        services.AddSingleton<IDockerExecTerminalSessionFactory, DockerExecTerminalSessionFactory>();
        services.AddSingleton(DockerComposeOptions.Default);
        services.AddSingleton<DockerComposeService>();
        services.AddSingleton<IDockerComposeService>(provider =>
            new AuditedDockerComposeService(
                provider.GetRequiredService<DockerComposeService>(),
                provider.GetRequiredService<IOperationAudit>()));
        services.AddSingleton(GitOperationsOptions.Default);
        services.AddSingleton<GitOperationsService>();
        services.AddSingleton<IGitOperationsService>(provider =>
            new AuditedGitOperationsService(
                provider.GetRequiredService<GitOperationsService>(),
                provider.GetRequiredService<IOperationAudit>()));
        services.AddSingleton(ScheduledTaskOptions.Default);
        services.AddSingleton<ScheduledTaskService>();
        services.AddSingleton<GuardedScheduledTaskService>(provider =>
            new GuardedScheduledTaskService(
                provider.GetRequiredService<ScheduledTaskService>(),
                provider.GetRequiredService<IRemoteCommandExecutorFactory>(),
                provider.GetRequiredService<IRemoteFileSystemFactory>(),
                provider.GetRequiredService<ScheduledTaskOptions>()));
        services.AddSingleton<IScheduledTaskService>(provider =>
            new AuditedScheduledTaskService(
                provider.GetRequiredService<GuardedScheduledTaskService>(),
                provider.GetRequiredService<IOperationAudit>()));
        services.AddSingleton(NginxInventoryOptions.Default);
        services.AddSingleton<INginxInventoryService, NginxInventoryService>();
        services.AddSingleton(NginxSiteEditingOptions.Default);
        services.AddSingleton<NginxSiteEditingService>();
        services.AddSingleton<INginxSiteEditingService>(provider =>
            new AuditedNginxSiteEditingService(
                provider.GetRequiredService<NginxSiteEditingService>(),
                provider.GetRequiredService<IOperationAudit>()));
        services.AddSingleton(TlsCertificateOptions.Default);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<TlsCertificateService>();
        services.AddSingleton<ITlsCertificateService>(provider =>
            new AuditedTlsCertificateService(
                provider.GetRequiredService<TlsCertificateService>(),
                provider.GetRequiredService<IOperationAudit>()));
        services.AddSingleton(EnvironmentFileOptions.Default);
        services.AddSingleton<EnvironmentFileService>();
        services.AddSingleton<IEnvironmentFileService>(provider =>
            new AuditedEnvironmentFileService(
                provider.GetRequiredService<EnvironmentFileService>(),
                provider.GetRequiredService<IOperationAudit>()));

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IThemeService, WpfThemeService>();
        services.AddSingleton<ILocalizationService, WpfLocalizationService>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
