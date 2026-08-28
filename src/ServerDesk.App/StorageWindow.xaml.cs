using System.Windows;
using System.Windows.Controls;
using ServerDesk.Application.Storage;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class StorageWindow : Window
{
    private readonly IServerStorageService _storageService;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private readonly List<FilesystemRow> _allFilesystems = [];
    private readonly List<BlockDeviceRow> _allBlockDevices = [];
    private CancellationTokenSource? _operationCancellation;

    public StorageWindow(
        IServerStorageService storageService,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = initiallyConnected;
        InitializeComponent();
        TitleText.Text = $"Storage · {_profile.Name}";
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        StatusText.Text = initiallyConnected
            ? "Ready to inspect storage."
            : "Disconnected: connect the server before loading storage data.";
        FooterText.Text = "Storage inspection is read-only.";
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

    private async void AnalyzeOnClick(object sender, RoutedEventArgs e) => await AnalyzeAsync();

    private void CancelOnClick(object sender, RoutedEventArgs e) => CancelActiveOperation();

    private void SearchBoxOnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private async Task RefreshAsync()
    {
        using var operation = BeginOperation();
        StatusText.Text = "Loading filesystems and block devices…";
        try
        {
            var result = await _storageService.InspectAsync(_profile, operation.Token);
            if (!result.IsSuccess)
            {
                ApplyError(result.Error!);
                return;
            }

            _allFilesystems.Clear();
            _allFilesystems.AddRange(result.Filesystems.Select(FilesystemRow.From));
            _allBlockDevices.Clear();
            _allBlockDevices.AddRange(result.BlockDevices.Select(BlockDeviceRow.From));
            ApplyFilter();

            var warnings = _allFilesystems.Count(row => row.IsWarning);
            StatusText.Text = _allFilesystems.Count == 0 && _allBlockDevices.Count == 0
                ? "Empty: no storage data was returned."
                : warnings == 0
                    ? $"Ready: {_allFilesystems.Count:N0} filesystem(s), {_allBlockDevices.Count:N0} block device row(s)."
                    : $"Ready: {_allFilesystems.Count:N0} filesystem(s), {_allBlockDevices.Count:N0} block device row(s); {warnings:N0} filesystem(s) at or above 85% usage.";
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Cancelled: storage refresh stopped.";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Error: {exception.Message}";
        }
    }

    private async Task AnalyzeAsync()
    {
        var path = DirectoryPathBox.Text.Trim();
        if (path.Length == 0)
        {
            AnalyzerStatusText.Text = "Enter an absolute Linux path.";
            return;
        }

        using var operation = BeginOperation();
        AnalyzerStatusText.Text = $"Analyzing '{path}' read-only… Cancel remains available.";
        try
        {
            var result = await _storageService.AnalyzeDirectoryAsync(_profile, path, operation.Token);
            if (!result.IsSuccess)
            {
                AnalyzerStatusText.Text = FormatError(result.Error!);
                return;
            }

            var rows = result.Entries.Select(DirectoryRow.From).ToArray();
            DirectoryGrid.ItemsSource = rows;
            AnalyzerStatusText.Text = rows.Length == 0
                ? "Empty: no directory usage rows were returned."
                : $"Ready: {rows.Length:N0} path(s), sorted largest first.";
        }
        catch (OperationCanceledException)
        {
            AnalyzerStatusText.Text = "Cancelled: directory analysis stopped.";
        }
        catch (Exception exception)
        {
            AnalyzerStatusText.Text = $"Error: {exception.Message}";
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var filesystems = query.Length == 0
            ? _allFilesystems
            : _allFilesystems.Where(row => row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        var devices = query.Length == 0
            ? _allBlockDevices
            : _allBlockDevices.Where(row => row.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        FilesystemGrid.ItemsSource = filesystems;
        BlockGrid.ItemsSource = devices;
        FooterText.Text = query.Length == 0
            ? $"Read-only snapshot · {_allFilesystems.Count:N0} filesystem(s) · {_allBlockDevices.Count:N0} block device row(s) · warning threshold 85%."
            : $"Filter '{query}': {filesystems.Count:N0}/{_allFilesystems.Count:N0} filesystem(s), {devices.Count:N0}/{_allBlockDevices.Count:N0} block device row(s).";
    }

    private void ApplyError(RemoteError error) => StatusText.Text = FormatError(error);

    private static string FormatError(RemoteError error) => error.Code switch
    {
        RemoteErrorCode.PermissionDenied => $"Permission: {error.Message}",
        RemoteErrorCode.CommandNotFound or RemoteErrorCode.CapabilityUnavailable => $"Capability: {error.Message}",
        RemoteErrorCode.PathNotFound => $"Not found: {error.Message}",
        RemoteErrorCode.NetworkInterrupted => $"Disconnected: {error.Message}",
        RemoteErrorCode.OperationCancelled => $"Cancelled: {error.Message}",
        _ => $"{error.Code}: {error.Message}",
    };

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

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    private sealed record FilesystemRow(
        string Device,
        string FileSystemType,
        string TotalText,
        string UsedText,
        string AvailableText,
        string PercentText,
        string HealthText,
        string MountPoint,
        bool IsWarning,
        string SearchText)
    {
        public static FilesystemRow From(ServerFilesystemInfo info) =>
            new(
                info.Device,
                info.FileSystemType,
                FormatBytes(info.TotalBytes),
                FormatBytes(info.UsedBytes),
                FormatBytes(info.AvailableBytes),
                $"{info.UsedPercent:0.#}%",
                info.IsWarning ? "Warning" : "Healthy",
                info.MountPoint,
                info.IsWarning,
                $"{info.Device} {info.FileSystemType} {info.MountPoint}");
    }

    private sealed record BlockDeviceRow(
        string Name,
        string ParentName,
        string Type,
        string SizeText,
        string FileSystemType,
        string MountPoint,
        string Model,
        string MediaText,
        string SearchText)
    {
        public static BlockDeviceRow From(ServerBlockDeviceInfo info)
        {
            var parent = info.ParentName ?? "—";
            var fileSystemType = string.IsNullOrWhiteSpace(info.FileSystemType) ? "—" : info.FileSystemType;
            var mountPoint = string.IsNullOrWhiteSpace(info.MountPoint) ? "—" : info.MountPoint;
            var model = string.IsNullOrWhiteSpace(info.Model) ? "—" : info.Model;
            var media = info.IsRotational is null ? "Unknown" : info.IsRotational.Value ? "HDD" : "SSD/flash";
            return new BlockDeviceRow(
                info.Name,
                parent,
                info.Type,
                FormatBytes(info.SizeBytes),
                fileSystemType,
                mountPoint,
                model,
                media,
                $"{info.Name} {parent} {info.Type} {fileSystemType} {mountPoint} {model} {media}");
        }
    }

    private sealed record DirectoryRow(string Path, string SizeText)
    {
        public static DirectoryRow From(ServerDirectoryUsageInfo info) => new(info.Path, FormatBytes(info.SizeBytes));
    }

    private sealed class OperationScope : IDisposable
    {
        private readonly StorageWindow _owner;
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public OperationScope(StorageWindow owner, CancellationTokenSource source)
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
