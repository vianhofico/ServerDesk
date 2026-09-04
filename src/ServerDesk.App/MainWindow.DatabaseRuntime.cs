using System.Windows;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private void OpenDatabaseRuntimeOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected || System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenDatabaseRuntime(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }

    private void OpenDatabaseProfilesOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected || System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenDatabaseProfiles(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
