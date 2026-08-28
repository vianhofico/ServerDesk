using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Docker;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class DockerInventoryWindow : Window
{
    private readonly IDockerInventoryService _service;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private CancellationTokenSource? _refreshCancellation;
    private DockerInventorySnapshot? _snapshot;
    private bool _closed;

    public DockerInventoryWindow(
        IDockerInventoryService service,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = initiallyConnected;
        InitializeComponent();

        TitleText.Text = $"Docker · {_profile.Name}";
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        StatusText.Text = initiallyConnected
            ? "Initial: refresh to detect Docker runtime access and load inventory."
            : "Disconnected: connect the server before inspecting Docker.";
        RuntimeDetailText.Text = "ServerDesk uses the remote Docker CLI over SSH; it never exposes or forwards the Docker socket.";
        FooterText.Text = "Read-only Docker inventory. No container or data mutation is available in this slice.";
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
        CancelRefresh();
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void CancelOnClick(object sender, RoutedEventArgs e)
    {
        CancelRefresh();
        StatusText.Text = "Cancelled: active Docker inspection was stopped.";
    }

    private void SearchBoxOnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async Task RefreshAsync()
    {
        if (!_initiallyConnected)
        {
            StatusText.Text = "Disconnected: connect the server before inspecting Docker.";
            return;
        }

        CancelRefresh();
        _refreshCancellation = new CancellationTokenSource();
        var source = _refreshCancellation;
        StatusText.Text = "Loading: detecting Docker runtime access and reading structured inventory…";
        RuntimeDetailText.Text = "No Docker socket is exposed; all reads use the existing SSH command channel.";
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
                ClearInventory();
                ApplyError(result.Error!);
                return;
            }

            _snapshot = result.Snapshot;
            ApplySnapshot();
            if (_snapshot is null)
            {
                StatusText.Text = "Recoverable error: Docker inspection returned no normalized snapshot.";
                return;
            }

            var runtime = _snapshot.Runtime;
            if (!runtime.IsUsable)
            {
                ApplyRuntimeUnavailable(runtime);
                return;
            }

            var resourceCount = _snapshot.Containers.Count + _snapshot.Images.Count + _snapshot.Volumes.Count + _snapshot.Networks.Count;
            if (result.IsPartial)
            {
                var first = result.Warnings[0];
                StatusText.Text = $"Partial: Docker is usable, but {result.Warnings.Count:N0} inventory read(s) failed. {first.Message}";
            }
            else if (resourceCount == 0)
            {
                StatusText.Text = "Empty: Docker is usable, but no containers, images, volumes or networks were returned.";
            }
            else
            {
                StatusText.Text = $"Loaded: Docker inventory contains {resourceCount:N0} resource row(s).";
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closed)
            {
                StatusText.Text = "Cancelled: Docker inspection was stopped.";
            }
        }
        catch (Exception exception)
        {
            if (!_closed)
            {
                StatusText.Text = $"Recoverable error: {exception.Message}";
            }
        }
        finally
        {
            if (ReferenceEquals(_refreshCancellation, source))
            {
                _refreshCancellation = null;
            }

            source.Dispose();
        }
    }

    private void ApplySnapshot()
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            ClearInventory();
            return;
        }

        var runtime = snapshot.Runtime;
        EngineValue.Text = runtime.IsUsable
            ? string.IsNullOrWhiteSpace(runtime.ApiVersion)
                ? ValueOrDash(runtime.EngineVersion)
                : $"{ValueOrDash(runtime.EngineVersion)} / {runtime.ApiVersion}"
            : runtime.Status.ToString();
        ContainerValue.Text = snapshot.System is null
            ? snapshot.Containers.Count.ToString("N0", CultureInfo.CurrentCulture)
            : $"{snapshot.System.ContainersRunning:N0}/{snapshot.System.Containers:N0}";
        ImageValue.Text = snapshot.Images.Count.ToString("N0", CultureInfo.CurrentCulture);
        VolumeValue.Text = snapshot.Volumes.Count.ToString("N0", CultureInfo.CurrentCulture);
        NetworkValue.Text = snapshot.Networks.Count.ToString("N0", CultureInfo.CurrentCulture);
        HostValue.Text = snapshot.System is null ? "—" : ValueOrDash(snapshot.System.Hostname);

        RuntimeDetailText.Text = BuildRuntimeDetail(snapshot);
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            ContainerGrid.ItemsSource = Array.Empty<DockerContainerInfo>();
            ImageGrid.ItemsSource = Array.Empty<DockerImageInfo>();
            VolumeGrid.ItemsSource = Array.Empty<DockerVolumeInfo>();
            NetworkGrid.ItemsSource = Array.Empty<DockerNetworkInfo>();
            FooterText.Text = "No Docker inventory is loaded.";
            return;
        }

        var search = SearchBox.Text;
        var containers = DockerInventoryProjection.FilterContainers(snapshot.Containers, search);
        var images = DockerInventoryProjection.FilterImages(snapshot.Images, search);
        var volumes = DockerInventoryProjection.FilterVolumes(snapshot.Volumes, search);
        var networks = DockerInventoryProjection.FilterNetworks(snapshot.Networks, search);
        ContainerGrid.ItemsSource = containers;
        ImageGrid.ItemsSource = images;
        VolumeGrid.ItemsSource = volumes;
        NetworkGrid.ItemsSource = networks;
        FooterText.Text =
            $"Visible: {containers.Count:N0} container(s), {images.Count:N0} image(s), {volumes.Count:N0} volume(s), {networks.Count:N0} network(s). Search is client-side.";
    }

    private void ApplyRuntimeUnavailable(DockerRuntimeState runtime)
    {
        StatusText.Text = runtime.Status switch
        {
            DockerRuntimeStatus.CliUnavailable => "Capability unavailable: Docker CLI was not found on this server.",
            DockerRuntimeStatus.DaemonUnavailable => "Capability unavailable: Docker CLI exists, but the Docker daemon is unavailable.",
            DockerRuntimeStatus.PermissionDenied => "Permission required: Docker exists, but the current SSH user cannot access the Docker daemon.",
            DockerRuntimeStatus.Unsupported => "Unsupported: Docker client/daemon versions cannot communicate safely.",
            _ => "Recoverable error: Docker runtime usability could not be determined.",
        };
        RuntimeDetailText.Text = runtime.Detail;
    }

    private void ApplyError(RemoteError error)
    {
        StatusText.Text = error.Code switch
        {
            RemoteErrorCode.OperationCancelled => $"Cancelled: {error.Message}",
            RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.ConnectionFailed => $"Disconnected: {error.Message}",
            RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired => $"Permission required: {error.Message}",
            RemoteErrorCode.CommandNotFound or RemoteErrorCode.CapabilityUnavailable => $"Capability unavailable: {error.Message}",
            RemoteErrorCode.UnsupportedVersion => $"Unsupported: {error.Message}",
            RemoteErrorCode.ParseFailed => $"Malformed/partial Docker output: {error.Message}",
            _ => $"Recoverable error ({error.Code}): {error.Message}",
        };
        RuntimeDetailText.Text = error.TechnicalDetail ?? string.Empty;
    }

    private void ClearInventory()
    {
        EngineValue.Text = "—";
        ContainerValue.Text = "—";
        ImageValue.Text = "—";
        VolumeValue.Text = "—";
        NetworkValue.Text = "—";
        HostValue.Text = "—";
        ContainerGrid.ItemsSource = Array.Empty<DockerContainerInfo>();
        ImageGrid.ItemsSource = Array.Empty<DockerImageInfo>();
        VolumeGrid.ItemsSource = Array.Empty<DockerVolumeInfo>();
        NetworkGrid.ItemsSource = Array.Empty<DockerNetworkInfo>();
        FooterText.Text = "No Docker inventory is loaded.";
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
        var parts = new List<string>
        {
            runtime.Detail,
        };
        if (!string.IsNullOrWhiteSpace(runtime.CliVersion))
        {
            parts.Add($"CLI: {runtime.CliVersion}");
        }

        if (snapshot.System is { } system)
        {
            parts.Add($"Host OS: {ValueOrDash(system.OperatingSystem)} · {ValueOrDash(system.Architecture)} · {system.CpuCount:N0} CPU · {FormatBytes(system.MemoryBytes)} RAM");
            if (!string.IsNullOrWhiteSpace(system.StorageDriver))
            {
                parts.Add($"Storage driver: {system.StorageDriver}");
            }
        }

        return string.Join("  |  ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

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

    private static string ValueOrDash(string value) => string.IsNullOrWhiteSpace(value) ? "—" : value;
}
