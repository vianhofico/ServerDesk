using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using ServerDesk.App.Presentation;
using ServerDesk.Application.Storage;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class StorageWindow : Window
{
    private readonly IServerStorageService _storageService;
    private readonly ServerProfile _profile;
    private readonly bool _hasConnection;
    private readonly List<StorageFilesystemRow> _allFilesystems = [];
    private readonly List<StorageBlockDeviceRow> _allBlockDevices = [];
    private CancellationTokenSource? _operationCancellation;
    private StorageOperationKind _activeOperation;
    private bool _isBusy;
    private bool _snapshotLoaded;
    private string? _inventoryOverrideText;
    private bool _inventoryOverrideShowsOverlay;

    public StorageWindow(
        IServerStorageService storageService,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _storageService = storageService ?? throw new ArgumentNullException(nameof(storageService));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _hasConnection = initiallyConnected;
        InitializeComponent();

        ServerNameText.Text = _profile.Name;
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        EnvironmentValueText.Text = string.IsNullOrWhiteSpace(_profile.Environment) ? "—" : _profile.Environment;
        ConnectionValueText.SetResourceReference(
            TextBlock.TextProperty,
            initiallyConnected ? "Loc.Storage.Connection.Connected" : "Loc.Storage.Connection.Disconnected");

        if (initiallyConnected)
        {
            StatusText.SetResourceReference(TextBlock.TextProperty, "Loc.Storage.State.Initial");
            AnalyzerStatusText.SetResourceReference(TextBlock.TextProperty, "Loc.Storage.Directory.Initial");
        }
        else
        {
            var disconnected = Resource("Loc.Storage.State.Disconnected");
            _inventoryOverrideText = disconnected;
            _inventoryOverrideShowsOverlay = true;
            StatusText.Text = disconnected;
            AnalyzerStatusText.Text = disconnected;
            ShowInventoryOverlays(disconnected);
            ShowDirectoryOverlay(disconnected);
        }

        ApplyFilter();
        UpdateCommandState();
    }

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_hasConnection)
        {
            await RefreshAsync().ConfigureAwait(true);
        }
    }

    private void WindowOnClosed(object? sender, EventArgs e) => CancelActiveOperation();

    private async void RefreshOnClick(object sender, RoutedEventArgs e) => await RefreshAsync().ConfigureAwait(true);

    private async void AnalyzeOnClick(object sender, RoutedEventArgs e) => await AnalyzeAsync().ConfigureAwait(true);

    private void CancelOnClick(object sender, RoutedEventArgs e) => CancelActiveOperation();

    private void SearchBoxOnTextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void ClearSearchOnClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        SearchBox.Focus();
    }

    private async Task RefreshAsync()
    {
        if (!_hasConnection || _isBusy)
        {
            if (!_hasConnection)
            {
                SetInventoryOverride(Resource("Loc.Storage.State.Disconnected"), showOverlay: true);
            }

            return;
        }

        using var operation = BeginOperation(StorageOperationKind.Refresh);
        SetInventoryOverride(Resource("Loc.Storage.State.Loading"), showOverlay: true);
        try
        {
            var result = await _storageService.InspectAsync(_profile, operation.Token).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                SetInventoryOverride(FormatError(result.Error!), showOverlay: true);
                return;
            }

            _allFilesystems.Clear();
            _allFilesystems.AddRange(StorageWorkspaceProjection.ProjectFilesystems(result.Filesystems));
            _allBlockDevices.Clear();
            _allBlockDevices.AddRange(StorageWorkspaceProjection.ProjectBlockDevices(result.BlockDevices));
            _snapshotLoaded = true;
            _inventoryOverrideText = null;
            _inventoryOverrideShowsOverlay = false;
            ApplyFilter();
        }
        catch (OperationCanceledException)
        {
            SetInventoryOverride(Resource("Loc.Storage.State.Cancelled"), showOverlay: true);
        }
        catch (Exception exception)
        {
            SetInventoryOverride(FormatResource("Loc.Storage.Error.Generic", exception.Message), showOverlay: true);
        }
    }

    private async Task AnalyzeAsync()
    {
        if (!_hasConnection || _isBusy)
        {
            if (!_hasConnection)
            {
                var disconnected = Resource("Loc.Storage.State.Disconnected");
                AnalyzerStatusText.Text = disconnected;
                ShowDirectoryOverlay(disconnected);
            }

            return;
        }

        var path = DirectoryPathBox.Text.Trim();
        if (path.Length == 0)
        {
            AnalyzerStatusText.Text = Resource("Loc.Storage.Directory.PathRequired");
            return;
        }

        using var operation = BeginOperation(StorageOperationKind.Analyze);
        var analyzing = FormatResource("Loc.Storage.Directory.Analyzing", path);
        AnalyzerStatusText.Text = analyzing;
        ShowDirectoryOverlay(analyzing);
        try
        {
            var result = await _storageService.AnalyzeDirectoryAsync(_profile, path, operation.Token).ConfigureAwait(true);
            if (!result.IsSuccess)
            {
                var error = FormatError(result.Error!);
                AnalyzerStatusText.Text = error;
                ShowDirectoryOverlay(error);
                return;
            }

            var rows = StorageWorkspaceProjection.ProjectDirectory(result.Entries);
            DirectoryGrid.ItemsSource = rows;
            if (rows.Count == 0)
            {
                var empty = Resource("Loc.Storage.Directory.Empty");
                AnalyzerStatusText.Text = empty;
                ShowDirectoryOverlay(empty);
            }
            else
            {
                AnalyzerStatusText.Text = FormatResource("Loc.Storage.Directory.Ready", rows.Count);
                HideDirectoryOverlay();
            }
        }
        catch (OperationCanceledException)
        {
            var cancelled = Resource("Loc.Storage.Directory.Cancelled");
            AnalyzerStatusText.Text = cancelled;
            ShowDirectoryOverlay(cancelled);
        }
        catch (Exception exception)
        {
            var error = FormatResource("Loc.Storage.Error.Generic", exception.Message);
            AnalyzerStatusText.Text = error;
            ShowDirectoryOverlay(error);
        }
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text.Trim();
        var filesystems = StorageWorkspaceProjection.FilterFilesystems(_allFilesystems, query);
        var devices = StorageWorkspaceProjection.FilterBlockDevices(_allBlockDevices, query);
        var summary = StorageWorkspaceProjection.Summarize(_allFilesystems, filesystems, _allBlockDevices, devices);

        FilesystemGrid.ItemsSource = filesystems;
        BlockGrid.ItemsSource = devices;
        ClearSearchButton.Visibility = query.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        FilesystemValueText.Text = summary.Filesystems.ToString("N0", CultureInfo.CurrentCulture);
        WarningValueText.Text = summary.WarningFilesystems.ToString("N0", CultureInfo.CurrentCulture);
        BlockValueText.Text = summary.BlockDevices.ToString("N0", CultureInfo.CurrentCulture);
        MountedValueText.Text = summary.MountedBlockDevices.ToString("N0", CultureInfo.CurrentCulture);
        VisibleValueText.Text = query.Length == 0
            ? string.Empty
            : $"{Resource("Loc.Storage.Summary.Visible")}: {summary.VisibleFilesystems:N0} FS · {summary.VisibleBlockDevices:N0} block";

        if (_inventoryOverrideText is not null)
        {
            StatusText.Text = _inventoryOverrideText;
            if (_inventoryOverrideShowsOverlay)
            {
                ShowInventoryOverlays(_inventoryOverrideText);
            }

            return;
        }

        if (!_snapshotLoaded)
        {
            HideInventoryOverlays();
            return;
        }

        if (query.Length > 0)
        {
            StatusText.Text = summary.VisibleFilesystems == 0 && summary.VisibleBlockDevices == 0
                ? Resource("Loc.Storage.State.NoMatch")
                : FormatResource(
                    "Loc.Storage.State.Filtered",
                    summary.VisibleFilesystems,
                    summary.Filesystems,
                    summary.VisibleBlockDevices,
                    summary.BlockDevices);
        }
        else if (summary.Filesystems == 0 && summary.BlockDevices == 0)
        {
            StatusText.Text = Resource("Loc.Storage.State.Empty");
        }
        else
        {
            StatusText.Text = summary.WarningFilesystems == 0
                ? FormatResource("Loc.Storage.State.Ready", summary.Filesystems, summary.BlockDevices)
                : FormatResource("Loc.Storage.State.ReadyWarning", summary.Filesystems, summary.BlockDevices, summary.WarningFilesystems);
        }

        UpdateInventoryOverlays(filesystems.Count, devices.Count, query.Length > 0);
    }

    private void UpdateInventoryOverlays(int visibleFilesystems, int visibleBlockDevices, bool filtered)
    {
        if (_allFilesystems.Count == 0 || filtered && visibleFilesystems == 0)
        {
            ShowFilesystemOverlay(filtered ? Resource("Loc.Storage.State.NoMatch") : Resource("Loc.Storage.State.Empty"));
        }
        else
        {
            FilesystemStateOverlay.Visibility = Visibility.Collapsed;
        }

        if (_allBlockDevices.Count == 0 || filtered && visibleBlockDevices == 0)
        {
            ShowBlockOverlay(filtered ? Resource("Loc.Storage.State.NoMatch") : Resource("Loc.Storage.State.Empty"));
        }
        else
        {
            BlockStateOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void SetInventoryOverride(string text, bool showOverlay)
    {
        _inventoryOverrideText = text;
        _inventoryOverrideShowsOverlay = showOverlay;
        StatusText.Text = text;
        if (showOverlay)
        {
            ShowInventoryOverlays(text);
        }
    }

    private void ShowInventoryOverlays(string text)
    {
        ShowFilesystemOverlay(text);
        ShowBlockOverlay(text);
    }

    private void HideInventoryOverlays()
    {
        FilesystemStateOverlay.Visibility = Visibility.Collapsed;
        BlockStateOverlay.Visibility = Visibility.Collapsed;
    }

    private void ShowFilesystemOverlay(string text)
    {
        FilesystemStateText.Text = text;
        FilesystemStateOverlay.Visibility = Visibility.Visible;
    }

    private void ShowBlockOverlay(string text)
    {
        BlockStateText.Text = text;
        BlockStateOverlay.Visibility = Visibility.Visible;
    }

    private void ShowDirectoryOverlay(string text)
    {
        DirectoryStateText.Text = text;
        DirectoryStateOverlay.Visibility = Visibility.Visible;
    }

    private void HideDirectoryOverlay() => DirectoryStateOverlay.Visibility = Visibility.Collapsed;

    private void UpdateCommandState()
    {
        RefreshButton.IsEnabled = _hasConnection && !_isBusy;
        AnalyzeButton.IsEnabled = _hasConnection && !_isBusy;
        DirectoryPathBox.IsEnabled = _hasConnection && !_isBusy;
        CancelButton.IsEnabled = _isBusy;
        SearchBox.IsEnabled = !_isBusy;
        ClearSearchButton.IsEnabled = !_isBusy;
    }

    private string FormatError(RemoteError error) => error.Code switch
    {
        RemoteErrorCode.PermissionDenied => FormatResource("Loc.Storage.Error.Permission", error.Message),
        RemoteErrorCode.CommandNotFound or RemoteErrorCode.CapabilityUnavailable =>
            FormatResource("Loc.Storage.Error.Capability", error.Message),
        RemoteErrorCode.PathNotFound => FormatResource("Loc.Storage.Error.NotFound", error.Message),
        RemoteErrorCode.NetworkInterrupted => FormatResource("Loc.Storage.Error.Disconnected", error.Message),
        RemoteErrorCode.OperationCancelled => FormatResource("Loc.Storage.Error.Cancelled", error.Message),
        _ => FormatResource("Loc.Storage.Error.Generic", error.Message),
    };

    private OperationScope BeginOperation(StorageOperationKind kind)
    {
        CancelActiveOperation();
        _operationCancellation = new CancellationTokenSource();
        _activeOperation = kind;
        _isBusy = true;
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

    private void EndOperation(CancellationTokenSource source)
    {
        if (!ReferenceEquals(_operationCancellation, source))
        {
            return;
        }

        _operationCancellation = null;
        _activeOperation = StorageOperationKind.None;
        _isBusy = false;
        UpdateCommandState();
    }

    private static string Resource(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as string ?? key;

    private static string FormatResource(string key, params object?[] arguments)
    {
        var template = Resource(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private enum StorageOperationKind
    {
        None,
        Refresh,
        Analyze,
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
            _owner.EndOperation(_source);
            _source.Dispose();
        }
    }
}
