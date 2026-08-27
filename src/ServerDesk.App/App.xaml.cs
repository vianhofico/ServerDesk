using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Settings;
using ServerDesk.Infrastructure.Persistence.Sqlite;
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
        services.AddSingleton<IOperationAudit, SqliteOperationAudit>();

        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<IThemeService, WpfThemeService>();
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<MainWindow>();
    }
}
