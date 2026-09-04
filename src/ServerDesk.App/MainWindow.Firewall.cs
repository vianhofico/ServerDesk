using System.Windows;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private void OpenFirewallOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected ||
            System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenFirewallInventory(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
