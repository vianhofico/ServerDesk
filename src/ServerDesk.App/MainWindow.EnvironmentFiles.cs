using System.Windows;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private void OpenEnvironmentFilesOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenEnvironmentFiles(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
