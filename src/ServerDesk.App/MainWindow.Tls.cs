using System.Windows;
using ServerDesk.Application.Sessions;

namespace ServerDesk.App;

public partial class MainWindow
{
    private void OpenTlsCertificatesOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenTlsCertificates(
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected,
            this);
    }
}
