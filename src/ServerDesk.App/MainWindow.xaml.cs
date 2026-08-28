using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Capabilities;
using ServerDesk.Application.Dashboard;
using ServerDesk.Application.Docker;
using ServerDesk.Application.History;
using ServerDesk.Application.Logs;
using ServerDesk.Application.Networking;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Processes;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Routing;
using ServerDesk.Application.Services;
using ServerDesk.Application.Sessions;
using ServerDesk.Application.Storage;
using ServerDesk.Application.Terminal;

namespace ServerDesk.App;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _viewModel;
    private readonly IRemoteTerminalSessionFactory _terminalFactory;
    private readonly IRemoteFileSystemFactory _remoteFileSystemFactory;
    private readonly IRemoteFileEditorService _remoteFileEditorService;
    private readonly IServerProcessService _processService;
    private readonly IServerServiceManager _serviceManager;
    private readonly IDockerInventoryService _dockerInventoryService;
    private readonly IDockerContainerDiagnosticsService _dockerDiagnosticsService;
    private readonly IDockerContainerActionService _dockerActionService;
    private readonly IDockerExecTerminalSessionFactory _dockerExecTerminalSessionFactory;
    private readonly IServerStorageService _storageService;
    private readonly IServerNetworkService _networkService;
    private readonly IServerLogService _logService;
    private readonly PortForwardManager _portForwardManager;
    private readonly IServerCapabilityService _capabilityService;
    private readonly IServerDashboardService _dashboardService;
    private readonly IServerConnectionRouteService _connectionRouteService;
    private readonly IServerProfileOrganizationService _organizationService;
    private readonly IConnectionHistoryRepository _historyRepository;
    private ProfileEditorViewModel? _observedEditor;
    private ServerProfileListItemViewModel? _observedServer;
    private CapabilitySummaryControl? _capabilitySummary;

    public MainWindow(
        ShellViewModel viewModel,
        IRemoteTerminalSessionFactory terminalFactory,
        IRemoteFileSystemFactory remoteFileSystemFactory,
        IRemoteFileEditorService remoteFileEditorService,
        IServerProcessService processService,
        IServerServiceManager serviceManager,
        IDockerInventoryService dockerInventoryService,
        IDockerContainerDiagnosticsService dockerDiagnosticsService,
        IDockerContainerActionService dockerActionService,
        IDockerExecTerminalSessionFactory dockerExecTerminalSessionFactory,
        IServerStorageService storageService,
        IServerNetworkService networkService,
        IServerLogService logService,
        PortForwardManager portForwardManager,
        IServerCapabilityService capabilityService,
        IServerDashboardService dashboardService,
        IServerConnectionRouteService connectionRouteService,
        IServerProfileOrganizationService organizationService,
        IConnectionHistoryRepository historyRepository)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _terminalFactory = terminalFactory;
        _remoteFileSystemFactory = remoteFileSystemFactory;
        _remoteFileEditorService = remoteFileEditorService;
        _processService = processService;
        _serviceManager = serviceManager;
        _dockerInventoryService = dockerInventoryService;
        _dockerDiagnosticsService = dockerDiagnosticsService;
        _dockerActionService = dockerActionService;
        _dockerExecTerminalSessionFactory = dockerExecTerminalSessionFactory;
        _storageService = storageService;
        _networkService = networkService;
        _logService = logService;
        _portForwardManager = portForwardManager;
        _capabilityService = capabilityService;
        _dashboardService = dashboardService;
        _connectionRouteService = connectionRouteService;
        _organizationService = organizationService;
        _historyRepository = historyRepository;
        DataContext = viewModel;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        Loaded += AddRemoteActionsOnLoaded;
        ObserveEditor(_viewModel.Editor);
        ObserveSelectedServer(_viewModel.SelectedServer);
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= AddRemoteActionsOnLoaded;
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        ObserveEditor(null);
        ObserveSelectedServer(null);
        _capabilitySummary?.Dispose();
        _capabilitySummary = null;
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

    private void AddRemoteActionsOnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= AddRemoteActionsOnLoaded;
        var editButton = FindDescendant<Button>(this, button => string.Equals(button.Content as string, "Edit", StringComparison.Ordinal));
        if (editButton is null || VisualTreeHelper.GetParent(editButton) is not StackPanel actionPanel)
        {
            return;
        }

        var editIndex = Math.Max(0, actionPanel.Children.IndexOf(editButton));
        var dashboardButton = CreateActionButton(
            "Dashboard",
            "Open read-only CPU, memory, load, uptime, network and filesystem health metrics.",
            OpenDashboardOnClick);
        var explorerButton = CreateActionButton(
            "Explorer",
            "Browse and manage remote files over the certified SFTP channel.",
            OpenExplorerOnClick);
        var processesButton = CreateActionButton(
            "Processes",
            "Inspect processes and send confirmed SIGTERM/SIGKILL actions.",
            OpenProcessesOnClick);
        var servicesButton = CreateActionButton(
            "Services",
            "Inspect systemd services and run confirmed lifecycle actions with verification.",
            OpenServicesOnClick);
        var dockerButton = CreateActionButton(
            "Docker",
            "Inspect Docker resources, verified container lifecycle actions and container exec without exposing the Docker socket.",
            OpenDockerOnClick);
        var storageButton = CreateActionButton(
            "Storage",
            "Inspect filesystems, block devices and run cancellable read-only directory analysis.",
            OpenStorageOnClick);
        var networkButton = CreateActionButton(
            "Network",
            "Inspect interfaces, traffic rates and listening TCP/UDP sockets without mutating the server.",
            OpenNetworkOnClick);
        var logsButton = CreateActionButton(
            "Logs",
            "Inspect journald or remote text logs with follow, pause, filters and explicit local export.",
            OpenLogsOnClick);
        var organizeButton = CreateActionButton(
            "Organize",
            "Manage groups, tags and favorites and search/filter saved servers.",
            OpenOrganizationOnClick);
        var historyButton = CreateActionButton(
            "History",
            "View recent secret-safe SSH connection attempts.",
            OpenConnectionHistoryOnClick);
        var routeButton = CreateActionButton(
            "Route",
            "Choose Direct, HTTP/SOCKS proxy or an SSH bastion route for this server.",
            OpenConnectionRouteOnClick);
        var terminalButton = CreateActionButton(
            "Terminal",
            "Open a real SSH PTY terminal (Ctrl+Shift+F searches scrollback).",
            OpenTerminalOnClick);
        var tunnelsButton = CreateActionButton(
            "Tunnels",
            "Manage local, remote and SOCKS5 SSH port forwarding.",
            OpenPortForwardingOnClick);
        actionPanel.Children.Insert(editIndex, dashboardButton);
        actionPanel.Children.Insert(editIndex + 1, explorerButton);
        actionPanel.Children.Insert(editIndex + 2, processesButton);
        actionPanel.Children.Insert(editIndex + 3, servicesButton);
        actionPanel.Children.Insert(editIndex + 4, dockerButton);
        actionPanel.Children.Insert(editIndex + 5, storageButton);
        actionPanel.Children.Insert(editIndex + 6, networkButton);
        actionPanel.Children.Insert(editIndex + 7, logsButton);
        actionPanel.Children.Insert(editIndex + 8, organizeButton);
        actionPanel.Children.Insert(editIndex + 9, historyButton);
        actionPanel.Children.Insert(editIndex + 10, routeButton);
        actionPanel.Children.Insert(editIndex + 11, terminalButton);
        actionPanel.Children.Insert(editIndex + 12, tunnelsButton);

        if (VisualTreeHelper.GetParent(actionPanel) is Grid headerGrid &&
            VisualTreeHelper.GetParent(headerGrid) is StackPanel serverCardPanel)
        {
            _capabilitySummary = new CapabilitySummaryControl(_capabilityService)
            {
                Margin = new Thickness(0, 14, 0, 0),
            };
            serverCardPanel.Children.Insert(Math.Min(3, serverCardPanel.Children.Count), _capabilitySummary);
            UpdateCapabilitySummary();
        }
    }

    private Button CreateActionButton(string label, string toolTip, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = label,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("SecondaryButton"),
            ToolTip = toolTip,
        };
        button.Click += handler;
        return button;
    }

    private void OpenDashboardOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        var window = new ServerDashboardWindow(
            _dashboardService,
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected)
        {
            Owner = this,
        };
        window.Show();
    }

    private void OpenExplorerOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        var window = new RemoteExplorerWindow(
            _remoteFileSystemFactory,
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected)
        {
            EditorService = _remoteFileEditorService,
            Owner = this,
        };
        window.Show();
    }

    private void OpenProcessesOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        var window = new ProcessManagerWindow(
            _processService,
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected)
        {
            Owner = this,
        };
        window.Show();
    }

    private void OpenServicesOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        var window = new ServiceManagerWindow(
            _serviceManager,
            _logService,
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected)
        {
            Owner = this,
        };
        window.Show();
    }

    private void OpenDockerOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        var window = new DockerInventoryWindow(
            _dockerInventoryService,
            _dockerDiagnosticsService,
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected)
        {
            ActionService = _dockerActionService,
            ExecTerminalSessionFactory = _dockerExecTerminalSessionFactory,
            Owner = this,
        };
        window.Show();
    }

    private void OpenStorageOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        var window = new StorageWindow(
            _storageService,
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected)
        {
            Owner = this,
        };
        window.Show();
    }

    private void OpenNetworkOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        var window = new NetworkWindow(
            _networkService,
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected)
        {
            Owner = this,
        };
        window.Show();
    }

    private void OpenLogsOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        var window = new LogViewerWindow(
            _logService,
            selected.Profile,
            selected.ConnectionState == RemoteSessionState.Connected)
        {
            Owner = this,
        };
        window.Show();
    }

    private void OpenOrganizationOnClick(object sender, RoutedEventArgs e)
    {
        var window = new ProfileOrganizationWindow(_organizationService, _viewModel.SelectedServer?.Id)
        {
            Owner = this,
        };
        _ = window.ShowDialog();
    }

    private void OpenConnectionHistoryOnClick(object sender, RoutedEventArgs e)
    {
        var window = new ConnectionHistoryWindow(_historyRepository)
        {
            Owner = this,
        };
        _ = window.ShowDialog();
    }

    private void OpenConnectionRouteOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected || !selected.CanModifyProfile)
        {
            return;
        }

        var profiles = _viewModel.Servers.Select(server => server.Profile).ToArray();
        var window = new ConnectionRouteWindow(_connectionRouteService, selected.Profile, profiles)
        {
            Owner = this,
        };
        _ = window.ShowDialog();
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

    private void OpenPortForwardingOnClick(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedServer is not { } selected)
        {
            return;
        }

        var window = new PortForwardingWindow(_portForwardManager, selected.Profile)
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
        else if (e.PropertyName == nameof(ShellViewModel.SelectedServer))
        {
            ObserveSelectedServer(_viewModel.SelectedServer);
            UpdateCapabilitySummary();
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

    private void SelectedServerOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ServerProfileListItemViewModel.ConnectionState))
        {
            UpdateCapabilitySummary();
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

    private void ObserveSelectedServer(ServerProfileListItemViewModel? server)
    {
        if (_observedServer is not null)
        {
            _observedServer.PropertyChanged -= SelectedServerOnPropertyChanged;
        }

        _observedServer = server;
        if (_observedServer is not null)
        {
            _observedServer.PropertyChanged += SelectedServerOnPropertyChanged;
        }
    }

    private void UpdateCapabilitySummary()
    {
        if (_capabilitySummary is null)
        {
            return;
        }

        var selected = _viewModel.SelectedServer;
        _capabilitySummary.SetServer(
            selected?.Profile,
            selected?.ConnectionState == RemoteSessionState.Connected);
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
