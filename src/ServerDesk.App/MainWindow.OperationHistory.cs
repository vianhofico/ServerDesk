using System.Windows;

namespace ServerDesk.App;

public partial class MainWindow
{
    private void OpenOperationHistoryOnClick(object sender, RoutedEventArgs e)
    {
        if (System.Windows.Application.Current is not App app)
        {
            return;
        }

        app.OpenOperationHistory(_viewModel.SelectedServer?.Profile.Id, this);
    }
}
