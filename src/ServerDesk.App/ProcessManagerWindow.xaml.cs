using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Processes;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class ProcessManagerWindow : Window
{
    private readonly IServerProcessService _processService;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private IReadOnlyList<ProcessWorkspaceRow> _allRows = [];
    private CancellationTokenSource? _operationCancellation;
    private ProcessUiState _currentState;
    private bool _hasLoadedSnapshot;

    public ProcessManagerWindow(
        IServerProcessService processService,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _processService = processService ?? throw new ArgumentNullException(nameof(processService));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = initiallyConnected;
        InitializeComponent();

        ServerNameText.Text = _profile.Name;
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        if (string.IsNullOrWhiteSpace(_profile.Environment))
        {
            EnvironmentValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Processes.Header.Unlabeled");
        }
        else
        {
            EnvironmentValueText.Text = _profile.Environment;
        }

        ConnectionValueText.SetResourceReference(
            TextBlock.TextProperty,
            _initiallyConnected ? "Loc.Processes.Header.Connected" : "Loc.Processes.Header.Disconnected");
        SetStatusResource(
            _initiallyConnected ? ProcessUiState.Ready : ProcessUiState.Disconnected,
            _initiallyConnected ? "Loc.Processes.Status.ReadyInitial" : "Loc.Processes.Status.DisconnectedInitial");
        FooterText.SetResourceReference(TextBlock.TextProperty, "Loc.Processes.Footer.None");
        UpdateSelectionPresentation();
        ApplyFilter();
    }

    private bool IsBusy => _operationCancellation is not null;

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initiallyConnected)
        {
            await RefreshAsync();
        }
    }

    private void WindowOnClosed(object? sender, EventArgs e) => CancelActiveOperation();

    private async void RefreshOnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void CancelOnClick(object sender, RoutedEventArgs e) => CancelActiveOperation();

    private async void TerminateOnClick(object sender, RoutedEventArgs e) =>
        await SignalSelectedAsync(ServerProcessSignal.Terminate);

    private async void ForceKillOnClick(object sender, RoutedEventArgs e) =>
        await SignalSelectedAsync(ServerProcessSignal.ForceKill);

    private void SearchBoxOnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ClearSearchOnClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void ProcessGridOnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateSelectionPresentation();

    private async Task RefreshAsync()
    {
        using var operation = BeginOperation();
        SetStatusResource(ProcessUiState.Loading, "Loc.Processes.Status.Loading");
        ApplyFilter();

        try
        {
            var result = await _processService.ListAsync(_profile, operation.Token);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error!);
                ApplyFilter();
                return;
            }

            _allRows = ProcessWorkspaceProjection.Project(result.Processes);
            _hasLoadedSnapshot = true;
            ConnectionValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Processes.Header.Connected");
            if (_allRows.Count == 0)
            {
                SetStatusResource(ProcessUiState.Empty, "Loc.Processes.Status.Empty");
            }
            else
            {
                SetStatusRaw(
                    ProcessUiState.Ready,
                    FormatLocalize("Loc.Processes.Status.Loaded", _allRows.Count));
            }

            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            SetStatusResource(ProcessUiState.Cancelled, "Loc.Processes.Status.RefreshCancelled");
            ApplyFilter();
        }
        catch (Exception exception)
        {
            SetStatusRaw(
                ProcessUiState.Error,
                FormatLocalize("Loc.Processes.Status.Error", exception.Message));
            ApplyFilter();
        }
    }

    private async Task SignalSelectedAsync(ServerProcessSignal signal)
    {
        if (ProcessGrid.SelectedItem is not ProcessWorkspaceRow row)
        {
            SetStatusResource(ProcessUiState.Error, "Loc.Processes.Status.SelectFirst");
            return;
        }

        if (row.ProcessId <= 1)
        {
            SetStatusResource(ProcessUiState.Error, "Loc.Processes.Status.ProtectedPid");
            return;
        }

        var force = signal == ServerProcessSignal.ForceKill;
        var prompt = force
            ? FormatLocalize("Loc.Processes.Confirm.ForceKill.Body", row.ProcessId, row.Command)
            : FormatLocalize("Loc.Processes.Confirm.Terminate.Body", row.ProcessId, row.Command);
        if (MessageBox.Show(
                this,
                prompt,
                Localize(force
                    ? "Loc.Processes.Confirm.ForceKill.Title"
                    : "Loc.Processes.Confirm.Terminate.Title"),
                MessageBoxButton.OKCancel,
                force ? MessageBoxImage.Error : MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        if (force && MessageBox.Show(
                this,
                FormatLocalize("Loc.Processes.Confirm.ForceKillFinal.Body", row.ProcessId, row.Command),
                Localize("Loc.Processes.Confirm.ForceKillFinal.Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Error,
                MessageBoxResult.No) != MessageBoxResult.Yes)
        {
            return;
        }

        using var operation = BeginOperation();
        SetStatusRaw(
            ProcessUiState.Loading,
            FormatLocalize(
                force ? "Loc.Processes.Status.ForceKilling" : "Loc.Processes.Status.Terminating",
                row.ProcessId));
        ApplyFilter();

        try
        {
            var result = await _processService.SignalAsync(
                _profile,
                row.ProcessId,
                signal,
                operation.Token);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error!);
                ApplyFilter();
                return;
            }

            SetStatusRaw(
                ProcessUiState.Loading,
                FormatLocalize("Loc.Processes.Status.Verify", result.Message));
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            SetStatusResource(ProcessUiState.Cancelled, "Loc.Processes.Status.Interrupted");
            ApplyFilter();
        }
        catch (Exception exception)
        {
            SetStatusRaw(
                ProcessUiState.Error,
                FormatLocalize("Loc.Processes.Status.Error", exception.Message));
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text;
        var visibleRows = ProcessWorkspaceProjection.Filter(_allRows, query);
        ProcessGrid.ItemsSource = visibleRows;
        ClearSearchButton.Visibility = string.IsNullOrWhiteSpace(query)
            ? Visibility.Collapsed
            : Visibility.Visible;

        var summary = ProcessWorkspaceProjection.Summarize(_allRows, visibleRows);
        TotalProcessesValue.Text = summary.TotalProcesses.ToString("N0", CultureInfo.CurrentCulture);
        VisibleProcessesValue.Text = summary.VisibleProcesses.ToString("N0", CultureInfo.CurrentCulture);
        UsersValue.Text = summary.UserCount.ToString("N0", CultureInfo.CurrentCulture);
        ResidentMemoryValue.Text = summary.ResidentMemoryText;

        if (!_hasLoadedSnapshot)
        {
            FooterText.SetResourceReference(TextBlock.TextProperty, "Loc.Processes.Footer.None");
        }
        else if (string.IsNullOrWhiteSpace(query))
        {
            FooterText.Text = FormatLocalize("Loc.Processes.Footer.All", visibleRows.Count);
        }
        else
        {
            FooterText.Text = FormatLocalize(
                "Loc.Processes.Footer.Filtered",
                visibleRows.Count,
                _allRows.Count,
                query.Trim());
        }

        RefreshGridOverlay(visibleRows, query);
        UpdateCommandState();
    }

    private void UpdateSelectionPresentation()
    {
        if (ProcessGrid.SelectedItem is not ProcessWorkspaceRow row)
        {
            DetailsHintText.Visibility = Visibility.Visible;
            DetailsContent.Visibility = Visibility.Collapsed;
            TargetValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Processes.Command.NoTarget");
            UpdateCommandState();
            return;
        }

        DetailsHintText.Visibility = Visibility.Collapsed;
        DetailsContent.Visibility = Visibility.Visible;
        DetailPidText.Text = row.ProcessId.ToString(CultureInfo.InvariantCulture);
        DetailParentPidText.Text = row.ParentProcessId.ToString(CultureInfo.InvariantCulture);
        DetailUserText.Text = row.User;
        DetailStateText.Text = row.State;
        DetailCpuText.Text = row.CpuText;
        DetailMemoryText.Text = row.MemoryText;
        DetailElapsedText.Text = row.ElapsedText;
        DetailCommandText.Text = row.Command;
        DetailArgumentsText.Text = row.Arguments;
        TargetValueText.Text = FormatLocalize("Loc.Processes.Command.TargetValue", row.ProcessId, row.Command);
        UpdateCommandState();
    }

    private void ApplyError(RemoteError error)
    {
        var state = error.Code switch
        {
            RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.ConnectionFailed => ProcessUiState.Disconnected,
            RemoteErrorCode.OperationCancelled => ProcessUiState.Cancelled,
            _ => ProcessUiState.Error,
        };
        var key = error.Code switch
        {
            RemoteErrorCode.PermissionDenied => "Loc.Processes.Status.Permission",
            RemoteErrorCode.CommandNotFound or RemoteErrorCode.CapabilityUnavailable => "Loc.Processes.Status.Capability",
            RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.ConnectionFailed => "Loc.Processes.Status.Disconnected",
            RemoteErrorCode.OperationCancelled => "Loc.Processes.Status.Cancelled",
            RemoteErrorCode.AmbiguousState => "Loc.Processes.Status.Ambiguous",
            _ => "Loc.Processes.Status.Typed",
        };

        if (state == ProcessUiState.Disconnected)
        {
            ConnectionValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Processes.Header.Disconnected");
        }

        SetStatusRaw(
            state,
            key == "Loc.Processes.Status.Typed"
                ? FormatLocalize(key, error.Code, error.Message)
                : FormatLocalize(key, error.Message));
    }

    private OperationScope BeginOperation()
    {
        CancelActiveOperation();
        _operationCancellation = new CancellationTokenSource();
        UpdateCommandState();
        return new OperationScope(this, _operationCancellation);
    }

    private void CancelActiveOperation()
    {
        if (_operationCancellation is not null && !_operationCancellation.IsCancellationRequested)
        {
            _operationCancellation.Cancel();
        }
    }

    private void UpdateCommandState()
    {
        var selected = ProcessGrid.SelectedItem as ProcessWorkspaceRow;
        var busy = IsBusy;
        var signalEligible = selected is { ProcessId: > 1 };

        RefreshButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        TerminateButton.IsEnabled = !busy && signalEligible;
        ForceKillButton.IsEnabled = !busy && signalEligible;
        ProcessGrid.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        ClearSearchButton.IsEnabled = !busy;
    }

    private void SetStatusResource(ProcessUiState state, string resourceKey)
    {
        _currentState = state;
        StatusText.SetResourceReference(TextBlock.TextProperty, resourceKey);
        RefreshStatusChrome();
    }

    private void SetStatusRaw(ProcessUiState state, string message)
    {
        _currentState = state;
        StatusText.Text = message;
        RefreshStatusChrome();
    }

    private void RefreshStatusChrome()
    {
        StatusCard.Style = (Style)FindResource(
            _currentState == ProcessUiState.Error ? "InlineErrorCard" : "InlineInfoCard");
    }

    private void RefreshGridOverlay(
        IReadOnlyCollection<ProcessWorkspaceRow> visibleRows,
        string? query)
    {
        if (_currentState == ProcessUiState.Loading)
        {
            ShowOverlay("Loc.Processes.Overlay.LoadingTitle", rawDetail: StatusText.Text);
            return;
        }

        if (_hasLoadedSnapshot && _allRows.Count == 0)
        {
            ShowOverlay("Loc.Processes.Overlay.EmptyTitle", "Loc.Processes.Overlay.EmptyDetail");
            return;
        }

        if (_hasLoadedSnapshot &&
            _allRows.Count > 0 &&
            visibleRows.Count == 0 &&
            !string.IsNullOrWhiteSpace(query))
        {
            ShowOverlay("Loc.Processes.Overlay.SearchTitle", "Loc.Processes.Overlay.SearchDetail");
            return;
        }

        if (!_hasLoadedSnapshot && _currentState == ProcessUiState.Disconnected)
        {
            ShowOverlay("Loc.Processes.Overlay.DisconnectedTitle", "Loc.Processes.Status.DisconnectedInitial");
            return;
        }

        if (_allRows.Count == 0 && _currentState == ProcessUiState.Error)
        {
            ShowOverlay("Loc.Processes.Overlay.ErrorTitle", rawDetail: StatusText.Text);
            return;
        }

        if (_allRows.Count == 0 && _currentState == ProcessUiState.Cancelled)
        {
            ShowOverlay("Loc.Processes.Overlay.CancelledTitle", rawDetail: StatusText.Text);
            return;
        }

        GridStateOverlay.Visibility = Visibility.Collapsed;
    }

    private void ShowOverlay(string titleKey, string? detailKey = null, string? rawDetail = null)
    {
        GridStateOverlay.Visibility = Visibility.Visible;
        GridStateTitle.SetResourceReference(TextBlock.TextProperty, titleKey);
        if (detailKey is not null)
        {
            GridStateDetail.SetResourceReference(TextBlock.TextProperty, detailKey);
        }
        else
        {
            GridStateDetail.Text = rawDetail ?? string.Empty;
        }
    }

    private static string Localize(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

    private static string FormatLocalize(string key, params object?[] arguments)
    {
        var template = Localize(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private enum ProcessUiState
    {
        Disconnected,
        Ready,
        Loading,
        Empty,
        Error,
        Cancelled,
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly ProcessManagerWindow _owner;
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public OperationScope(ProcessManagerWindow owner, CancellationTokenSource source)
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
                _owner.UpdateCommandState();
            }

            _source.Dispose();
        }
    }
}
