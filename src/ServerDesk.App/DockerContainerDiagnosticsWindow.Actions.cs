using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Docker;
using ServerDesk.Domain.Errors;

namespace ServerDesk.App;

public partial class DockerContainerDiagnosticsWindow
{
    private IDockerContainerActionService? _actionService;
    private IDockerExecTerminalSessionFactory? _execTerminalSessionFactory;
    private bool _containerRemoved;

    public IDockerContainerActionService? ActionService
    {
        get => _actionService;
        set
        {
            _actionService = value;
            SetManagementButtonsEnabled(value is not null && _initiallyConnected && !_containerRemoved);
        }
    }

    public IDockerExecTerminalSessionFactory? ExecTerminalSessionFactory
    {
        get => _execTerminalSessionFactory;
        set
        {
            _execTerminalSessionFactory = value;
            ExecButton.IsEnabled = value is not null && _initiallyConnected && !_containerRemoved;
        }
    }

    private async void ContainerActionOnClick(object sender, RoutedEventArgs e)
    {
        if (_actionService is null || sender is not Button { Tag: string actionText } ||
            !Enum.TryParse<DockerContainerAction>(actionText, out var action))
        {
            StatusText.Text = "Docker lifecycle actions are unavailable in this session.";
            return;
        }

        if (!CanRead() || _containerRemoved)
        {
            return;
        }

        CancelDetails();
        _detailsCancellation = new CancellationTokenSource();
        var source = _detailsCancellation;
        try
        {
            var current = await _service.InspectAsync(_profile, _container.Id, source.Token);
            if (!current.IsSuccess || current.Details is null)
            {
                ApplyError("Action precondition", current.Error!);
                return;
            }

            var details = current.Details;
            ApplyDetails(details);
            UpdateManagementButtons(details);
            if (!ConfirmAction(action, details))
            {
                StatusText.Text = $"Cancelled: Docker {DockerContainerActionService.Verb(action)} was not sent.";
                return;
            }

            CancelStats();
            CancelLogs();
            _statsPolling = false;
            _logsFollowing = false;
            StatusText.Text = $"Running Docker {DockerContainerActionService.Verb(action)} for '{details.Name}'… ServerDesk will verify the resulting state.";
            var result = await _actionService.ExecuteAsync(_profile, _container.Id, action, source.Token);
            if (!result.IsSuccess)
            {
                ApplyActionError(result.Error!);
                if (result.VerifiedDetails is not null)
                {
                    ApplyDetails(result.VerifiedDetails);
                    UpdateManagementButtons(result.VerifiedDetails);
                }

                return;
            }

            StatusText.Text = result.Message;
            if (action == DockerContainerAction.Remove)
            {
                _containerRemoved = true;
                SetManagementButtonsEnabled(false);
                ClearRemovedContainerViews();
                FooterText.Text = "Container removed and verified absent. Close this diagnostics window or refresh Docker inventory.";
                return;
            }

            if (result.VerifiedDetails is not null)
            {
                ApplyDetails(result.VerifiedDetails);
                UpdateManagementButtons(result.VerifiedDetails);
            }

            await RefreshStatsAsync();
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                StatusText.Text = "Action cancelled/interrupted: refresh container state before deciding whether to retry.";
            }
        }
        finally
        {
            DisposeIfCurrent(ref _detailsCancellation, source);
        }
    }

    private async void ExecOnClick(object sender, RoutedEventArgs e)
    {
        if (_execTerminalSessionFactory is null || !CanRead() || _containerRemoved)
        {
            StatusText.Text = "Docker exec terminal is unavailable in this session.";
            return;
        }

        CancelDetails();
        _detailsCancellation = new CancellationTokenSource();
        var source = _detailsCancellation;
        try
        {
            var current = await _service.InspectAsync(_profile, _container.Id, source.Token);
            if (!current.IsSuccess || current.Details is null)
            {
                ApplyError("Exec precondition", current.Error!);
                return;
            }

            var details = current.Details;
            ApplyDetails(details);
            UpdateManagementButtons(details);
            if (!details.State.Running || details.State.Paused)
            {
                StatusText.Text = "Exec unavailable: the container must be running and unpaused.";
                return;
            }

            var window = new DockerExecTerminalWindow(
                _execTerminalSessionFactory,
                _profile,
                _container.Id,
                details.Name)
            {
                Owner = this,
            };
            window.Show();
            StatusText.Text = "Docker exec terminal opened through the existing SSH PTY architecture.";
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                StatusText.Text = "Exec precondition read cancelled.";
            }
        }
        finally
        {
            DisposeIfCurrent(ref _detailsCancellation, source);
        }
    }

    private bool ConfirmAction(DockerContainerAction action, DockerContainerDetails details)
    {
        var verb = DockerContainerActionService.Verb(action);
        if (action is DockerContainerAction.Kill or DockerContainerAction.Remove)
        {
            var identity = string.IsNullOrWhiteSpace(details.Name) ? details.Id[..12] : details.Name;
            var destructiveConsequence = action == DockerContainerAction.Kill
                ? $"SIGKILL immediately terminates '{identity}' without graceful shutdown. Data not flushed by the process may be lost."
                : $"Removing '{identity}' deletes the stopped container object. ServerDesk does not use --force and does not delete attached volumes.";
            var dialog = new DockerDestructiveConfirmationWindow(verb, identity, destructiveConsequence)
            {
                Owner = this,
            };
            return dialog.ShowDialog() == true;
        }

        if (action == DockerContainerAction.Start)
        {
            return true;
        }

        var consequence = action switch
        {
            DockerContainerAction.Stop => "The container will receive Docker's normal stop workflow and its workload becomes unavailable.",
            DockerContainerAction.Restart => "The running workload will be interrupted and restarted.",
            DockerContainerAction.Pause => "All processes in the container will be suspended until explicitly unpaused.",
            DockerContainerAction.Unpause => "Suspended container processes will resume execution.",
            _ => "This changes the remote container state.",
        };
        return MessageBox.Show(
                this,
                $"Run Docker {verb} for '{details.Name}' on {_profile.Username}@{_profile.Host}:{_profile.Port}?\n\n{consequence}\n\nServerDesk will verify state afterwards and will not blindly retry if completion becomes ambiguous.",
                $"Confirm Docker {verb}",
                MessageBoxButton.OKCancel,
                DockerContainerActionService.Risk(action) == ServerDesk.Domain.Operations.OperationRisk.Destructive
                    ? MessageBoxImage.Error
                    : MessageBoxImage.Warning,
                MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    private void UpdateManagementButtons(DockerContainerDetails details)
    {
        if (_actionService is null || _containerRemoved)
        {
            SetManagementButtonsEnabled(false);
            return;
        }

        var running = details.State.Running;
        var paused = details.State.Paused;
        StartButton.IsEnabled = _initiallyConnected && !running;
        StopButton.IsEnabled = _initiallyConnected && running;
        RestartButton.IsEnabled = _initiallyConnected && running;
        PauseButton.IsEnabled = _initiallyConnected && running && !paused;
        UnpauseButton.IsEnabled = _initiallyConnected && running && paused;
        KillButton.IsEnabled = _initiallyConnected && running;
        RemoveButton.IsEnabled = _initiallyConnected && !running;
        ExecButton.IsEnabled = _execTerminalSessionFactory is not null && _initiallyConnected && running && !paused;
    }

    private void SetManagementButtonsEnabled(bool enabled)
    {
        StartButton.IsEnabled = enabled;
        StopButton.IsEnabled = enabled;
        RestartButton.IsEnabled = enabled;
        PauseButton.IsEnabled = enabled;
        UnpauseButton.IsEnabled = enabled;
        KillButton.IsEnabled = enabled;
        RemoveButton.IsEnabled = enabled;
        ExecButton.IsEnabled = enabled && _execTerminalSessionFactory is not null;
    }

    private void ClearRemovedContainerViews()
    {
        EnvironmentGrid.ItemsSource = Array.Empty<DockerEnvironmentVariable>();
        MountGrid.ItemsSource = Array.Empty<DockerContainerMountInfo>();
        DetailNetworkGrid.ItemsSource = Array.Empty<DockerContainerNetworkInfo>();
        LabelGrid.ItemsSource = Array.Empty<KeyValuePair<string, string>>();
        _logBuffer.Clear();
        ApplyLogFilter();
        CpuValue.Text = "—";
        MemoryValue.Text = "—";
        MemoryPercentValue.Text = "—";
        NetworkIoValue.Text = "—";
        BlockIoValue.Text = "—";
        PidsValue.Text = "—";
        StatsFooterText.Text = "Container removed.";
    }

    private void ApplyActionError(RemoteError error)
    {
        StatusText.Text = error.Code switch
        {
            RemoteErrorCode.AmbiguousState => $"Ambiguous state: {error.Message}",
            RemoteErrorCode.PathConflict => $"Action blocked by current state: {error.Message}",
            RemoteErrorCode.PathNotFound => $"Container not found: {error.Message}",
            RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => $"Docker permission denied: {error.Message}",
            RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.ConnectionFailed => $"Disconnected: {error.Message}",
            RemoteErrorCode.OperationCancelled => $"Cancelled/ambiguous: {error.Message}",
            _ => $"Docker action failed ({error.Code}): {error.Message}",
        };
    }
}
