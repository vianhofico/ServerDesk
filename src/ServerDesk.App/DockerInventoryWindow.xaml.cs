using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Docker;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class DockerInventoryWindow : Window
{
    private readonly IDockerInventoryService _service;
    private readonly IDockerContainerDiagnosticsService _diagnosticsService;
    private readonly ServerProfile _profile;
    private CancellationTokenSource? _refreshCancellation;
    private DockerInventorySnapshot? _snapshot;
    private DockerVisibleInventory? _visibleInventory;
    private DockerUiState _currentState;
    private bool _hasLoadedSnapshot;
    private bool _hasConnection;
    private bool _closed;

    public DockerInventoryWindow(
        IDockerInventoryService service,
        IDockerContainerDiagnosticsService diagnosticsService,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _diagnosticsService = diagnosticsService ?? throw new ArgumentNullException(nameof(diagnosticsService));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _hasConnection = initiallyConnected;
        InitializeComponent();

        ServerNameText.Text = _profile.Name;
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        if (string.IsNullOrWhiteSpace(_profile.Environment))
        {
            EnvironmentValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Docker.Header.Unlabeled");
        }
        else
        {
            EnvironmentValueText.Text = _profile.Environment;
        }

        RefreshConnectionLabel();
        RuntimeStatusValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Docker.Runtime.Unknown");
        SetStatusResource(
            initiallyConnected ? DockerUiState.Ready : DockerUiState.Disconnected,
            initiallyConnected ? "Loc.Docker.Status.ReadyInitial" : "Loc.Docker.Status.DisconnectedInitial");
        RuntimeDetailText.Text = string.Empty;
        FooterText.SetResourceReference(TextBlock.TextProperty, "Loc.Docker.Footer.None");
        UpdateContainerSelectionPresentation();
        ApplyFilter();
    }

    public IDockerContainerActionService? ActionService { get; set; }

    public IDockerExecTerminalSessionFactory? ExecTerminalSessionFactory { get; set; }

    private bool IsBusy => _refreshCancellation is not null;

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hasConnection)
        {
            await RefreshAsync();
        }
    }

    private void WindowOnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        CancelRefresh();
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void DiagnosticsOnClick(object sender, RoutedEventArgs e) => OpenSelectedDiagnostics();

    private void CancelOnClick(object sender, RoutedEventArgs e) => CancelRefresh();

    private void SearchBoxOnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ClearSearchOnClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private void InventoryTabsOnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.Source, InventoryTabs) || ContainerStateOverlay is null)
        {
            return;
        }

        RefreshOverlays();
    }

    private void ContainerGridOnSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateContainerSelectionPresentation();

    private void ContainerGridOnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsBusy && _hasConnection && ContainerGrid.SelectedItem is DockerContainerInfo)
        {
            OpenSelectedDiagnostics();
        }
    }

    private void OpenSelectedDiagnostics()
    {
        if (IsBusy || !_hasConnection || ContainerGrid.SelectedItem is not DockerContainerInfo container)
        {
            SetStatusResource(DockerUiState.Error, "Loc.Docker.Status.SelectFirst");
            ApplyFilter();
            return;
        }

        var window = new DockerContainerDiagnosticsWindow(
            _diagnosticsService,
            _profile,
            container,
            _hasConnection)
        {
            ActionService = ActionService,
            ExecTerminalSessionFactory = ExecTerminalSessionFactory,
            Owner = this,
        };
        window.Show();
    }

    private async Task RefreshAsync()
    {
        if (!_hasConnection)
        {
            SetStatusResource(DockerUiState.Disconnected, "Loc.Docker.Status.DisconnectedInitial");
            ApplyFilter();
            return;
        }

        CancelRefresh();
        _refreshCancellation = new CancellationTokenSource();
        var source = _refreshCancellation;
        SetStatusResource(DockerUiState.Loading, "Loc.Docker.Status.Loading");
        RuntimeStatusValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Docker.Runtime.Unknown");
        UpdateCommandState();
        RefreshOverlays();

        try
        {
            var result = await _service.InspectAsync(_profile, source.Token);
            if (_closed)
            {
                return;
            }

            if (!result.IsSuccess)
            {
                _snapshot = null;
                _visibleInventory = null;
                _hasLoadedSnapshot = false;
                ClearInventory();
                ApplyError(result.Error!);
                ApplyFilter();
                return;
            }

            _snapshot = result.Snapshot;
            _hasLoadedSnapshot = _snapshot is not null;
            _hasConnection = true;
            RefreshConnectionLabel();
            if (_snapshot is null)
            {
                ClearInventory();
                SetStatusResource(DockerUiState.Error, "Loc.Docker.Status.NoSnapshot");
                ApplyFilter();
                return;
            }

            ApplySnapshotPresentation(_snapshot);
            if (!_snapshot.Runtime.IsUsable)
            {
                ApplyRuntimeUnavailable(_snapshot.Runtime);
                ApplyFilter();
                return;
            }

            var resourceCount = _snapshot.Containers.Count + _snapshot.Images.Count + _snapshot.Volumes.Count + _snapshot.Networks.Count;
            if (result.IsPartial)
            {
                var first = result.Warnings[0];
                SetStatusRaw(
                    DockerUiState.Partial,
                    FormatLocalize("Loc.Docker.Status.Partial", result.Warnings.Count, first.Message));
            }
            else if (resourceCount == 0)
            {
                SetStatusResource(DockerUiState.Empty, "Loc.Docker.Status.Empty");
            }
            else
            {
                SetStatusRaw(DockerUiState.Ready, FormatLocalize("Loc.Docker.Status.Loaded", resourceCount));
            }

            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                SetStatusResource(DockerUiState.Cancelled, "Loc.Docker.Status.Cancelled");
                ApplyFilter();
            }
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                SetStatusRaw(DockerUiState.Error, FormatLocalize("Loc.Docker.Status.Error", exception.Message));
                ApplyFilter();
            }
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, source))
            {
                _refreshCancellation = null;
            }

            source.Dispose();
            if (!_closed)
            {
                UpdateCommandState();
            }
        }
    }

    private void ApplySnapshotPresentation(DockerInventorySnapshot snapshot)
    {
        var runtime = snapshot.Runtime;
        RuntimeStatusValueText.SetResourceReference(
            TextBlock.TextProperty,
            RuntimeStatusResource(runtime.Status));
        EngineValue.Text = runtime.IsUsable
            ? string.IsNullOrWhiteSpace(runtime.ApiVersion)
                ? ValueOrDash(runtime.EngineVersion)
                : $"{ValueOrDash(runtime.EngineVersion)} / {runtime.ApiVersion}"
            : Localize(RuntimeStatusResource(runtime.Status));
        HostValue.Text = snapshot.System is null ? "—" : ValueOrDash(snapshot.System.Hostname);
        RuntimeDetailText.Text = BuildRuntimeDetail(snapshot);
    }

    private void ApplyFilter()
    {
        var snapshot = _snapshot;
        var query = SearchBox.Text;
        ClearSearchButton.Visibility = string.IsNullOrWhiteSpace(query) ? Visibility.Collapsed : Visibility.Visible;

        if (snapshot is null)
        {
            _visibleInventory = null;
            ContainerGrid.ItemsSource = Array.Empty<DockerContainerInfo>();
            ImageGrid.ItemsSource = Array.Empty<DockerImageInfo>();
            VolumeGrid.ItemsSource = Array.Empty<DockerVolumeInfo>();
            NetworkGrid.ItemsSource = Array.Empty<DockerNetworkInfo>();
            ClearSummaryValues();
            FooterText.SetResourceReference(TextBlock.TextProperty, "Loc.Docker.Footer.None");
            UpdateContainerSelectionPresentation();
            RefreshOverlays();
            UpdateCommandState();
            return;
        }

        var visible = DockerWorkspaceProjection.Filter(snapshot, query);
        _visibleInventory = visible;
        ContainerGrid.ItemsSource = visible.Containers;
        ImageGrid.ItemsSource = visible.Images;
        VolumeGrid.ItemsSource = visible.Volumes;
        NetworkGrid.ItemsSource = visible.Networks;

        var summary = DockerWorkspaceProjection.Summarize(snapshot, visible);
        ContainerValue.Text = $"{summary.RunningContainers:N0}/{summary.Containers:N0}";
        ImageValue.Text = summary.Images.ToString("N0", CultureInfo.CurrentCulture);
        VolumeValue.Text = summary.Volumes.ToString("N0", CultureInfo.CurrentCulture);
        NetworkValue.Text = summary.Networks.ToString("N0", CultureInfo.CurrentCulture);
        FooterText.Text = FormatLocalize(
            "Loc.Docker.Footer.Visible",
            summary.VisibleContainers,
            summary.VisibleImages,
            summary.VisibleVolumes,
            summary.VisibleNetworks);

        RefreshOverlays();
        UpdateCommandState();
    }

    private void UpdateContainerSelectionPresentation()
    {
        if (ContainerGrid.SelectedItem is not DockerContainerInfo container)
        {
            ContainerDetailsHintText.Visibility = Visibility.Visible;
            ContainerDetailsContent.Visibility = Visibility.Collapsed;
            TargetValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Docker.Command.NoTarget");
            UpdateCommandState();
            return;
        }

        ContainerDetailsHintText.Visibility = Visibility.Collapsed;
        ContainerDetailsContent.Visibility = Visibility.Visible;
        TargetValueText.Text = FormatLocalize("Loc.Docker.Command.TargetValue", DisplayOrDash(container.Name));
        DetailNameText.Text = DisplayOrDash(container.Name);
        DetailStateText.Text = DisplayOrDash(container.State);
        DetailImageText.Text = DisplayOrDash(container.Image);
        DetailStatusText.Text = DisplayOrDash(container.Status);
        DetailPortsText.Text = DisplayOrDash(container.Ports);
        DetailNetworksText.Text = DisplayOrDash(container.Networks);
        DetailMountsText.Text = DisplayOrDash(container.Mounts);
        DetailCreatedText.Text = DisplayOrDash(container.CreatedAt);
        DetailSizeText.Text = DisplayOrDash(container.Size);
        DetailIdText.Text = DisplayOrDash(container.Id);
        UpdateCommandState();
    }

    private void ApplyRuntimeUnavailable(DockerRuntimeState runtime)
    {
        RuntimeStatusValueText.SetResourceReference(TextBlock.TextProperty, RuntimeStatusResource(runtime.Status));
        var key = runtime.Status switch
        {
            DockerRuntimeStatus.CliUnavailable => "Loc.Docker.Status.CliUnavailable",
            DockerRuntimeStatus.DaemonUnavailable => "Loc.Docker.Status.DaemonUnavailable",
            DockerRuntimeStatus.PermissionDenied => "Loc.Docker.Status.RuntimePermission",
            DockerRuntimeStatus.Unsupported => "Loc.Docker.Status.RuntimeUnsupported",
            _ => "Loc.Docker.Status.RuntimeUnknown",
        };
        SetStatusResource(DockerUiState.RuntimeUnavailable, key);
        RuntimeDetailText.Text = runtime.Detail;
    }

    private void ApplyError(RemoteError error)
    {
        var state = error.Code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.ConnectionFailed
            ? DockerUiState.Disconnected
            : error.Code == RemoteErrorCode.OperationCancelled
                ? DockerUiState.Cancelled
                : DockerUiState.Error;
        var key = error.Code switch
        {
            RemoteErrorCode.OperationCancelled => "Loc.Docker.Status.Cancelled",
            RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.ConnectionFailed => "Loc.Docker.Status.Disconnected",
            RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => "Loc.Docker.Status.Permission",
            RemoteErrorCode.CommandNotFound or RemoteErrorCode.CapabilityUnavailable => "Loc.Docker.Status.Capability",
            RemoteErrorCode.UnsupportedVersion => "Loc.Docker.Status.Unsupported",
            RemoteErrorCode.ParseFailed => "Loc.Docker.Status.Malformed",
            _ => "Loc.Docker.Status.ErrorTyped",
        };

        if (state == DockerUiState.Disconnected)
        {
            _hasConnection = false;
            RefreshConnectionLabel();
        }

        SetStatusRaw(
            state,
            key == "Loc.Docker.Status.ErrorTyped"
                ? FormatLocalize(key, error.Code, error.Message)
                : key == "Loc.Docker.Status.Cancelled"
                    ? Localize(key)
                    : FormatLocalize(key, error.Message));
        RuntimeDetailText.Text = error.TechnicalDetails ?? string.Empty;
        RuntimeStatusValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Docker.Runtime.Unknown");
    }

    private void RefreshOverlays()
    {
        var snapshot = _snapshot;
        var visible = _visibleInventory;
        var query = SearchBox.Text;
        RefreshOverlay(
            ContainerStateOverlay,
            ContainerStateTitle,
            ContainerStateDetail,
            snapshot?.Containers.Count ?? 0,
            visible?.Containers.Count ?? 0,
            query);
        RefreshOverlay(
            ImageStateOverlay,
            ImageStateTitle,
            ImageStateDetail,
            snapshot?.Images.Count ?? 0,
            visible?.Images.Count ?? 0,
            query);
        RefreshOverlay(
            VolumeStateOverlay,
            VolumeStateTitle,
            VolumeStateDetail,
            snapshot?.Volumes.Count ?? 0,
            visible?.Volumes.Count ?? 0,
            query);
        RefreshOverlay(
            NetworkStateOverlay,
            NetworkStateTitle,
            NetworkStateDetail,
            snapshot?.Networks.Count ?? 0,
            visible?.Networks.Count ?? 0,
            query);
    }

    private void RefreshOverlay(
        Border overlay,
        TextBlock title,
        TextBlock detail,
        int sourceCount,
        int visibleCount,
        string? query)
    {
        if (_currentState == DockerUiState.Loading)
        {
            ShowOverlay(overlay, title, detail, "Loc.Docker.Overlay.LoadingTitle", rawDetail: StatusText.Text);
            return;
        }

        if (!_hasLoadedSnapshot && _currentState == DockerUiState.Disconnected)
        {
            ShowOverlay(overlay, title, detail, "Loc.Docker.Overlay.DisconnectedTitle", rawDetail: StatusText.Text);
            return;
        }

        if (_currentState == DockerUiState.RuntimeUnavailable)
        {
            ShowOverlay(overlay, title, detail, "Loc.Docker.Overlay.RuntimeTitle", rawDetail: StatusText.Text);
            return;
        }

        if (!_hasLoadedSnapshot && _currentState == DockerUiState.Error)
        {
            ShowOverlay(overlay, title, detail, "Loc.Docker.Overlay.ErrorTitle", rawDetail: StatusText.Text);
            return;
        }

        if (!_hasLoadedSnapshot && _currentState == DockerUiState.Cancelled)
        {
            ShowOverlay(overlay, title, detail, "Loc.Docker.Overlay.CancelledTitle", rawDetail: StatusText.Text);
            return;
        }

        if (_hasLoadedSnapshot && sourceCount == 0)
        {
            ShowOverlay(overlay, title, detail, "Loc.Docker.Overlay.EmptyTitle", "Loc.Docker.Overlay.EmptyDetail");
            return;
        }

        if (_hasLoadedSnapshot && sourceCount > 0 && visibleCount == 0 && !string.IsNullOrWhiteSpace(query))
        {
            ShowOverlay(overlay, title, detail, "Loc.Docker.Overlay.SearchTitle", "Loc.Docker.Overlay.SearchDetail");
            return;
        }

        overlay.Visibility = Visibility.Collapsed;
    }

    private static void ShowOverlay(
        Border overlay,
        TextBlock title,
        TextBlock detail,
        string titleKey,
        string? detailKey = null,
        string? rawDetail = null)
    {
        overlay.Visibility = Visibility.Visible;
        title.SetResourceReference(TextBlock.TextProperty, titleKey);
        if (detailKey is not null)
        {
            detail.SetResourceReference(TextBlock.TextProperty, detailKey);
        }
        else
        {
            detail.Text = rawDetail ?? string.Empty;
        }
    }

    private void UpdateCommandState()
    {
        var busy = IsBusy;
        var hasContainer = ContainerGrid.SelectedItem is DockerContainerInfo;
        RefreshButton.IsEnabled = !busy && _hasConnection;
        CancelButton.IsEnabled = busy;
        DiagnosticsButton.IsEnabled = !busy && _hasConnection && hasContainer;
        SearchBox.IsEnabled = !busy;
        ClearSearchButton.IsEnabled = !busy;
        ContainerGrid.IsEnabled = !busy;
        ImageGrid.IsEnabled = !busy;
        VolumeGrid.IsEnabled = !busy;
        NetworkGrid.IsEnabled = !busy;
        InventoryTabs.IsEnabled = !busy;
    }

    private void RefreshConnectionLabel() => ConnectionValueText.SetResourceReference(
        TextBlock.TextProperty,
        _hasConnection ? "Loc.Docker.Header.Connected" : "Loc.Docker.Header.Disconnected");

    private void SetStatusResource(DockerUiState state, string resourceKey)
    {
        _currentState = state;
        StatusText.SetResourceReference(TextBlock.TextProperty, resourceKey);
        RefreshStatusChrome();
    }

    private void SetStatusRaw(DockerUiState state, string message)
    {
        _currentState = state;
        StatusText.Text = message;
        RefreshStatusChrome();
    }

    private void RefreshStatusChrome()
    {
        var error = _currentState is DockerUiState.Error or DockerUiState.Disconnected or DockerUiState.RuntimeUnavailable;
        StatusCard.Style = (Style)FindResource(error ? "InlineErrorCard" : "InlineInfoCard");
    }

    private void ClearInventory()
    {
        _visibleInventory = null;
        ClearSummaryValues();
        ContainerGrid.ItemsSource = Array.Empty<DockerContainerInfo>();
        ImageGrid.ItemsSource = Array.Empty<DockerImageInfo>();
        VolumeGrid.ItemsSource = Array.Empty<DockerVolumeInfo>();
        NetworkGrid.ItemsSource = Array.Empty<DockerNetworkInfo>();
        UpdateContainerSelectionPresentation();
        FooterText.SetResourceReference(TextBlock.TextProperty, "Loc.Docker.Footer.None");
    }

    private void ClearSummaryValues()
    {
        EngineValue.Text = "—";
        ContainerValue.Text = "—";
        ImageValue.Text = "—";
        VolumeValue.Text = "—";
        NetworkValue.Text = "—";
        HostValue.Text = "—";
    }

    private void CancelRefresh()
    {
        if (_refreshCancellation is not null && !_refreshCancellation.IsCancellationRequested)
        {
            _refreshCancellation.Cancel();
        }
    }

    private static string BuildRuntimeDetail(DockerInventorySnapshot snapshot)
    {
        var runtime = snapshot.Runtime;
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(runtime.Detail))
        {
            parts.Add(runtime.Detail);
        }

        if (!string.IsNullOrWhiteSpace(runtime.CliVersion))
        {
            parts.Add(FormatLocalize("Loc.Docker.Runtime.CliDetail", runtime.CliVersion));
        }

        if (snapshot.System is { } system)
        {
            parts.Add(FormatLocalize(
                "Loc.Docker.Runtime.HostDetail",
                ValueOrDash(system.OperatingSystem),
                ValueOrDash(system.Architecture),
                system.CpuCount,
                FormatBytes(system.MemoryBytes)));
            if (!string.IsNullOrWhiteSpace(system.StorageDriver))
            {
                parts.Add(FormatLocalize("Loc.Docker.Runtime.StorageDetail", system.StorageDriver));
            }
        }

        return string.Join("  |  ", parts);
    }

    private static string RuntimeStatusResource(DockerRuntimeStatus status) => status switch
    {
        DockerRuntimeStatus.Available => "Loc.Docker.Runtime.Available",
        DockerRuntimeStatus.CliUnavailable => "Loc.Docker.Runtime.CliUnavailable",
        DockerRuntimeStatus.DaemonUnavailable => "Loc.Docker.Runtime.DaemonUnavailable",
        DockerRuntimeStatus.PermissionDenied => "Loc.Docker.Runtime.PermissionDenied",
        DockerRuntimeStatus.Unsupported => "Loc.Docker.Runtime.Unsupported",
        _ => "Loc.Docker.Runtime.Unknown",
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0)
        {
            return "—";
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var index = 0;
        while (value >= 1024 && index < units.Length - 1)
        {
            value /= 1024;
            index++;
        }

        return $"{value:0.#} {units[index]}";
    }

    private static string DisplayOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private static string ValueOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

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

    private enum DockerUiState
    {
        Disconnected,
        Ready,
        Loading,
        Partial,
        Empty,
        RuntimeUnavailable,
        Error,
        Cancelled,
    }
}
