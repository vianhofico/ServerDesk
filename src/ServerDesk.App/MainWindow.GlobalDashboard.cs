using System.Windows;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private void OpenGlobalDashboardOnClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenGlobalDashboard(
            () => _viewModel.Servers
                .Select(server => new GlobalDashboardTarget(server.Profile, server.ConnectionState))
                .ToArray(),
            this);
    }
}
