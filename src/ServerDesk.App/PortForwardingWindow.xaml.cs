using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class PortForwardingWindow : Window
{
    private readonly PortForwardManager _manager;
    private readonly ServerProfile _serverProfile;
    private readonly ObservableCollection<PortForwardListItem> _items = [];
    private Guid? _editingId;
    private bool _loaded;
    private bool _refreshing;

    public PortForwardingWindow(PortForwardManager manager, ServerProfile serverProfile)
    {
        InitializeComponent();
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _serverProfile = serverProfile ?? throw new ArgumentNullException(nameof(serverProfile));
        ServerEndpointText.Text = $"{serverProfile.Name} · {serverProfile.Username}@{serverProfile.Host}:{serverProfile.Port}";
        ForwardList.ItemsSource = _items;
        KindBox.ItemsSource = Enum.GetValues<PortForwardKind>();
        KindBox.SelectedItem = PortForwardKind.Local;
        _manager.Changed += ManagerOnChanged;
        Loaded += WindowOnLoaded;
        Closed += WindowOnClosed;
        ResetEditor();
    }

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        await RefreshAsync().ConfigureAwait(true);
    }

    private void WindowOnClosed(object? sender, EventArgs e)
    {
        _manager.Changed -= ManagerOnChanged;
    }

    private void ManagerOnChanged(Guid profileId)
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(async () => await RefreshAsync(profileId).ConfigureAwait(true));
    }

    private async Task RefreshAsync(Guid? preferredId = null)
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var selectedId = preferredId ?? (ForwardList.SelectedItem as PortForwardListItem)?.Profile.Id ?? _editingId;
            var profiles = await _manager.ListProfilesAsync(_serverProfile.Id).ConfigureAwait(true);
            _items.Clear();
            foreach (var profile in profiles)
            {
                _manager.TryGetRuntimeSnapshot(profile.Id, out var runtime);
                _items.Add(new PortForwardListItem(profile, runtime));
            }

            if (selectedId is Guid id)
            {
                ForwardList.SelectedItem = _items.FirstOrDefault(item => item.Profile.Id == id);
            }

            if (ForwardList.SelectedItem is null && _items.Count > 0)
            {
                ForwardList.SelectedIndex = 0;
            }

            if (_items.Count == 0 && _editingId is not null)
            {
                ResetEditor();
            }
        }
        catch (Exception exception)
        {
            ShowError($"Could not load saved tunnels: {exception.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void AddOnClick(object sender, RoutedEventArgs e)
    {
        ForwardList.SelectedItem = null;
        ResetEditor();
        NameBox.Focus();
    }

    private void ResetOnClick(object sender, RoutedEventArgs e) => ResetEditor();

    private void ForwardListOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ForwardList.SelectedItem is not PortForwardListItem item)
        {
            UpdateRuntimeControls(null);
            return;
        }

        _editingId = item.Profile.Id;
        EditorTitleText.Text = item.Profile.Name;
        NameBox.Text = item.Profile.Name;
        KindBox.SelectedItem = item.Profile.Kind;
        BindHostBox.Text = item.Profile.BindHost;
        BindPortBox.Text = item.Profile.BindPort.ToString(CultureInfo.InvariantCulture);
        DestinationHostBox.Text = item.Profile.DestinationHost ?? string.Empty;
        DestinationPortBox.Text = item.Profile.DestinationPort?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        ShowError(null);
        UpdateDestinationFields();
        UpdateExposureWarning();
        UpdateRuntimeControls(item);
    }

    private void KindBoxOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateDestinationFields();
        UpdateExposureWarning();
    }

    private void BindHostOnTextChanged(object sender, TextChangedEventArgs e) => UpdateExposureWarning();

    private async void SaveOnClick(object sender, RoutedEventArgs e)
    {
        ShowError(null);
        if (!TryBuildProfile(out var profile))
        {
            return;
        }

        if (!IsLoopbackHost(profile.BindHost))
        {
            var side = profile.Kind == PortForwardKind.Remote ? "Linux server" : "Windows PC";
            var result = MessageBox.Show(
                $"This tunnel will bind {profile.BindHost}:{profile.BindPort} on the {side}. Other machines may be able to reach it depending on firewall/routing.\n\nSave this non-loopback bind?",
                "Confirm network exposure",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
            if (result != MessageBoxResult.Yes)
            {
                StatusText.Text = "Tunnel save cancelled; no network exposure was changed.";
                return;
            }
        }

        try
        {
            SetBusy(true);
            await _manager.SaveProfileAsync(profile).ConfigureAwait(true);
            _editingId = profile.Id;
            StatusText.Text = $"Saved tunnel '{profile.Name}'.";
            await RefreshAsync(profile.Id).ConfigureAwait(true);
        }
        catch (PortForwardSessionException exception)
        {
            ShowError(exception.Error.Message);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"Could not save tunnel: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void StartStopOnClick(object sender, RoutedEventArgs e)
    {
        if (ForwardList.SelectedItem is not PortForwardListItem item)
        {
            return;
        }

        ShowError(null);
        try
        {
            SetBusy(true);
            if (item.IsRunning)
            {
                await _manager.StopAsync(item.Profile.Id).ConfigureAwait(true);
                StatusText.Text = $"Stopped tunnel '{item.Profile.Name}'.";
            }
            else
            {
                var runtime = await _manager.StartAsync(item.Profile.Id).ConfigureAwait(true);
                var bound = runtime.BoundPort > 0 ? runtime.BoundPort : item.Profile.BindPort;
                StatusText.Text = $"Tunnel '{item.Profile.Name}' is active on {item.Profile.BindHost}:{bound}.";
            }

            await RefreshAsync(item.Profile.Id).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Tunnel operation cancelled.";
        }
        catch (PortForwardSessionException exception)
        {
            ShowError(exception.Error.Message);
            await RefreshAsync(item.Profile.Id).ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            ShowError($"Tunnel operation failed: {exception.Message}");
            await RefreshAsync(item.Profile.Id).ConfigureAwait(true);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void DeleteOnClick(object sender, RoutedEventArgs e)
    {
        if (ForwardList.SelectedItem is not PortForwardListItem item)
        {
            return;
        }

        var result = MessageBox.Show(
            $"Delete tunnel '{item.Profile.Name}'?\n\nIf it is active, ServerDesk will stop it first.",
            "Delete SSH tunnel",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            SetBusy(true);
            await _manager.DeleteProfileAsync(item.Profile.Id).ConfigureAwait(true);
            StatusText.Text = $"Deleted tunnel '{item.Profile.Name}'.";
            ResetEditor();
            await RefreshAsync().ConfigureAwait(true);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            ShowError($"Could not delete tunnel: {exception.Message}");
        }
        finally
        {
            SetBusy(false);
        }
    }

    private bool TryBuildProfile(out PortForwardProfile profile)
    {
        profile = null!;
        if (!int.TryParse(BindPortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var bindPort))
        {
            ShowError("Bind port must be 0 (automatic) or a number from 1 to 65535.");
            return false;
        }

        if (KindBox.SelectedItem is not PortForwardKind kind)
        {
            ShowError("Choose a forwarding type.");
            return false;
        }

        int? destinationPort = null;
        string? destinationHost = null;
        if (kind != PortForwardKind.Dynamic)
        {
            destinationHost = DestinationHostBox.Text;
            if (!int.TryParse(DestinationPortBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedDestinationPort))
            {
                ShowError("Destination port must be a number from 1 to 65535.");
                return false;
            }

            destinationPort = parsedDestinationPort;
        }

        try
        {
            profile = _editingId is Guid id
                ? PortForwardProfile.Create(
                    id,
                    _serverProfile.Id,
                    NameBox.Text,
                    kind,
                    BindHostBox.Text,
                    bindPort,
                    destinationHost,
                    destinationPort)
                : PortForwardProfile.Create(
                    _serverProfile.Id,
                    NameBox.Text,
                    kind,
                    BindHostBox.Text,
                    bindPort,
                    destinationHost,
                    destinationPort);
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            ShowError(exception.Message);
            return false;
        }
    }

    private void ResetEditor()
    {
        _editingId = null;
        EditorTitleText.Text = "New tunnel";
        NameBox.Text = string.Empty;
        KindBox.SelectedItem = PortForwardKind.Local;
        BindHostBox.Text = "127.0.0.1";
        BindPortBox.Text = "0";
        DestinationHostBox.Text = "127.0.0.1";
        DestinationPortBox.Text = "5432";
        RuntimeStateText.Text = "Not saved · Stopped";
        StartStopButton.Content = "Start";
        StartStopButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        EditorPanel.IsEnabled = true;
        SaveButton.IsEnabled = true;
        ShowError(null);
        UpdateDestinationFields();
        UpdateExposureWarning();
    }

    private void UpdateRuntimeControls(PortForwardListItem? item)
    {
        if (item is null)
        {
            RuntimeStateText.Text = "Not saved · Stopped";
            StartStopButton.Content = "Start";
            StartStopButton.IsEnabled = false;
            DeleteButton.IsEnabled = false;
            EditorPanel.IsEnabled = true;
            SaveButton.IsEnabled = true;
            return;
        }

        RuntimeStateText.Text = item.RuntimeLabel;
        StartStopButton.Content = item.IsRunning ? "Stop" : "Start";
        StartStopButton.IsEnabled = !item.IsTransitioning;
        DeleteButton.IsEnabled = !item.IsTransitioning;
        EditorPanel.IsEnabled = !item.IsRunning && !item.IsTransitioning;
        SaveButton.IsEnabled = !item.IsRunning && !item.IsTransitioning;
    }

    private void UpdateDestinationFields()
    {
        var dynamic = KindBox.SelectedItem is PortForwardKind.Dynamic;
        DestinationHostPanel.Visibility = dynamic ? Visibility.Collapsed : Visibility.Visible;
        DestinationPortPanel.Visibility = dynamic ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateExposureWarning()
    {
        if (string.IsNullOrWhiteSpace(BindHostBox.Text) || IsLoopbackHost(BindHostBox.Text))
        {
            ExposureWarning.Visibility = Visibility.Collapsed;
            return;
        }

        var side = KindBox.SelectedItem is PortForwardKind.Remote ? "Linux server" : "Windows PC";
        ExposureWarningText.Text =
            $"Network exposure warning: this bind is not loopback. The listener may be reachable from other machines on the {side}. ServerDesk will ask for confirmation before saving.";
        ExposureWarning.Visibility = Visibility.Visible;
    }

    private void SetBusy(bool busy)
    {
        SaveButton.IsEnabled = !busy && (ForwardList.SelectedItem as PortForwardListItem)?.IsRunning != true;
        StartStopButton.IsEnabled = !busy && ForwardList.SelectedItem is PortForwardListItem item && !item.IsTransitioning;
        DeleteButton.IsEnabled = !busy && ForwardList.SelectedItem is PortForwardListItem selected && !selected.IsTransitioning;
    }

    private void ShowError(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            ErrorText.Text = string.Empty;
            ErrorPanel.Visibility = Visibility.Collapsed;
            return;
        }

        ErrorText.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
        StatusText.Text = message;
    }

    private static bool IsLoopbackHost(string host)
    {
        var normalized = host.Trim();
        if (normalized.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return IPAddress.TryParse(normalized.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address);
    }

    private sealed class PortForwardListItem
    {
        public PortForwardListItem(PortForwardProfile profile, PortForwardRuntimeSnapshot? runtime)
        {
            Profile = profile;
            Runtime = runtime;
        }

        public PortForwardProfile Profile { get; }

        public PortForwardRuntimeSnapshot? Runtime { get; }

        public string Name => Profile.Name;

        public bool IsRunning => Runtime?.State is PortForwardSessionState.Active;

        public bool IsTransitioning => Runtime?.State is PortForwardSessionState.Starting or PortForwardSessionState.Stopping;

        public string StateLabel => Runtime?.State.ToString() ?? "Stopped";

        public string RuntimeLabel
        {
            get
            {
                var state = Runtime?.State ?? PortForwardSessionState.Stopped;
                var port = Runtime?.BoundPort > 0 ? Runtime.BoundPort : Profile.BindPort;
                var endpoint = port == 0 ? $"{Profile.BindHost}:automatic" : $"{Profile.BindHost}:{port}";
                return $"{state} · {endpoint}";
            }
        }

        public string RouteLabel => Profile.Kind switch
        {
            PortForwardKind.Local => $"Local {Profile.BindHost}:{PortLabel(Profile.BindPort)} → {Profile.DestinationHost}:{Profile.DestinationPort}",
            PortForwardKind.Remote => $"Remote {Profile.BindHost}:{PortLabel(Profile.BindPort)} → {Profile.DestinationHost}:{Profile.DestinationPort}",
            PortForwardKind.Dynamic => $"SOCKS5 {Profile.BindHost}:{PortLabel(Profile.BindPort)}",
            _ => Profile.Name,
        };

        private static string PortLabel(int port) => port == 0 ? "auto" : port.ToString(CultureInfo.InvariantCulture);
    }
}
