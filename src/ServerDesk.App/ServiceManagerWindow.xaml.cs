using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Services;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class ServiceManagerWindow : Window
{
    private readonly IServerServiceManager _serviceManager;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private readonly List<ServerServiceInfo> _allServices = [];
    private CancellationTokenSource? _operationCancellation;

    public ServiceManagerWindow(
        IServerServiceManager serviceManager,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _serviceManager = serviceManager ?? throw new ArgumentNullException(nameof(serviceManager));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = initiallyConnected;
        InitializeComponent();
        TitleText.Text = $"Services · {_profile.Name}";
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        StatusText.Text = initiallyConnected
            ? "Ready to load systemd services."
            : "Disconnected: connect the server before loading services.";
        DetailsText.Text = "Select a service, then choose Details for normalized systemd status.";
        FooterText.Text = "No service data loaded.";
    }

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initiallyConnected)
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

    private void SearchBoxOnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ServiceGridOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ServiceGrid.SelectedItem is not ServerServiceInfo service)
        {
            DetailsText.Text = "Select a service, then choose Details for normalized systemd status.";
            return;
        }

        ApplyDetails(service, includeStatus: false);
    }

    private async Task RefreshAsync()
    {
        using var operation = BeginOperation();
        StatusText.Text = "Loading normalized systemd service list…";
        try
        {
            var result = await _serviceManager.ListAsync(_profile, operation.Token);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error!);
                return;
            }

            _allServices.Clear();
            _allServices.AddRange(result.Services);
            ApplyFilter();
            StatusText.Text = _allServices.Count == 0
                ? "Empty: no systemd service units were returned."
                : $"Ready: {_allServices.Count:N0} service(s) loaded.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled: service refresh stopped.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Error: {exception.Message}";
        }
    }

    private async Task LoadSelectedDetailsAsync()
    {
        if (ServiceGrid.SelectedItem is not ServerServiceInfo selected)
        {
            StatusText.Text = "Select a service first.";
            return;
        }

        using var operation = BeginOperation();
        StatusText.Text = $"Loading details for {selected.Unit}…";
        try
        {
            var result = await _serviceManager.GetAsync(_profile, selected.Unit, operation.Token);
            if (!result.IsSuccess || result.Services.Count != 1)
            {
                ApplyError(result.Error ?? new RemoteError(RemoteErrorCode.ParseFailed, "Service details were incomplete."));
                return;
            }

            ApplyDetails(result.Services[0], includeStatus: true);
            StatusText.Text = $"Ready: details loaded for {selected.Unit}.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled: service detail load stopped.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Error: {exception.Message}";
        }
    }

    private async Task ExecuteSelectedAsync(ServerServiceAction action)
    {
        if (ServiceGrid.SelectedItem is not ServerServiceInfo selected)
        {
            StatusText.Text = "Select a service first.";
            return;
        }

        var disruptive = SystemdServiceManager.IsDisruptive(action);
        var verb = action.ToString().ToLowerInvariant();
        var consequence = action switch
        {
            ServerServiceAction.Stop => "This stops the workload and can cause an outage.",
            ServerServiceAction.Restart => "This interrupts the workload while the service restarts.",
            ServerServiceAction.Disable => "This changes boot-time availability and can prevent the service from starting automatically.",
            ServerServiceAction.Start => "This starts the service and may consume resources or expose a workload.",
            ServerServiceAction.Reload => "This asks the service to reload configuration and may affect live traffic.",
            ServerServiceAction.Enable => "This changes boot-time configuration so the service starts automatically.",
            _ => "This changes remote service state.",
        };

        var confirmation = MessageBox.Show(
            this,
            $"Run systemctl {verb} for '{selected.Unit}' on {_profile.Username}@{_profile.Host}:{_profile.Port}?\n\n{consequence}\n\nServerDesk will verify state afterwards. If the transport drops around execution, it will report ambiguous state instead of retrying automatically.",
            disruptive ? $"Confirm disruptive {verb}" : $"Confirm {verb}",
            MessageBoxButton.OKCancel,
            disruptive ? MessageBoxImage.Error : MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        using var operation = BeginOperation();
        StatusText.Text = $"Running systemctl {verb} for {selected.Unit}…";
        try
        {
            var result = await _serviceManager.ExecuteAsync(
                _profile,
                selected.Unit,
                action,
                operation.Token);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error!);
                if (result.VerifiedService is not null)
                {
                    ApplyDetails(result.VerifiedService, includeStatus: true);
                }

                return;
            }

            if (result.VerifiedService is not null)
            {
                ApplyDetails(result.VerifiedService, includeStatus: true);
            }

            StatusText.Text = result.Message;
            await RefreshAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled or interrupted: refresh the service state before deciding whether to retry this action.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Error: {exception.Message}";
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var rows = query.Length == 0
            ? _allServices
            : _allServices.Where(service =>
                    $"{service.Unit} {service.Description} {service.LoadState} {service.ActiveState} {service.SubState} {service.EnabledState}"
                        .Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        ServiceGrid.ItemsSource = rows;
        FooterText.Text = query.Length == 0
            ? $"{rows.Count:N0} service(s)."
            : $"{rows.Count:N0} of {_allServices.Count:N0} service(s) match '{query}'.";
    }

    private void ApplyDetails(ServerServiceInfo service, bool includeStatus)
    {
        DetailsText.Text =
            $"Unit: {service.Unit}\n" +
            $"Description: {service.Description}\n" +
            $"Load: {service.LoadState}\n" +
            $"Active: {service.ActiveState}\n" +
            $"Sub: {service.SubState}\n" +
            $"Enabled: {service.EnabledState}\n" +
            $"Main PID: {service.MainProcessId?.ToString() ?? "—"}" +
            (includeStatus ? $"\n\nStatus: {DisplayOrDash(service.StatusText)}" : "\n\nChoose Details to load Main PID and recent status.");
    }

    private void ApplyError(RemoteError error)
    {
        StatusText.Text = error.Code switch
        {
            RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => $"Permission: {error.Message}",
            RemoteErrorCode.CommandNotFound or RemoteErrorCode.CapabilityUnavailable => $"Capability: {error.Message}",
            RemoteErrorCode.PathNotFound => $"Not found: {error.Message}",
            RemoteErrorCode.NetworkInterrupted => $"Disconnected: {error.Message}",
            RemoteErrorCode.OperationCancelled => $"Cancelled: {error.Message}",
            RemoteErrorCode.AmbiguousState => $"Ambiguous state: {error.Message}",
            _ => $"{error.Code}: {error.Message}",
        };
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

    private static string DisplayOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

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
            }

            _source.Dispose();
        }
    }
}
