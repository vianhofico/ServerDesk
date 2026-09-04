using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Localization;
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
    private readonly ILocalizationService _localization;
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
    private readonly ObservableCollection<WorkspaceNavigationItem> _workspaceNavigationItems = [];
    private ProfileEditorViewModel? _observedEditor;
    private ServerProfileListItemViewModel? _observedServer;
    private CapabilitySummaryControl? _capabilitySummary;

    public MainWindow(
        ShellViewModel viewModel,
        ILocalizationService localization,
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
        _localization = localization;
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
        WorkspaceNavigationList.ItemsSource = _workspaceNavigationItems;
        _viewModel.PropertyChanged += ViewModelOnPropertyChanged;
        _localization.LanguageChanged += LocalizationOnLanguageChanged;
        Loaded += InitializeShellOnLoaded;
        ObserveEditor(_viewModel.Editor);
        ObserveSelectedServer(_viewModel.SelectedServer);
        RefreshWorkspaceNavigation();
    }

    protected override void OnClosed(EventArgs e)
    {
        Loaded -= InitializeShellOnLoaded;
        _viewModel.PropertyChanged -= ViewModelOnPropertyChanged;
        _localization.LanguageChanged -= LocalizationOnLanguageChanged;
        ObserveEditor(null);
        ObserveSelectedServer(null);
        _capabilitySummary?.Dispose();
        _capabilitySummary = null;
        CapabilityHost.Content = null;
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

    private void InitializeShellOnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= InitializeShellOnLoaded;

        _capabilitySummary = new CapabilitySummaryControl(_capabilityService);
        CapabilityHost.Content = _capabilitySummary;
        UpdateCapabilitySummary();
    }

    private void LocalizationOnLanguageChanged()
    {
        RefreshWorkspaceNavigation();
    }

    private void RefreshWorkspaceNavigation()
    {
        _workspaceNavigationItems.Clear();
        string? previousGroupKey = null;
        var hasServer = _viewModel.SelectedServer is not null;

        foreach (var definition in WorkspaceNavigationCatalog.Items)
        {
            var isAvailable = !definition.RequiresServer || hasServer;
            var group = _localization.Get(definition.GroupKey);
            var title = _localization.Get(definition.TitleKey);
            var description = _localization.Get(definition.DescriptionKey);
            if (!isAvailable)
            {
                description = $"{_localization.Get("Loc.Shell.Workspace.RequiresServer")} {description}";
            }

            _workspaceNavigationItems.Add(
                new WorkspaceNavigationItem(
                    definition.Route,
                    group,
                    title,
                    description,
                    isAvailable,
                    !string.Equals(previousGroupKey, definition.GroupKey, StringComparison.Ordinal)));

            previousGroupKey = definition.GroupKey;
        }
    }

    private void WorkspaceNavigationOnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: WorkspaceNavigationItem item } || !item.IsAvailable)
        {
            return;
        }

        switch (item.Route)
        {
            case WorkspaceNavigationCatalog.GlobalDashboard:
                OpenGlobalDashboardOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Dashboard:
                OpenDashboardOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Explorer:
                OpenExplorerOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Terminal:
                OpenTerminalOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Processes:
                OpenProcessesOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Services:
                OpenServicesOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Docker:
                OpenDockerOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Storage:
                OpenStorageOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Network:
                OpenNetworkOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Logs:
                OpenLogsOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Tunnels:
                OpenPortForwardingOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.ScheduledTasks:
                OpenScheduledTasksOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Git:
                OpenGitOperationsOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Nginx:
                OpenNginxInventoryOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Tls:
                OpenTlsCertificatesOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.EnvironmentFiles:
                OpenEnvironmentFilesOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Deployment:
                OpenDeploymentOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Firewall:
                OpenFirewallOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Users:
                OpenUserAdministrationOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Packages:
                OpenPackageAdministrationOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Databases:
                OpenDatabaseRuntimeOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.DatabaseProfiles:
                OpenDatabaseProfilesOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Backups:
                OpenBackupRestoreOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.OperationHistory:
                OpenOperationHistoryOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.Organize:
                OpenOrganizationOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.ConnectionHistory:
                OpenConnectionHistoryOnClick(sender, e);
                break;
            case WorkspaceNavigationCatalog.ConnectionRoute:
                OpenConnectionRouteOnClick(sender, e);
                break;
        }
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
            RefreshWorkspaceNavigation();
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
}
