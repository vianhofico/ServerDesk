using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Networking;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class NetworkWindow : Window
{
    private readonly IServerNetworkService _networkService;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private readonly List<InterfaceRow> _allInterfaces = [];
    private readonly List<PortRow> _allPorts = [];
    private CancellationTokenSource? _operationCancellation;

    public NetworkWindow(
        IServerNetworkService networkService,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _networkService = networkService ?? throw new ArgumentNullException(nameof(networkService));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = initiallyConnected;
        InitializeComponent();
        TitleText.Text = $"Network · {_profile.Name}";
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        StatusText.Text = initiallyConnected
            ? "Ready to inspect interfaces and listening ports."
            : "Disconnected: connect the server before loading network state.";
        FooterText.Text = "Read-only network diagnostics. Owner/PID may be hidden by Linux permissions.";
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

    private void CancelOnClick(object sender, RoutedEventArgs e) => CancelActiveOperation();

    private void SearchBoxOnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async Task RefreshAsync()
    {
        using var operation = BeginOperation();
        StatusText.Text = "Sampling interface counters and listening ports…";
        try
        {
            var result = await _networkService.InspectAsync(_profile, operation.Token);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error!);
                return;
            }

            _allInterfaces.Clear();
            _allInterfaces.AddRange(result.Interfaces.Select(InterfaceRow.From));
            _allPorts.Clear();
            _allPorts.AddRange(result.ListeningSockets.Select(PortRow.From));
            ApplyFilter();

            var hiddenOwners = _allPorts.Count(row => !row.OwnerVisible);
            StatusText.Text = _allInterfaces.Count == 0 && _allPorts.Count == 0
                ? "Empty: no network rows were returned."
                : hiddenOwners == 0
                    ? $"Ready: {_allInterfaces.Count:N0} interface(s), {_allPorts.Count:N0} listening socket(s)."
                    : $"Ready: {_allInterfaces.Count:N0} interface(s), {_allPorts.Count:N0} listening socket(s); owner details are unavailable for {hiddenOwners:N0} row(s).";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled: network refresh stopped.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Error: {exception.Message}";
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var interfaces = query.Length == 0
            ? _allInterfaces
            : _allInterfaces.Where(row => row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var ports = query.Length == 0
            ? _allPorts
            : _allPorts.Where(row => row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        InterfaceGrid.ItemsSource = interfaces;
        PortGrid.ItemsSource = ports;
        FooterText.Text = query.Length == 0
            ? $"Read-only · {_allInterfaces.Count:N0} interface(s) · {_allPorts.Count:N0} listening socket(s)."
            : $"Filter '{query}': {interfaces.Count:N0}/{_allInterfaces.Count:N0} interface(s), {ports.Count:N0}/{_allPorts.Count:N0} socket(s).";
    }

    private void ApplyError(RemoteError error)
    {
        StatusText.Text = error.Code switch
        {
            RemoteErrorCode.PermissionDenied => $"Permission: {error.Message}",
            RemoteErrorCode.CommandNotFound or RemoteErrorCode.CapabilityUnavailable => $"Capability: {error.Message}",
            RemoteErrorCode.NetworkInterrupted => $"Disconnected: {error.Message}",
            RemoteErrorCode.OperationCancelled => $"Cancelled: {error.Message}",
            RemoteErrorCode.ParseFailed => $"Unsupported output: {error.Message}",
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

    private static string FormatBytes(double bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var value = Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private sealed record InterfaceRow(
        string Name,
        string State,
        string Addresses,
        string MacAddress,
        string Mtu,
        string RxTotal,
        string TxTotal,
        string RxRate,
        string TxRate,
        string SearchText)
    {
        public static InterfaceRow From(ServerNetworkInterfaceInfo info)
        {
            var addresses = info.Addresses.Count == 0
                ? "—"
                : string.Join(", ", info.Addresses.Select(address => $"{address.Address}/{address.PrefixLength}"));
            var mtu = info.Mtu?.ToString(CultureInfo.InvariantCulture) ?? "—";
            var mac = string.IsNullOrWhiteSpace(info.MacAddress) ? "—" : info.MacAddress;
            return new InterfaceRow(
                info.Name,
                info.OperationalState,
                addresses,
                mac,
                mtu,
                FormatBytes(info.RxBytes),
                FormatBytes(info.TxBytes),
                FormatBytes(info.RxBytesPerSecond) + "/s",
                FormatBytes(info.TxBytesPerSecond) + "/s",
                $"{info.Name} {info.OperationalState} {addresses} {mac} {mtu}");
        }
    }

    private sealed record PortRow(
        string Protocol,
        string State,
        string LocalAddress,
        int Port,
        string ProcessId,
        string ProcessName,
        string OwnerStatus,
        bool OwnerVisible,
        string SearchText)
    {
        public static PortRow From(ServerListeningSocketInfo info)
        {
            var pid = info.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "—";
            var process = string.IsNullOrWhiteSpace(info.ProcessName) ? "—" : info.ProcessName;
            var owner = info.OwnerVisible ? "Visible" : "Unavailable";
            return new PortRow(
                info.Protocol,
                info.State,
                info.LocalAddress,
                info.Port,
                pid,
                process,
                owner,
                info.OwnerVisible,
                $"{info.Protocol} {info.State} {info.LocalAddress} {info.Port} {pid} {process} {owner}");
        }
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly NetworkWindow _owner;
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public OperationScope(NetworkWindow owner, CancellationTokenSource source)
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
