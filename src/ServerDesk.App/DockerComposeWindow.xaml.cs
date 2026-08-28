using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ServerDesk.App.Localization;
using ServerDesk.Application.Docker;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class DockerComposeWindow : Window
{
    private readonly IDockerComposeService _service;
    private readonly IRemoteFileEditorService _editorService;
    private readonly ILocalizationService _localization;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private CancellationTokenSource? _operationCancellation;
    private DockerComposeSnapshot? _snapshot;
    private DockerComposeProject? _selectedProject;
    private string _statusKey = "Loc.Compose.Initial";
    private object?[] _statusArguments = [];
    private bool _closed;

    public DockerComposeWindow(
        IDockerComposeService service,
        IRemoteFileEditorService editorService,
        ILocalizationService localization,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _editorService = editorService ?? throw new ArgumentNullException(nameof(editorService));
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = initiallyConnected;
        InitializeComponent();
        _localization.LanguageChanged += OnLanguageChanged;
        ApplyLocalizedChrome();
        SetStatus(initiallyConnected ? "Loc.Compose.Initial" : "Loc.Compose.Disconnected");
        RuntimeText.Text = _localization.Get("Loc.Compose.RuntimeSafety");
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
        CancelActiveOperation();
        _localization.LanguageChanged -= OnLanguageChanged;
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void CancelOnClick(object sender, RoutedEventArgs e)
    {
        CancelActiveOperation();
        SetStatus("Loc.Compose.Cancelled");
    }

    private void SearchBoxOnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async void ProjectGridOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedProject = ProjectGrid.SelectedItem as DockerComposeProject;
        ApplyProjectSelection();
        if (_selectedProject is not null)
        {
            await RefreshProjectAsync(_selectedProject);
        }
    }

    private async void RefreshProjectOnClick(object sender, RoutedEventArgs e)
    {
        if (_selectedProject is not null)
        {
            await RefreshProjectAsync(_selectedProject);
        }
    }

    private async void LogsOnClick(object sender, RoutedEventArgs e)
    {
        var project = _selectedProject;
        if (project is null)
        {
            SetStatus("Loc.Compose.SelectProjectFirst");
            return;
        }

        using var operation = BeginOperation();
        SetStatus("Loc.Compose.LoadingLogs", project.Name);
        try
        {
            var result = await _service.ReadLogsAsync(_profile, project, DockerComposeOptions.Default.DefaultLogRows, operation.Token);
            if (_closed)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                ApplyError(result.Error!);
                return;
            }

            LogList.ItemsSource = result.Lines;
            SetStatus(result.Lines.Count == 0 ? "Loc.Compose.LogsEmpty" : "Loc.Compose.LogsLoaded", result.Lines.Count);
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                SetStatus("Loc.Compose.Cancelled");
            }
        }
        catch (Exception exception)
        {
            SetStatus("Loc.Compose.ErrorDetail", exception.Message);
        }
    }

    private void EditConfigOnClick(object sender, RoutedEventArgs e)
    {
        var project = _selectedProject;
        if (project is null)
        {
            SetStatus("Loc.Compose.SelectProjectFirst");
            return;
        }

        try
        {
            var identity = DockerComposeIdentity.Normalize(project);
            var editor = new RemoteEditorWindow(
                _editorService,
                _profile,
                RemotePath.Parse(identity.PrimaryConfigFile))
            {
                Owner = this,
            };
            editor.ConfigureValidation(_service.BuildConfigValidation(identity));
            editor.Show();
            SetStatus("Loc.Compose.EditorOpened", identity.Name);
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            SetStatus("Loc.Compose.ErrorDetail", exception.Message);
        }
    }

    private async void ActionOnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string actionName } ||
            !Enum.TryParse<DockerComposeAction>(actionName, out var action))
        {
            return;
        }

        var project = _selectedProject;
        if (project is null)
        {
            SetStatus("Loc.Compose.SelectProjectFirst");
            return;
        }

        if (!ConfirmAction(project, action))
        {
            SetStatus("Loc.Compose.ActionNotRun");
            return;
        }

        using var operation = BeginOperation();
        SetActionsEnabled(false);
        SetStatus("Loc.Compose.ActionRunning", _localization.Get(ActionLabelKey(action)), project.Name);
        try
        {
            var result = await _service.ExecuteAsync(_profile, project, action, operation.Token);
            if (_closed)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                ApplyError(result.Error!);
                return;
            }

            SetStatus("Loc.Compose.ActionVerified", _localization.Get(ActionLabelKey(action)), project.Name);
            if (action == DockerComposeAction.Down)
            {
                await RefreshAsync();
            }
            else
            {
                await RefreshProjectAsync(project);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                SetStatus("Loc.Compose.ActionCancelledAmbiguous", project.Name);
            }
        }
        catch (Exception exception)
        {
            SetStatus("Loc.Compose.ErrorDetail", exception.Message);
        }
        finally
        {
            if (!_closed)
            {
                SetActionsEnabled(_selectedProject is not null && _initiallyConnected);
            }
        }
    }

    private async Task RefreshAsync()
    {
        if (!_initiallyConnected)
        {
            SetStatus("Loc.Compose.Disconnected");
            return;
        }

        using var operation = BeginOperation();
        SetActionsEnabled(false);
        SetStatus("Loc.Compose.LoadingProjects");
        try
        {
            var result = await _service.InspectAsync(_profile, operation.Token);
            if (_closed)
            {
                return;
            }

            if (!result.IsSuccess || result.Snapshot is null)
            {
                _snapshot = null;
                ProjectGrid.ItemsSource = Array.Empty<DockerComposeProject>();
                ApplyError(result.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, _localization.Get("Loc.Compose.UnknownError")));
                return;
            }

            _snapshot = result.Snapshot;
            RuntimeText.Text = string.IsNullOrWhiteSpace(_snapshot.Runtime.Version)
                ? _snapshot.Runtime.Detail
                : _localization.Format("Loc.Compose.RuntimeVersion", _snapshot.Runtime.Version, _snapshot.Runtime.Detail);
            ApplyFilter();
            if (!_snapshot.Runtime.IsUsable)
            {
                ApplyRuntimeUnavailable(_snapshot.Runtime);
                return;
            }

            SetStatus(_snapshot.Projects.Count == 0 ? "Loc.Compose.Empty" : "Loc.Compose.Loaded", _snapshot.Projects.Count);
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                SetStatus("Loc.Compose.Cancelled");
            }
        }
        catch (Exception exception)
        {
            SetStatus("Loc.Compose.ErrorDetail", exception.Message);
        }
        finally
        {
            SetActionsEnabled(_selectedProject is not null && _initiallyConnected);
        }
    }

    private async Task RefreshProjectAsync(DockerComposeProject project)
    {
        using var operation = BeginOperation();
        SetActionsEnabled(false);
        SetStatus("Loc.Compose.LoadingProject", project.Name);
        try
        {
            var result = await _service.InspectProjectAsync(_profile, project, operation.Token);
            if (_closed || !ReferenceEquals(project, _selectedProject))
            {
                return;
            }

            if (!result.IsSuccess || result.Details is null)
            {
                ServiceGrid.ItemsSource = Array.Empty<DockerComposeService>();
                ConfigJsonBox.Text = string.Empty;
                ApplyError(result.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, _localization.Get("Loc.Compose.UnknownError")));
                return;
            }

            ServiceGrid.ItemsSource = result.Details.Services;
            ConfigJsonBox.Text = result.Details.NormalizedConfigJson;
            SetStatus("Loc.Compose.ProjectLoaded", project.Name, result.Details.Services.Count);
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                SetStatus("Loc.Compose.Cancelled");
            }
        }
        catch (Exception exception)
        {
            SetStatus("Loc.Compose.ErrorDetail", exception.Message);
        }
        finally
        {
            SetActionsEnabled(_selectedProject is not null && _initiallyConnected);
        }
    }

    private void ApplyFilter()
    {
        var projects = _snapshot?.Projects ?? Array.Empty<DockerComposeProject>();
        ProjectGrid.ItemsSource = DockerComposeProjection.FilterProjects(projects, SearchBox.Text);
    }

    private void ApplyProjectSelection()
    {
        var project = _selectedProject;
        var enabled = project is not null && _initiallyConnected;
        RefreshProjectButton.IsEnabled = enabled;
        LogsButton.IsEnabled = enabled;
        EditConfigButton.IsEnabled = enabled;
        SetActionsEnabled(enabled);
        if (project is null)
        {
            ProjectTitleText.Text = _localization.Get("Loc.Compose.SelectProject");
            ConfigPathText.Text = string.Empty;
            ServiceGrid.ItemsSource = Array.Empty<DockerComposeService>();
            ConfigJsonBox.Text = string.Empty;
            LogList.ItemsSource = Array.Empty<string>();
            return;
        }

        ProjectTitleText.Text = project.Name;
        ConfigPathText.Text = string.Join("  |  ", project.ConfigFiles);
    }

    private bool ConfirmAction(DockerComposeProject project, DockerComposeAction action)
    {
        var actionLabel = _localization.Get(ActionLabelKey(action));
        var impact = _localization.Get(action switch
        {
            DockerComposeAction.Up => "Loc.Compose.Impact.Up",
            DockerComposeAction.Down => "Loc.Compose.Impact.Down",
            DockerComposeAction.Restart => "Loc.Compose.Impact.Restart",
            DockerComposeAction.Pull => "Loc.Compose.Impact.Pull",
            DockerComposeAction.Build => "Loc.Compose.Impact.Build",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        });
        return MessageBox.Show(
                this,
                _localization.Format("Loc.Compose.ConfirmBody", actionLabel, project.Name, impact),
                _localization.Get("Loc.Compose.ConfirmTitle"),
                MessageBoxButton.OKCancel,
                DockerComposeProjection.Risk(action) == OperationRisk.Destructive ? MessageBoxImage.Warning : MessageBoxImage.Information,
                MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    private void ApplyRuntimeUnavailable(DockerComposeRuntimeState runtime)
    {
        SetStatus(runtime.Status switch
        {
            DockerComposeRuntimeStatus.CliUnavailable => "Loc.Compose.CapabilityUnavailable",
            DockerComposeRuntimeStatus.DaemonUnavailable => "Loc.Compose.DaemonUnavailable",
            DockerComposeRuntimeStatus.PermissionDenied => "Loc.Compose.PermissionRequired",
            DockerComposeRuntimeStatus.Unsupported => "Loc.Compose.Unsupported",
            _ => "Loc.Compose.UnknownRuntime",
        });
    }

    private void ApplyError(RemoteError error)
    {
        SetStatus(error.Code switch
        {
            RemoteErrorCode.AmbiguousState => "Loc.Compose.Ambiguous",
            RemoteErrorCode.OperationCancelled => "Loc.Compose.Cancelled",
            RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.ConnectionFailed => "Loc.Compose.DisconnectedDetail",
            RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => "Loc.Compose.PermissionDetail",
            RemoteErrorCode.CommandNotFound or RemoteErrorCode.CapabilityUnavailable => "Loc.Compose.CapabilityDetail",
            RemoteErrorCode.ParseFailed => "Loc.Compose.ParseDetail",
            _ => "Loc.Compose.ErrorDetail",
        }, error.Message);
    }

    private void SetActionsEnabled(bool enabled)
    {
        foreach (var button in FindVisualChildren<Button>(this).Where(button => button.Tag is string))
        {
            button.IsEnabled = enabled;
        }
    }

    private OperationScope BeginOperation()
    {
        CancelActiveOperation();
        _operationCancellation = new CancellationTokenSource();
        return new OperationScope(this, _operationCancellation);
    }

    private void CancelActiveOperation()
    {
        if (_operationCancellation is not null && !_operationCancellation.IsCancellationRequested)
        {
            _operationCancellation.Cancel();
        }
    }

    private void ApplyLocalizedChrome()
    {
        TitleText.Text = _localization.Format("Loc.Compose.Title", _profile.Name);
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        if (_selectedProject is null)
        {
            ProjectTitleText.Text = _localization.Get("Loc.Compose.SelectProject");
        }

        StatusText.Text = _localization.Format(_statusKey, _statusArguments);
    }

    private void OnLanguageChanged() => Dispatcher.Invoke(ApplyLocalizedChrome);

    private void SetStatus(string key, params object?[] arguments)
    {
        _statusKey = key;
        _statusArguments = arguments;
        StatusText.Text = _localization.Format(key, arguments);
    }

    private static string ActionLabelKey(DockerComposeAction action) => action switch
    {
        DockerComposeAction.Up => "Loc.Compose.Up",
        DockerComposeAction.Down => "Loc.Compose.Down",
        DockerComposeAction.Restart => "Loc.Compose.Restart",
        DockerComposeAction.Pull => "Loc.Compose.Pull",
        DockerComposeAction.Build => "Loc.Compose.Build",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

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
