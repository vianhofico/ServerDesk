using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Capabilities;
using ServerDesk.Application.Dashboard;
using ServerDesk.Application.History;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Processes;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Routing;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Services;
using ServerDesk.Application.Sessions;
using ServerDesk.Application.Settings;
using ServerDesk.Application.Storage;
using ServerDesk.Application.Terminal;
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
                // Process shutdown must continue; tunnel disposal closes SSH/listener resources best-effort.
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
                // Process shutdown will close any remaining socket. Exit must not be blocked by cleanup failures.
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
                // All critical remote resources were already given an explicit best-effort cleanup path above.
            }
        }

        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<IAppPaths, WindowsAppPaths>();
        services.AddSingleton<IAppSettingsStore, JsonAppSettingsStore>();
        services.AddSingleton<ISystemThemeDetector, WindowsSystemThemeDetector>();
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

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IThemeService, WpfThemeService>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
