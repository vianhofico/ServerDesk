using System.Windows;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private void OpenUserAdministrationOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected ||
            System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenUserAdministration(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
