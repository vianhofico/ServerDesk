using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.Audit;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Application.Settings;
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

        _serviceProvider?.Dispose();
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
        services.AddSingleton<IOperationAudit, SqliteOperationAudit>();

        services.AddSingleton<IServerProfileService, ServerProfileService>();
        services.AddSingleton<IHostTrustPrompt, WpfHostTrustPrompt>();
        services.AddSingleton<IHostTrustService, HostTrustService>();
        services.AddSingleton<IInteractiveAuthenticationPrompt, WpfInteractiveAuthenticationPrompt>();
        services.AddSingleton(SshSessionOptions.Default);
        services.AddSingleton<IRemoteSessionFactory, SshRemoteSessionFactory>();
        services.AddSingleton<IRemoteFileSystemFactory, SftpRemoteFileSystemFactory>();
        services.AddSingleton<IRemoteTerminalSessionFactory, SshRemoteTerminalSessionFactory>();
        services.AddSingleton<IPortForwardSessionFactory, SshPortForwardSessionFactory>();
        services.AddSingleton<PortForwardManager>();

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IThemeService, WpfThemeService>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
