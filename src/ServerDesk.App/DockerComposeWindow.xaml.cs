using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Docker;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class DockerComposeWindow : Window
{
    private readonly IDockerComposeService _service;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private CancellationTokenSource? _operationCancellation;
    private IReadOnlyList<DockerComposeProject> _projects = [];
    private DockerComposeProject? _selectedProject;
    private RemoteEditorDocument? _loadedConfig;
    private bool _closed;
    private bool _suppressConfigSelection;

    public DockerComposeWindow(
        IDockerComposeService service,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = initiallyConnected;
        InitializeComponent();

        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        StatusText.Text = initiallyConnected
            ? "Initial: detecting Docker Compose v2 and loading projects."
            : "Disconnected: connect the server before managing Docker Compose.";
        FooterText.Text = "Compose v2 uses the remote Docker CLI over SSH. No Docker socket is exposed or forwarded.";
    }

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initiallyConnected)
        {
            await RefreshAsync();
        }
    }

    private void WindowOnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        CancelOperation();
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void CancelOnClick(object sender, RoutedEventArgs e)
    {
        CancelOperation();
        StatusText.Text = "Cancelled: active Compose work was stopped. Refresh state before retrying any interrupted mutation.";
    }

    private void SearchOnChanged(object sender, TextChangedEventArgs e) => ApplyProjectFilter();

    private async void ProjectSelectionOnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProjectGrid.SelectedItem is not DockerComposeProject project)
        {
            return;
        }

        _selectedProject = project;
        _loadedConfig = null;
        ConfigTextBox.Text = string.Empty;
        _suppressConfigSelection = true;
        ConfigFileBox.ItemsSource = project.ConfigFiles.Select(path => path.Value).ToArray();
        ConfigFileBox.SelectedIndex = project.ConfigFiles.Count > 0 ? 0 : -1;
        _suppressConfigSelection = false;
        ProjectIdentityText.Text = BuildProjectIdentity(project);
        await RefreshSelectedProjectAsync();
        await LoadSelectedConfigAsync();
    }

    private async void RefreshProjectOnClick(object sender, RoutedEventArgs e) => await RefreshSelectedProjectAsync();

    private async void RefreshLogsOnClick(object sender, RoutedEventArgs e)
    {
        if (!TryGetProject(out var project))
        {
            return;
        }

        using var operation = BeginOperation();
        StatusText.Text = $"Loading recent Compose logs for '{project.Name}'…";
        try
        {
            var result = await _service.ReadLogsAsync(
                _profile,
                project,
                _service.Options.DefaultLogRows,
                operation.Token);
            if (!result.IsSuccess)
            {
                ApplyError("Logs", result.Error!);
                return;
            }

            LogList.ItemsSource = result.Lines;
            StatusText.Text = result.Lines.Count == 0
                ? $"Logs empty for '{project.Name}'."
                : $"Loaded {result.Lines.Count:N0} recent plain-text log row(s) for '{project.Name}'.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Compose log read cancelled.";
        }
    }

    private async void ActionOnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string actionText } ||
            !Enum.TryParse<DockerComposeAction>(actionText, out var action) ||
            !TryGetProject(out var project))
        {
            return;
        }

        if (!ConfirmAction(project, action))
        {
            StatusText.Text = $"Cancelled: Compose {DockerComposeService.Verb(action)} was not sent.";
            return;
        }

        using var operation = BeginOperation();
        StatusText.Text = $"Running Compose {DockerComposeService.Verb(action)} for '{project.Name}'…";
        try
        {
            var result = await _service.ExecuteAsync(_profile, project, action, operation.Token);
            if (!result.IsSuccess)
            {
                ApplyError("Action", result.Error!);
                if (result.VerifiedState is not null)
                {
                    ApplyProjectState(result.VerifiedState);
                }

                return;
            }

            StatusText.Text = result.Message;
            if (result.VerifiedState is not null)
            {
                ApplyProjectState(result.VerifiedState);
            }
            else
            {
                await RefreshSelectedProjectAsync();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Compose mutation cancelled/interrupted: completion may be ambiguous. Refresh project state before deciding whether to retry.";
        }
    }

    private async void ConfigFileOnChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_suppressConfigSelection)
        {
            await LoadSelectedConfigAsync();
        }
    }

    private async void ReloadConfigOnClick(object sender, RoutedEventArgs e) => await LoadSelectedConfigAsync();

    private async void SaveConfigOnClick(object sender, RoutedEventArgs e) => await SaveConfigAsync(privileged: false);

    private async void SavePrivilegedConfigOnClick(object sender, RoutedEventArgs e) => await SaveConfigAsync(privileged: true);

    private async Task RefreshAsync()
    {
        if (!CanUse())
        {
            return;
        }

        using var operation = BeginOperation();
        StatusText.Text = "Loading: detecting Docker Compose v2 separately from Docker Engine and reading project inventory…";
        try
        {
            var result = await _service.ListProjectsAsync(_profile, operation.Token);
            if (!result.IsSuccess)
            {
                _projects = [];
                ProjectGrid.ItemsSource = Array.Empty<DockerComposeProject>();
                ServiceGrid.ItemsSource = Array.Empty<DockerComposeServiceInfo>();
                LogList.ItemsSource = Array.Empty<string>();
                ApplyError("Compose", result.Error!);
                FooterText.Text = result.Runtime.Detail;
                return;
            }

            _projects = result.Projects;
            ApplyProjectFilter();
            FooterText.Text = $"Compose {DisplayOrDash(result.Runtime.Version)} · {_projects.Count:N0} project(s). Raw YAML is never silently rewritten.";
            StatusText.Text = _projects.Count == 0
                ? "Empty: Docker Compose v2 is usable but no projects were discovered."
                : $"Loaded {_projects.Count:N0} Docker Compose project(s).";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Compose project refresh cancelled.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Recoverable Compose refresh error: {exception.Message}";
        }
    }

    private async Task RefreshSelectedProjectAsync()
    {
        if (!TryGetProject(out var project))
        {
            return;
        }

        using var operation = BeginOperation();
        StatusText.Text = $"Reading normalized Compose service state for '{project.Name}'…";
        try
        {
            var state = await _service.ReadProjectAsync(_profile, project, operation.Token);
            if (!state.IsSuccess)
            {
                ApplyError("Project", state.Error!);
                return;
            }

            ApplyProjectState(state);
            StatusText.Text = state.Services.Count == 0
                ? $"Project '{project.Name}' currently has no Compose service containers."
                : $"Loaded {state.Services.Count:N0} service container row(s) for '{project.Name}'.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Compose project state refresh cancelled.";
        }
    }

    private async Task LoadSelectedConfigAsync()
    {
        if (!TryGetProject(out var project) || ConfigFileBox.SelectedItem is not string pathText)
        {
            return;
        }

        var path = RemotePath.Parse(pathText);
        using var operation = BeginOperation();
        StatusText.Text = $"Loading raw Compose YAML from {path.Value}…";
        try
        {
            _loadedConfig = await _service.LoadConfigAsync(_profile, project, path, operation.Token);
            ConfigTextBox.Text = _loadedConfig.Text;
            StatusText.Text = $"Raw YAML loaded from {path.Value}. Unsupported/advanced syntax is preserved as text.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Compose config load cancelled.";
        }
        catch (RemoteFileSystemException exception)
        {
            ApplyError("Config", exception.Error);
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Config load error: {exception.Message}";
        }
    }

    private async Task SaveConfigAsync(bool privileged)
    {
        if (!TryGetProject(out var project) || _loadedConfig is null)
        {
            StatusText.Text = "Load a Compose configuration file before saving.";
            return;
        }

        var edited = ConfigTextBox.Text;
        var diff = RemoteEditorDiff.Calculate(_loadedConfig.Text, edited);
        if (diff.TotalChanges == 0)
        {
            StatusText.Text = "No YAML changes to save.";
            return;
        }

        var confirmation = MessageBox.Show(
            this,
            $"Validate and save raw Compose YAML for '{project.Name}'?\n\nFile: {_loadedConfig.Metadata.Path.Value}\nChanges: {diff.Summary}\nMode: {(privileged ? "sudo-preserving UID/GID/mode" : "writable SFTP save")}\n\nThe candidate is validated by Docker Compose before replacement. ServerDesk will not deserialize or rewrite YAML constructs.",
            privileged ? "Confirm privileged Compose save" : "Confirm Compose save",
            MessageBoxButton.OKCancel,
            privileged ? MessageBoxImage.Error : MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        using var operation = BeginOperation();
        StatusText.Text = "Staging candidate YAML and running Docker Compose config validation before replacement…";
        try
        {
            var result = await _service.SaveConfigAsync(
                _profile,
                project,
                _loadedConfig,
                edited,
                privileged,
                operation.Token);
            if (!result.IsSuccess)
            {
                ApplyError(result.ValidationFailed ? "Validation" : "Config save", result.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, result.Message));
                return;
            }

            StatusText.Text = result.Message;
            await LoadSelectedConfigAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Compose config save cancelled/interrupted. Reload the live file before another save.";
        }
    }

    private void ApplyProjectFilter()
    {
        var query = SearchBox.Text.Trim();
        var visible = query.Length == 0
            ? _projects
            : _projects.Where(project =>
                    $"{project.Name} {project.Status} {string.Join(' ', project.ConfigFiles.Select(file => file.Value))}"
                        .Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        ProjectGrid.ItemsSource = visible;
    }

    private void ApplyProjectState(DockerComposeProjectState state)
    {
        ServiceGrid.ItemsSource = state.Services;
        ProjectIdentityText.Text = BuildProjectIdentity(state.Project);
    }

    private bool ConfirmAction(DockerComposeProject project, DockerComposeAction action)
    {
        var verb = DockerComposeService.Verb(action);
        var consequence = action switch
        {
            DockerComposeAction.Down => "Down stops and removes the project's Compose containers and network. ServerDesk does not request volume deletion.",
            DockerComposeAction.Restart => "Restart interrupts running workloads.",
            DockerComposeAction.Up => "Up may create or recreate workload containers to match the current configuration.",
            DockerComposeAction.Pull => "Pull performs a network mutation to update referenced images but does not automatically recreate containers.",
            DockerComposeAction.Build => "Build changes local image artifacts and may consume significant server resources.",
            _ => "This changes Compose project state.",
        };
        return MessageBox.Show(
                this,
                $"Run Docker Compose {verb} for project '{project.Name}'?\n\n{consequence}\n\nConfig: {string.Join(", ", project.ConfigFiles.Select(file => file.Value))}\n\nServerDesk validates config first and will not blindly retry if transport completion is ambiguous.",
                $"Confirm Compose {verb}",
                MessageBoxButton.OKCancel,
                DockerComposeService.Risk(action) == ServerDesk.Domain.Operations.OperationRisk.Destructive
                    ? MessageBoxImage.Error
                    : MessageBoxImage.Warning,
                MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    private bool TryGetProject(out DockerComposeProject project)
    {
        project = _selectedProject!;
        if (_closed || !_initiallyConnected)
        {
            StatusText.Text = "Disconnected: connect the server before using Docker Compose.";
            return false;
        }

        if (_selectedProject is null)
        {
            StatusText.Text = "Select a Compose project first.";
            return false;
        }

        project = _selectedProject;
        return true;
    }

    private bool CanUse()
    {
        if (_closed)
        {
            return false;
        }

        if (!_initiallyConnected)
        {
            StatusText.Text = "Disconnected: connect the server before using Docker Compose.";
            return false;
        }

        return true;
    }

    private OperationScope BeginOperation()
    {
        CancelOperation();
        _operationCancellation = new CancellationTokenSource();
        return new OperationScope(this, _operationCancellation);
    }

    private void CancelOperation()
    {
        if (_operationCancellation is not null && !_operationCancellation.IsCancellationRequested)
        {
            _operationCancellation.Cancel();
        }
    }

    private void ApplyError(string area, RemoteError error)
    {
        StatusText.Text = error.Code switch
        {
            RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => $"{area} permission denied: {error.Message}",
            RemoteErrorCode.CommandNotFound or RemoteErrorCode.CapabilityUnavailable => $"{area} capability unavailable: {error.Message}",
            RemoteErrorCode.UnsupportedVersion => $"{area} unsupported: {error.Message}",
            RemoteErrorCode.PathNotFound => $"{area} path/project not found: {error.Message}",
            RemoteErrorCode.PathConflict => $"{area} conflict: {error.Message}",
            RemoteErrorCode.ParseFailed => $"{area} validation/parse failed: {error.Message}",
            RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.ConnectionFailed => $"{area} disconnected: {error.Message}",
            RemoteErrorCode.AmbiguousState => $"{area} ambiguous state: {error.Message}",
            RemoteErrorCode.OperationCancelled => $"{area} cancelled: {error.Message}",
            _ => $"{area} error ({error.Code}): {error.Message}",
        };
    }

    private static string BuildProjectIdentity(DockerComposeProject project) =>
        $"Project: {project.Name} · Working directory: {project.WorkingDirectory.Value} · Config chain: {string.Join(" + ", project.ConfigFiles.Select(file => file.Value))}";

    private static string DisplayOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private sealed class OperationScope : IDisposable
    {
        private readonly DockerComposeWindow _owner;
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public OperationScope(DockerComposeWindow owner, CancellationTokenSource source)
        {
            _owner = owner;
            _source = source;
        }

        public CancellationToken Token => _source.Token;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (ReferenceEquals(_owner._operationCancellation, _source))
            {
                _owner._operationCancellation = null;
            }

            _source.Dispose();
        }
    }
}
