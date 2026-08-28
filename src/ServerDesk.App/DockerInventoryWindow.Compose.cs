using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Docker;

namespace ServerDesk.App;

public partial class DockerInventoryWindow
{
    private IDockerComposeService? _composeService;
    private Button? _composeButton;

    public IDockerComposeService? ComposeService
    {
        get => _composeService;
        set
        {
            _composeService = value;
            EnsureComposeButton();
        }
    }

    private void EnsureComposeButton()
    {
        if (_composeButton is not null || DiagnosticsButton.Parent is not StackPanel actions)
        {
            return;
        }

        _composeButton = new Button
        {
            Content = "Compose projects",
            Style = (Style)FindResource("DockerButton"),
            IsEnabled = _composeService is not null && _initiallyConnected,
            ToolTip = "Manage Docker Compose v2 projects, validated raw YAML and confirmed project actions.",
        };
        _composeButton.Click += ComposeOnClick;
        var diagnosticsIndex = actions.Children.IndexOf(DiagnosticsButton);
        actions.Children.Insert(Math.Max(0, diagnosticsIndex + 1), _composeButton);
    }

    private void ComposeOnClick(object sender, RoutedEventArgs e)
    {
        if (_composeService is null)
        {
            StatusText.Text = "Docker Compose workflows are unavailable in this session.";
            return;
        }

        var window = new DockerComposeWindow(_composeService, _profile, _initiallyConnected)
        {
            Owner = this,
        };
        window.Show();
    }
}
