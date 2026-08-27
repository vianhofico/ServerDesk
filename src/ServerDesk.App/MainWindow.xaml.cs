using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Presentation;

namespace ServerDesk.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel;

    public MainWindow(ShellViewModel viewModel)
    {
        InitializeComponent();
        _viewModel = viewModel;
        DataContext = viewModel;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        CredentialSecretBox.Password = string.Empty;
        base.OnClosed(e);
    }

    private void CredentialSecretBoxOnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox passwordBox && _viewModel.Editor is not null)
        {
            _viewModel.Editor.NewSecret = passwordBox.Password;
        }
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Editor))
        {
            CredentialSecretBox.Password = string.Empty;
        }
    }
}
