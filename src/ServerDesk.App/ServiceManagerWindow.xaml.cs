using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Logs;
using ServerDesk.Application.Services;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class ServiceManagerWindow : Window
{
    private readonly IServerServiceManager _serviceManager;
    private readonly IServerLogService _logService;
    private readonly ServerProfile _profile;
    private readonly List<ServerServiceInfo> _allServices = [];
    private CancellationTokenSource? _operationCancellation;
    private ServiceUiState _currentState;
    private bool _hasLoadedSnapshot;
    private bool _hasConnection;

    public ServiceManagerWindow(
        IServerServiceManager serviceManager,
        IServerLogService logService,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _serviceManager = serviceManager ?? throw new ArgumentNullException(nameof(serviceManager));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _hasConnection = initiallyConnected;
        InitializeComponent();

        ServerNameText.Text = _profile.Name;
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        if (string.IsNullOrWhiteSpace(_profile.Environment))
        {
            EnvironmentValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Services.Header.Unlabeled");
        }
        else
        {
            EnvironmentValueText.Text = _profile.Environment;
        }

        RefreshConnectionLabel();
        SetStatusResource(
            initiallyConnected ? ServiceUiState.Ready : ServiceUiState.Disconnected,
            initiallyConnected ? "Loc.Services.Status.ReadyInitial" : "Loc.Services.Status.DisconnectedInitial");
        FooterText.SetResourceReference(TextBlock.TextProperty, "Loc.Services.Footer.None");
        UpdateSelectionPresentation();
        ApplyFilter();
    }

    private bool IsBusy => _operationCancellation is not null;

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hasConnection)
        {
            await RefreshAsync();
        }
    }

    private void WindowOnClosed(object? sender, EventArgs e) => CancelActiveOperation();

    private async void RefreshOnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async void DetailsOnClick(object sender, RoutedEventArgs e) => await LoadSelectedDetailsAsync();

    private void CancelOnClick(object sender, RoutedEventArgs e) => CancelActiveOperation();

    private async void StartOnClick(object sender, RoutedEventArgs e) => await ExecuteSelectedAsync(ServerServiceAction.Start);

    private async void StopOnClick(object sender, RoutedEventArgs e) => await ExecuteSelectedAsync(ServerServiceAction.Stop);

    private async void RestartOnClick(object sender, RoutedEventArgs e) => await ExecuteSelectedAsync(ServerServiceAction.Restart);

    private async void ReloadOnClick(object sender, RoutedEventArgs e) => await ExecuteSelectedAsync(ServerServiceAction.Reload);

    private async void EnableOnClick(object sender, RoutedEventArgs e) => await ExecuteSelectedAsync(ServerServiceAction.Enable);

    private async void DisableOnClick(object sender, RoutedEventArgs e) => await ExecuteSelectedAsync(ServerServiceAction.Disable);

    private void LogsOnClick(object sender, RoutedEventArgs e)
    {
        if (ServiceGrid.SelectedItem is not ServerServiceInfo selected)
        {
            SetStatusResource(ServiceUiState.Error, "Loc.Services.Status.SelectFirst");
            ApplyFilter();
            return;
        }

        var window = new LogViewerWindow(
            _logService,
            _profile,
            _hasConnection,
            selected.Unit)
        {
            Owner = this,
        };
        window.Show();
    }

    private void SearchBoxOnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ClearSearchOnClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void ServiceGridOnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionPresentation();

    private async Task RefreshAsync()
    {
        using var operation = BeginOperation();
        SetStatusResource(ServiceUiState.Loading, "Loc.Services.Status.Loading");
        ApplyFilter();

        try
        {
            var result = await _serviceManager.ListAsync(_profile, operation.Token);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error!);
                ApplyFilter();
                return;
            }

            _allServices.Clear();
            _allServices.AddRange(result.Services);
            _hasLoadedSnapshot = true;
            _hasConnection = true;
            RefreshConnectionLabel();
            SetStatusRaw(
                _allServices.Count == 0 ? ServiceUiState.Empty : ServiceUiState.Ready,
                _allServices.Count == 0
                    ? Localize("Loc.Services.Status.Empty")
                    : FormatLocalize("Loc.Services.Status.Loaded", _allServices.Count));
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            SetStatusResource(ServiceUiState.Cancelled, "Loc.Services.Status.RefreshCancelled");
            ApplyFilter();
        }
        catch (Exception exception)
        {
            SetStatusRaw(ServiceUiState.Error, FormatLocalize("Loc.Services.Status.Error", exception.Message));
            ApplyFilter();
        }
    }

    private async Task LoadSelectedDetailsAsync()
    {
        if (ServiceGrid.SelectedItem is not ServerServiceInfo selected)
        {
            SetStatusResource(ServiceUiState.Error, "Loc.Services.Status.SelectFirst");
            ApplyFilter();
            return;
        }

        using var operation = BeginOperation();
        SetStatusRaw(ServiceUiState.Loading, FormatLocalize("Loc.Services.Status.LoadingDetails", selected.Unit));
        ApplyFilter();
        try
        {
            var result = await _serviceManager.GetAsync(_profile, selected.Unit, operation.Token);
            if (!result.IsSuccess || result.Services.Count != 1)
            {
                ApplyError(result.Error ?? new RemoteError(
                    RemoteErrorCode.ParseFailed,
                    Localize("Loc.Services.Status.IncompleteDetails")));
                ApplyFilter();
                return;
            }

            ApplyDetails(result.Services[0], includeStatus: true);
            SetStatusRaw(ServiceUiState.Ready, FormatLocalize("Loc.Services.Status.DetailsLoaded", selected.Unit));
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            SetStatusResource(ServiceUiState.Cancelled, "Loc.Services.Status.DetailsCancelled");
            ApplyFilter();
        }
        catch (Exception exception)
        {
            SetStatusRaw(ServiceUiState.Error, FormatLocalize("Loc.Services.Status.Error", exception.Message));
            ApplyFilter();
        }
    }

    private async Task ExecuteSelectedAsync(ServerServiceAction action)
    {
        if (ServiceGrid.SelectedItem is not ServerServiceInfo selected)
        {
            SetStatusResource(ServiceUiState.Error, "Loc.Services.Status.SelectFirst");
            ApplyFilter();
            return;
        }

        if (!ServiceWorkspaceProjection.CanExecute(selected, action))
        {
            UpdateCommandState();
            return;
        }

        var disruptive = SystemdServiceManager.IsDisruptive(action);
        var verb = action.ToString().ToLowerInvariant();
        var actionLabel = Localize($"Loc.Services.Command.{action}");
        var consequence = Localize($"Loc.Services.Consequence.{action}");
        var endpoint = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        var confirmation = MessageBox.Show(
            this,
            FormatLocalize("Loc.Services.Confirm.Body", verb, selected.Unit, endpoint, consequence),
            FormatLocalize(
                disruptive ? "Loc.Services.Confirm.DisruptiveTitle" : "Loc.Services.Confirm.Title",
                actionLabel),
            MessageBoxButton.OKCancel,
            disruptive ? MessageBoxImage.Error : MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        using var operation = BeginOperation();
        SetStatusRaw(ServiceUiState.Loading, FormatLocalize("Loc.Services.Status.Running", verb, selected.Unit));
        ApplyFilter();
        try
        {
            var result = await _serviceManager.ExecuteAsync(_profile, selected.Unit, action, operation.Token);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error!);
                if (result.VerifiedService is not null)
                {
                    ApplyDetails(result.VerifiedService, includeStatus: true);
                }

                ApplyFilter();
                return;
            }

            if (result.VerifiedService is not null)
            {
                ApplyDetails(result.VerifiedService, includeStatus: true);
            }

            SetStatusRaw(ServiceUiState.Ready, result.Message);
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            SetStatusResource(ServiceUiState.Cancelled, "Loc.Services.Status.Interrupted");
            ApplyFilter();
        }
        catch (Exception exception)
        {
            SetStatusRaw(ServiceUiState.Error, FormatLocalize("Loc.Services.Status.Error", exception.Message));
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text;
        var visible = ServiceWorkspaceProjection.Filter(_allServices, query);
        ServiceGrid.ItemsSource = visible;
        ClearSearchButton.Visibility = string.IsNullOrWhiteSpace(query) ? Visibility.Collapsed : Visibility.Visible;

        var summary = ServiceWorkspaceProjection.Summarize(_allServices, visible);
        TotalServicesValue.Text = summary.TotalServices.ToString("N0", CultureInfo.CurrentCulture);
        VisibleServicesValue.Text = summary.VisibleServices.ToString("N0", CultureInfo.CurrentCulture);
        ActiveServicesValue.Text = summary.ActiveServices.ToString("N0", CultureInfo.CurrentCulture);
        EnabledServicesValue.Text = summary.EnabledServices.ToString("N0", CultureInfo.CurrentCulture);

        if (!_hasLoadedSnapshot)
        {
            FooterText.SetResourceReference(TextBlock.TextProperty, "Loc.Services.Footer.None");
        }
        else if (string.IsNullOrWhiteSpace(query))
        {
            FooterText.Text = FormatLocalize("Loc.Services.Footer.All", visible.Count);
        }
        else
        {
            FooterText.Text = FormatLocalize("Loc.Services.Footer.Filtered", visible.Count, _allServices.Count, query.Trim());
        }

        RefreshGridOverlay(visible, query);
        UpdateCommandState();
    }

    private void UpdateSelectionPresentation()
    {
        if (ServiceGrid.SelectedItem is not ServerServiceInfo service)
        {
            DetailsHintText.Visibility = Visibility.Visible;
            DetailsContent.Visibility = Visibility.Collapsed;
            TargetValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Services.Command.NoTarget");
            UpdateCommandState();
            return;
        }

        TargetValueText.Text = FormatLocalize("Loc.Services.Command.TargetValue", service.Unit);
        ApplyDetails(service, includeStatus: false);
        UpdateCommandState();
    }

    private void ApplyDetails(ServerServiceInfo service, bool includeStatus)
    {
        DetailsHintText.Visibility = Visibility.Collapsed;
        DetailsContent.Visibility = Visibility.Visible;
        DetailUnitText.Text = service.Unit;
        DetailDescriptionText.Text = DisplayOrDash(service.Description);
        DetailLoadText.Text = DisplayOrDash(service.LoadState);
        DetailActiveText.Text = DisplayOrDash(service.ActiveState);
        DetailSubText.Text = DisplayOrDash(service.SubState);
        DetailEnabledText.Text = DisplayOrDash(service.EnabledState);
        DetailMainPidText.Text = includeStatus
            ? service.MainProcessId?.ToString(CultureInfo.InvariantCulture) ?? "—"
            : "—";
        if (includeStatus)
        {
            DetailStatusText.Text = DisplayOrDash(service.StatusText);
        }
        else
        {
            DetailStatusText.SetResourceReference(TextBlock.TextProperty, "Loc.Services.Details.NotLoaded");
        }
    }

    private void ApplyError(RemoteError error)
    {
        var state = error.Code switch
        {
            RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.ConnectionFailed => ServiceUiState.Disconnected,
            RemoteErrorCode.OperationCancelled => ServiceUiState.Cancelled,
            _ => ServiceUiState.Error,
        };
        var key = error.Code switch
        {
            RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => "Loc.Services.Status.Permission",
            RemoteErrorCode.CommandNotFound or RemoteErrorCode.CapabilityUnavailable => "Loc.Services.Status.Capability",
            RemoteErrorCode.PathNotFound => "Loc.Services.Status.NotFound",
            RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.ConnectionFailed => "Loc.Services.Status.Disconnected",
            RemoteErrorCode.OperationCancelled => "Loc.Services.Status.Cancelled",
            RemoteErrorCode.AmbiguousState => "Loc.Services.Status.Ambiguous",
            _ => "Loc.Services.Status.Typed",
        };

        if (state == ServiceUiState.Disconnected)
        {
            _hasConnection = false;
            RefreshConnectionLabel();
        }

        SetStatusRaw(
            state,
            key == "Loc.Services.Status.Typed"
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
        var selected = ServiceGrid.SelectedItem as ServerServiceInfo;
        var busy = IsBusy;
        var canUseSelection = !busy && _hasConnection && selected is not null;

        RefreshButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        DetailsButton.IsEnabled = canUseSelection;
        LogsButton.IsEnabled = canUseSelection;
        StartButton.IsEnabled = canUseSelection && ServiceWorkspaceProjection.CanExecute(selected!, ServerServiceAction.Start);
        StopButton.IsEnabled = canUseSelection && ServiceWorkspaceProjection.CanExecute(selected!, ServerServiceAction.Stop);
        RestartButton.IsEnabled = canUseSelection && ServiceWorkspaceProjection.CanExecute(selected!, ServerServiceAction.Restart);
        ReloadButton.IsEnabled = canUseSelection && ServiceWorkspaceProjection.CanExecute(selected!, ServerServiceAction.Reload);
        EnableButton.IsEnabled = canUseSelection && ServiceWorkspaceProjection.CanExecute(selected!, ServerServiceAction.Enable);
        DisableButton.IsEnabled = canUseSelection && ServiceWorkspaceProjection.CanExecute(selected!, ServerServiceAction.Disable);
        ServiceGrid.IsEnabled = !busy;
        SearchBox.IsEnabled = !busy;
        ClearSearchButton.IsEnabled = !busy;
    }

    private void RefreshConnectionLabel() => ConnectionValueText.SetResourceReference(
        TextBlock.TextProperty,
        _hasConnection ? "Loc.Services.Header.Connected" : "Loc.Services.Header.Disconnected");

    private void SetStatusResource(ServiceUiState state, string resourceKey)
    {
        _currentState = state;
        StatusText.SetResourceReference(TextBlock.TextProperty, resourceKey);
        RefreshStatusChrome();
    }

    private void SetStatusRaw(ServiceUiState state, string message)
    {
        _currentState = state;
        StatusText.Text = message;
        RefreshStatusChrome();
    }

    private void RefreshStatusChrome()
    {
        StatusCard.Style = (Style)FindResource(_currentState == ServiceUiState.Error ? "InlineErrorCard" : "InlineInfoCard");
    }

    private void RefreshGridOverlay(IReadOnlyCollection<ServerServiceInfo> visibleRows, string? query)
    {
        if (_currentState == ServiceUiState.Loading)
        {
            ShowOverlay("Loc.Services.Overlay.LoadingTitle", rawDetail: StatusText.Text);
            return;
        }

        if (_hasLoadedSnapshot && _allServices.Count == 0)
        {
            ShowOverlay("Loc.Services.Overlay.EmptyTitle", "Loc.Services.Overlay.EmptyDetail");
            return;
        }

        if (_hasLoadedSnapshot && _allServices.Count > 0 && visibleRows.Count == 0 && !string.IsNullOrWhiteSpace(query))
        {
            ShowOverlay("Loc.Services.Overlay.SearchTitle", "Loc.Services.Overlay.SearchDetail");
            return;
        }

        if (!_hasLoadedSnapshot && _currentState == ServiceUiState.Disconnected)
        {
            ShowOverlay("Loc.Services.Overlay.DisconnectedTitle", "Loc.Services.Status.DisconnectedInitial");
            return;
        }

        if (_allServices.Count == 0 && _currentState == ServiceUiState.Error)
        {
            ShowOverlay("Loc.Services.Overlay.ErrorTitle", rawDetail: StatusText.Text);
            return;
        }

        if (_allServices.Count == 0 && _currentState == ServiceUiState.Cancelled)
        {
            ShowOverlay("Loc.Services.Overlay.CancelledTitle", rawDetail: StatusText.Text);
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

    private static string DisplayOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string Localize(string key) => System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

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

    private enum ServiceUiState
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
        private readonly ServiceManagerWindow _owner;
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public OperationScope(ServiceManagerWindow owner, CancellationTokenSource source)
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
