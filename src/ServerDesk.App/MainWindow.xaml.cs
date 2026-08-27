using ServerDesk.App.Presentation;

namespace ServerDesk.App;

public partial class MainWindow : System.Windows.Window
{
    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }
}
