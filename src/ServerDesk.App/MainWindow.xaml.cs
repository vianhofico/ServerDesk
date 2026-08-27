using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Terminal;

namespace ServerDesk.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel;
    private readonly IRemoteTerminalSessionFactory _terminalFactory;
    private ProfileEditorViewModel? _observedEditor;

    public MainWindow(
        ShellViewModel viewModel,
        IRemoteTerminalSessionFactory terminalFactory)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _terminalFactory = terminalFactory;
        DataContext = viewModel;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        ObserveEditor(_viewModel.Editor);
    }

    protected override void OnClosed(EventArgs e)
    {
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        ObserveEditor(null);
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

    private void OpenTerminalOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        var profiles = _viewModel.Servers.Select(server => server.Profile).ToArray();
        var window = new TerminalWindow(_terminalFactory, profiles, selected.Profile)
        {
            Owner = this,
        };
        window.Show();
    }

    private void ViewModelOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.Editor))
        {
            ObserveEditor(_viewModel.Editor);
            CredentialSecretBox.Password = string.Empty;
        }
    }

    private void EditorOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProfileEditorViewModel.NewSecret) &&
            _observedEditor is not null &&
            string.IsNullOrEmpty(_observedEditor.NewSecret) &&
            !string.IsNullOrEmpty(CredentialSecretBox.Password))
        {
            CredentialSecretBox.Password = string.Empty;
        }
    }

    private void ObserveEditor(ProfileEditorViewModel? editor)
    {
        if (_observedEditor is not null)
        {
            _observedEditor.PropertyChanged -= EditorOnPropertyChanged;
        }

        _observedEditor = editor;
        if (_observedEditor is not null)
        {
            _observedEditor.PropertyChanged += EditorOnPropertyChanged;
        }
    }
}
