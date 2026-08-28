using System.ComponentModel;
using System.Windows;
using ServerDesk.Application.Docker;
using ServerDesk.Application.Terminal;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class DockerExecTerminalWindow : Window
{
    private readonly IDockerExecTerminalSessionFactory _sessionFactory;
    private readonly ServerProfile _profile;
    private readonly string _containerId;
    private readonly string _containerName;
    private TerminalTabHost? _host;
    private bool _closing;
    private bool _allowClose;

    public DockerExecTerminalWindow(
        IDockerExecTerminalSessionFactory sessionFactory,
        ServerProfile profile,
        string containerId,
        string containerName)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _containerId = DockerContainerIdentifier.Normalize(containerId);
        _containerName = string.IsNullOrWhiteSpace(containerName) ? _containerId[..12] : containerName;
        InitializeComponent();

        TitleText.Text = $"Docker exec · {_containerName}";
        IdentityText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port} · container {_containerId[..12]}";
        StatusText.Text = "Opening an interactive /bin/sh through the existing SSH PTY. The Docker socket is not exposed or forwarded.";
    }

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        var session = _sessionFactory.Create(_profile, _containerId);
        var host = new TerminalTabHost(session);
        _host = host;
        TerminalHost.Content = host;
        host.StateChanged += state => StatusText.Text = $"Docker exec terminal: {state}.";
        host.ErrorRaised += message => StatusText.Text = message;

        try
        {
            await host.InitializeAsync().ConfigureAwait(true);
            StatusText.Text = $"Connected. ServerDesk requested /bin/sh inside '{_containerName}' using docker exec.";
            await host.FocusAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Docker exec terminal connection was cancelled.";
        }
        catch (TerminalSessionException exception)
        {
            StatusText.Text = exception.Error.Message;
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not open Docker exec terminal: {exception.Message}";
        }
    }

    private void WindowOnClosing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_closing)
        {
            return;
        }

        _closing = true;
        _ = DisposeAndCloseAsync();
    }

    private async Task DisposeAndCloseAsync()
    {
        var host = _host;
        _host = null;
        TerminalHost.Content = null;
        if (host is not null)
        {
            try
            {
                await host.DisposeAsync().ConfigureAwait(true);
            }
            catch
            {
                // Window close must continue; disposing the PTY transport is best-effort during shutdown.
            }
        }

        _allowClose = true;
        Close();
    }
}
