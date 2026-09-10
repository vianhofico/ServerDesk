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
    private readonly List<InterfaceRow> _allInterfaces = [];
    private readonly List<PortRow> _allPorts = [];
    private CancellationTokenSource? _operationCancellation;
    private bool _hasConnection;

    public NetworkWindow(
        IServerNetworkService networkService,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _networkService = networkService ?? throw new ArgumentNullException(nameof(networkService));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _hasConnection = initiallyConnected;
        InitializeComponent();

        ServerNameText.Text = _profile.Name;
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        if (string.IsNullOrWhiteSpace(_profile.Environment))
        {
            EnvironmentValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Network.Header.Unlabeled");
        }
        else
        {
            EnvironmentValueText.Text = _profile.Environment;
        }

        RefreshConnectionLabel();
        SetStatusResource(
            initiallyConnected ? "Loc.Network.Status.State.Ready" : "Loc.Network.Status.State.Disconnected",
            initiallyConnected ? "Loc.Network.Status.ReadyInitial" : "Loc.Network.Status.DisconnectedInitial");
        FooterText.SetResourceReference(TextBlock.TextProperty, "Loc.Network.Footer.ReadOnly");
        UpdateSummary();
        UpdateCommandState();
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

    private void CancelOnClick(object sender, RoutedEventArgs e) => CancelActiveOperation();

    private void SearchBoxOnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ClearSearchOnClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private async Task RefreshAsync()
    {
        using var operation = BeginOperation();
        SetStatusResource("Loc.Network.Status.State.Loading", "Loc.Network.Status.Loading");
        UpdateCommandState();
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
            _hasConnection = true;
            RefreshConnectionLabel();
            ApplyFilter();
            UpdateSummary();

            var hiddenOwners = _allPorts.Count(row => !row.OwnerVisible);
            if (_allInterfaces.Count == 0 && _allPorts.Count == 0)
            {
                SetStatusResource("Loc.Network.Status.State.Empty", "Loc.Network.Status.Empty");
            }
            else if (hiddenOwners == 0)
            {
                SetStatusRaw(
                    "Loc.Network.Status.State.Ready",
                    FormatLocalize("Loc.Network.Status.LoadedFormat", _allInterfaces.Count, _allPorts.Count));
            }
            else
            {
                SetStatusRaw(
                    "Loc.Network.Status.State.Ready",
                    FormatLocalize(
                        "Loc.Network.Status.LoadedHiddenOwnersFormat",
                        _allInterfaces.Count,
                        _allPorts.Count,
                        hiddenOwners));
            }
        }
        catch (OperationCanceledException)
        {
            SetStatusResource("Loc.Network.Status.State.Cancelled", "Loc.Network.Status.Cancelled");
        }
        catch (Exception exception)
        {
            SetStatusRaw(
                "Loc.Network.Status.State.Error",
                FormatLocalize("Loc.Network.Status.ErrorFormat", exception.Message));
        }
        finally
        {
            UpdateCommandState();
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
        ClearSearchButton.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        FooterText.Text = query.Length == 0
            ? FormatLocalize("Loc.Network.Footer.CountsFormat", _allInterfaces.Count, _allPorts.Count)
            : FormatLocalize(
                "Loc.Network.Footer.FilterFormat",
                query,
                interfaces.Count,
                _allInterfaces.Count,
                ports.Count,
                _allPorts.Count);
    }

    private void ApplyError(RemoteError error)
    {
        switch (error.Code)
        {
            case RemoteErrorCode.PermissionDenied:
                SetStatusRaw(
                    "Loc.Network.Status.State.Permission",
                    FormatLocalize("Loc.Network.Status.PermissionFormat", error.Message));
                break;

            case RemoteErrorCode.CommandNotFound:
            case RemoteErrorCode.CapabilityUnavailable:
                SetStatusRaw(
                    "Loc.Network.Status.State.Capability",
                    FormatLocalize("Loc.Network.Status.CapabilityFormat", error.Message));
                break;

            case RemoteErrorCode.NetworkInterrupted:
                _hasConnection = false;
                RefreshConnectionLabel();
                SetStatusRaw(
                    "Loc.Network.Status.State.Disconnected",
                    FormatLocalize("Loc.Network.Status.DisconnectedErrorFormat", error.Message));
                break;

            case RemoteErrorCode.OperationCancelled:
                SetStatusRaw(
                    "Loc.Network.Status.State.Cancelled",
                    FormatLocalize("Loc.Network.Status.CancelledErrorFormat", error.Message));
                break;

            case RemoteErrorCode.ParseFailed:
                SetStatusRaw(
                    "Loc.Network.Status.State.Error",
                    FormatLocalize("Loc.Network.Status.UnsupportedOutputFormat", error.Message));
                break;

            default:
                SetStatusRaw(
                    "Loc.Network.Status.State.Error",
                    FormatLocalize("Loc.Network.Status.ErrorCodeFormat", error.Code, error.Message));
                break;
        }
    }

    private void UpdateSummary()
    {
        InterfaceCountText.Text = _allInterfaces.Count.ToString("N0", CultureInfo.CurrentCulture);
        PortCountText.Text = _allPorts.Count.ToString("N0", CultureInfo.CurrentCulture);
        HiddenOwnerCountText.Text = _allPorts.Count(row => !row.OwnerVisible).ToString("N0", CultureInfo.CurrentCulture);
    }

    private void UpdateCommandState()
    {
        RefreshButton.IsEnabled = !IsBusy;
        CancelButton.IsEnabled = IsBusy;
    }

    private void RefreshConnectionLabel() =>
        ConnectionValueText.SetResourceReference(
            TextBlock.TextProperty,
            _hasConnection ? "Loc.Network.Connection.Connected" : "Loc.Network.Connection.Disconnected");

    private void SetStatusResource(string stateResourceKey, string messageResourceKey)
    {
        StatusStateText.SetResourceReference(TextBlock.TextProperty, stateResourceKey);
        StatusText.SetResourceReference(TextBlock.TextProperty, messageResourceKey);
    }

    private void SetStatusRaw(string stateResourceKey, string message)
    {
        StatusStateText.SetResourceReference(TextBlock.TextProperty, stateResourceKey);
        StatusText.Text = message;
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
            var owner = Localize(info.OwnerVisible ? "Loc.Network.Owner.Visible" : "Loc.Network.Owner.Unavailable");
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
            _owner.UpdateCommandState();
        }
    }
}
