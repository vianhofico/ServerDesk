using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
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
        Loaded += AddTerminalActionOnLoaded;
        ObserveEditor(_viewModel.Editor);
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= AddTerminalActionOnLoaded;
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

    private void AddTerminalActionOnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= AddTerminalActionOnLoaded;
        var editButton = FindDescendant<Button>(this, button => string.Equals(button.Content as string, "Edit", StringComparison.Ordinal));
        if (editButton is null || VisualTreeHelper.GetParent(editButton) is not StackPanel actionPanel)
        {
            return;
        }

        var terminalButton = new Button
        {
            Content = "Terminal",
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
            ToolTip = "Open a real SSH PTY terminal (Ctrl+Shift+F searches scrollback).",
        };
        terminalButton.Click += OpenTerminalOnClick;
        var editIndex = actionPanel.Children.IndexOf(editButton);
        actionPanel.Children.Insert(Math.Max(0, editIndex), terminalButton);
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

    private static T? FindDescendant<T>(DependencyObject root, Func<T, bool> predicate)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T candidate && predicate(candidate))
            {
                return candidate;
            }

            var nested = FindDescendant(child, predicate);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
    }
}
