using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.App;

public partial class RemoteExplorerWindow : Window
{
    private const int StreamBufferSize = 64 * 1024;

    private readonly IRemoteFileSystemFactory _fileSystemFactory;
    private readonly ServerProfile _profile;
    private readonly bool _initiallyConnected;
    private readonly Stack<RemotePath> _backHistory = new();
    private readonly Stack<RemotePath> _forwardHistory = new();
    private IRemoteFileSystem? _fileSystem;
    private CancellationTokenSource? _operationCancellation;
    private IReadOnlyList<RemoteExplorerRow> _loadedRows = [];
    private RemotePath _currentPath = RemotePath.Parse(".");
    private RemoteExplorerUiState _currentState = RemoteExplorerUiState.Disconnected;
    private string? _statusResourceKey;
    private string _statusRawMessage = string.Empty;
    private bool _hasLoadedPath;
    private bool _isAddressEditing;

    public RemoteExplorerWindow(
        IRemoteFileSystemFactory fileSystemFactory,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _fileSystemFactory = fileSystemFactory ?? throw new ArgumentNullException(nameof(fileSystemFactory));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = initiallyConnected;
        InitializeComponent();

        ServerNameText.Text = _profile.Name;
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        if (string.IsNullOrWhiteSpace(_profile.Environment))
        {
            EnvironmentValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Explorer.Header.Unlabeled");
        }
        else
        {
            EnvironmentValueText.Text = _profile.Environment;
        }

        ConnectionValueText.SetResourceReference(
            TextBlock.TextProperty,
            _initiallyConnected ? "Loc.Explorer.Header.Connected" : "Loc.Explorer.Header.OnDemand");
        AddressBox.Text = _currentPath.Value;
        BuildBreadcrumbs();
        SetAddressEditing(false);
        SetStateResource(RemoteExplorerUiState.Disconnected, "Loc.Explorer.Message.Initial");
        ApplyLocalFilter();
        UpdateCommandState();
    }

    private bool IsBusy => _operationCancellation is not null;

    private async void WindowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (_initiallyConnected)
        {
            await LoadDirectoryAsync(_currentPath, pushHistory: false);
        }
    }

    private async void WindowOnClosed(object? sender, EventArgs e)
    {
        CancelActiveOperation();
        if (_fileSystem is not null)
        {
            try
            {
                await _fileSystem.DisposeAsync();
            }
            catch
            {
                // Window shutdown must not be blocked by best-effort SFTP cleanup.
            }
        }
    }

    private async void RefreshOnClick(object sender, RoutedEventArgs e) =>
        await LoadDirectoryAsync(_currentPath, pushHistory: false);

    private async void BackOnClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy || !_backHistory.TryPop(out var previous))
        {
            return;
        }

        var origin = _currentPath;
        if (await LoadDirectoryAsync(previous, pushHistory: false))
        {
            _forwardHistory.Push(origin);
        }
        else
        {
            _backHistory.Push(previous);
        }

        UpdateCommandState();
    }

    private async void ForwardOnClick(object sender, RoutedEventArgs e)
    {
        if (IsBusy || !_forwardHistory.TryPop(out var next))
        {
            return;
        }

        var origin = _currentPath;
        if (await LoadDirectoryAsync(next, pushHistory: false))
        {
            _backHistory.Push(origin);
        }
        else
        {
            _forwardHistory.Push(next);
        }

        UpdateCommandState();
    }

    private async void UpOnClick(object sender, RoutedEventArgs e)
    {
        if (!IsBusy && _hasLoadedPath && _currentPath.Parent != _currentPath)
        {
            await LoadDirectoryAsync(_currentPath.Parent, pushHistory: true);
        }
    }

    private void EditAddressOnClick(object sender, RoutedEventArgs e) =>
        SetAddressEditing(!_isAddressEditing);

    private async void AddressBoxOnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            SetAddressEditing(false);
            return;
        }

        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        try
        {
            var succeeded = await LoadDirectoryAsync(RemotePath.Parse(AddressBox.Text), pushHistory: true);
            if (succeeded)
            {
                SetAddressEditing(false);
            }
        }
        catch (ArgumentException exception)
        {
            SetStateRaw(RemoteExplorerUiState.Error, exception.Message);
        }
    }

    private async void BreadcrumbOnClick(object sender, RoutedEventArgs e)
    {
        if (!IsBusy && sender is Button { Tag: RemotePath target } && target != _currentPath)
        {
            await LoadDirectoryAsync(target, pushHistory: true);
        }
    }

    private async void FileGridOnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!IsBusy && SelectedRow is { IsDirectory: true } row)
        {
            await LoadDirectoryAsync(row.Path, pushHistory: true);
        }
    }

    private void FileGridOnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCommandState();

    private void SearchBoxOnTextChanged(object sender, TextChangedEventArgs e) => ApplyLocalFilter();

    private void ClearSearchOnClick(object sender, RoutedEventArgs e)
    {
        SearchBox.Clear();
        SearchBox.Focus();
    }

    private async void NewFolderOnClick(object sender, RoutedEventArgs e)
    {
        var name = ExplorerTextPrompt.Show(
            this,
            Localize("Loc.Explorer.Prompt.NewFolder.Title"),
            Localize("Loc.Explorer.Prompt.NewFolder.Label"));
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        RemotePath target;
        try
        {
            target = _currentPath.Combine(name.Trim());
        }
        catch (ArgumentException exception)
        {
            SetStateRaw(RemoteExplorerUiState.Error, exception.Message);
            return;
        }

        if (await ExecuteMutationAsync(
                FormatLocalize("Loc.Explorer.Message.Creating", target.Value),
                (fileSystem, token) => fileSystem.CreateDirectoryAsync(target, token)))
        {
            await LoadDirectoryAsync(_currentPath, pushHistory: false);
        }
    }

    private async void RenameOnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            SetStateResource(RemoteExplorerUiState.Error, "Loc.Explorer.Message.SelectRename");
            return;
        }

        var name = ExplorerTextPrompt.Show(
            this,
            Localize("Loc.Explorer.Prompt.Rename.Title"),
            Localize("Loc.Explorer.Prompt.Rename.Label"),
            row.Name);
        if (string.IsNullOrWhiteSpace(name) || string.Equals(name.Trim(), row.Name, StringComparison.Ordinal))
        {
            return;
        }

        RemotePath destination;
        try
        {
            destination = row.Path.Parent.Combine(name.Trim());
        }
        catch (ArgumentException exception)
        {
            SetStateRaw(RemoteExplorerUiState.Error, exception.Message);
            return;
        }

        if (await ExecuteMutationAsync(
                FormatLocalize("Loc.Explorer.Message.Renaming", row.Name),
                (fileSystem, token) => fileSystem.RenameAsync(row.Path, destination, overwrite: false, token)))
        {
            await LoadDirectoryAsync(_currentPath, pushHistory: false);
        }
    }

    private async void PermissionsOnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            SetStateResource(RemoteExplorerUiState.Error, "Loc.Explorer.Message.SelectPermissions");
            return;
        }

        var raw = ExplorerTextPrompt.Show(
            this,
            Localize("Loc.Explorer.Prompt.Permissions.Title"),
            Localize("Loc.Explorer.Prompt.Permissions.Label"),
            row.PermissionsText);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        RemoteUnixPermissions permissions;
        try
        {
            if (!short.TryParse(raw.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var mode))
            {
                throw new ArgumentException(Localize("Loc.Explorer.Prompt.Permissions.Invalid"));
            }

            permissions = RemoteUnixPermissions.FromMode(mode);
        }
        catch (ArgumentException exception)
        {
            SetStateRaw(RemoteExplorerUiState.Error, exception.Message);
            return;
        }

        if (MessageBox.Show(
                this,
                FormatLocalize(
                    "Loc.Explorer.Confirm.Permissions.Body",
                    row.Name,
                    row.PermissionsText,
                    permissions),
                Localize("Loc.Explorer.Confirm.Permissions.Title"),
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        if (await ExecuteMutationAsync(
                FormatLocalize("Loc.Explorer.Message.ChangingPermissions", row.Name),
                (fileSystem, token) => fileSystem.SetPermissionsAsync(row.Path, permissions, token)))
        {
            await LoadDirectoryAsync(_currentPath, pushHistory: false);
        }
    }

    private async void DeleteOnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            SetStateResource(RemoteExplorerUiState.Error, "Loc.Explorer.Message.SelectDelete");
            return;
        }

        var description = Localize(
            row.IsDirectory ? "Loc.Explorer.Confirm.Delete.Directory" : "Loc.Explorer.Confirm.Delete.File");
        if (MessageBox.Show(
                this,
                FormatLocalize("Loc.Explorer.Confirm.Delete.Body", description, row.Path.Value),
                Localize("Loc.Explorer.Confirm.Delete.Title"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var succeeded = row.IsDirectory
            ? await ExecuteMutationAsync(
                FormatLocalize("Loc.Explorer.Message.Deleting", row.Name),
                (fileSystem, token) => fileSystem.DeleteDirectoryAsync(row.Path, token))
            : await ExecuteMutationAsync(
                FormatLocalize("Loc.Explorer.Message.Deleting", row.Name),
                (fileSystem, token) => fileSystem.DeleteFileAsync(row.Path, token));

        if (succeeded)
        {
            await LoadDirectoryAsync(_currentPath, pushHistory: false);
        }
    }

    private async void UploadOnClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = Localize("Loc.Explorer.Dialog.Upload.Title"),
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true)
        {
            await UploadFilesAsync(dialog.FileNames);
        }
    }

    private async void DownloadOnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { IsDownloadable: true } row)
        {
            SetStateResource(RemoteExplorerUiState.Error, "Loc.Explorer.Message.SelectDownload");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = Localize("Loc.Explorer.Dialog.Download.Title"),
            FileName = row.Name,
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        await DownloadAsync(row, dialog.FileName);
    }

    private void CancelOnClick(object sender, RoutedEventArgs e) => CancelActiveOperation();

    private void WindowOnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void WindowOnDrop(object sender, DragEventArgs e)
    {
        if (!IsBusy && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            await UploadFilesAsync(paths.Where(File.Exists).ToArray());
        }
    }

    private async Task<bool> LoadDirectoryAsync(RemotePath path, bool pushHistory)
    {
        using var operation = BeginOperation();
        SetStateResource(RemoteExplorerUiState.Loading, "Loc.Explorer.Message.LoadingDirectory");

        try
        {
            var fileSystem = await EnsureConnectedAsync(operation.Token);
            var entries = await fileSystem.ListAsync(path, operation.Token);
            var rows = RemoteExplorerProjection.Project(entries);

            if (pushHistory && _hasLoadedPath && path != _currentPath)
            {
                _backHistory.Push(_currentPath);
                _forwardHistory.Clear();
            }

            _currentPath = path;
            _hasLoadedPath = true;
            _loadedRows = rows;
            AddressBox.Text = path.Value;
            BuildBreadcrumbs();
            ConnectionValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Explorer.Header.Connected");
            SetStateResource(
                rows.Count == 0 ? RemoteExplorerUiState.Empty : RemoteExplorerUiState.Ready,
                rows.Count == 0 ? "Loc.Explorer.Message.Empty" : "Loc.Explorer.Message.Loaded");
            ApplyLocalFilter();
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStateResource(RemoteExplorerUiState.Cancelled, "Loc.Explorer.Message.ReadCancelled");
        }
        catch (RemoteFileSystemException exception)
        {
            SetStateRaw(RemoteExplorerProjection.Classify(exception.Error), $"{exception.Error.Code}: {exception.Error.Message}");
        }
        catch (Exception exception)
        {
            SetStateRaw(RemoteExplorerUiState.Error, exception.Message);
        }

        ApplyLocalFilter();
        return false;
    }

    private async Task<bool> ExecuteMutationAsync(
        string activity,
        Func<IRemoteFileSystem, CancellationToken, ValueTask> action)
    {
        using var operation = BeginOperation();
        SetStateRaw(RemoteExplorerUiState.Loading, activity);
        try
        {
            var fileSystem = await EnsureConnectedAsync(operation.Token);
            await action(fileSystem, operation.Token);
            SetStateResource(RemoteExplorerUiState.Ready, "Loc.Explorer.Message.MutationCompleted");
            return true;
        }
        catch (OperationCanceledException)
        {
            SetStateResource(RemoteExplorerUiState.Cancelled, "Loc.Explorer.Message.OperationCancelled");
        }
        catch (RemoteFileSystemException exception)
        {
            SetStateRaw(RemoteExplorerProjection.Classify(exception.Error), $"{exception.Error.Code}: {exception.Error.Message}");
        }
        catch (Exception exception)
        {
            SetStateRaw(RemoteExplorerUiState.Error, exception.Message);
        }

        return false;
    }

    private async Task UploadFilesAsync(IReadOnlyCollection<string> localPaths)
    {
        if (localPaths.Count == 0)
        {
            return;
        }

        using var operation = BeginOperation();
        TransferProgress.Visibility = Visibility.Visible;
        try
        {
            var fileSystem = await EnsureConnectedAsync(operation.Token);
            foreach (var localPath in localPaths)
            {
                operation.Token.ThrowIfCancellationRequested();
                var fileInfo = new FileInfo(localPath);
                if (!fileInfo.Exists)
                {
                    continue;
                }

                var destination = _currentPath.Combine(fileInfo.Name);
                try
                {
                    await UploadOneAsync(fileSystem, fileInfo, destination, overwrite: false, operation.Token);
                }
                catch (RemoteFileSystemException exception) when (exception.Error.Code == RemoteErrorCode.PathConflict)
                {
                    if (MessageBox.Show(
                            this,
                            FormatLocalize("Loc.Explorer.Conflict.Body", destination.Value),
                            Localize("Loc.Explorer.Conflict.Title"),
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        continue;
                    }

                    await UploadOneAsync(fileSystem, fileInfo, destination, overwrite: true, operation.Token);
                }
            }

            SetStateRaw(
                RemoteExplorerUiState.Ready,
                FormatLocalize("Loc.Explorer.Message.Uploaded", localPaths.Count));
            await LoadDirectoryAfterTransferAsync(operation.Token);
        }
        catch (OperationCanceledException)
        {
            SetStateResource(RemoteExplorerUiState.Cancelled, "Loc.Explorer.Message.UploadCancelled");
        }
        catch (RemoteFileSystemException exception)
        {
            SetStateRaw(RemoteExplorerProjection.Classify(exception.Error), $"{exception.Error.Code}: {exception.Error.Message}");
        }
        catch (Exception exception)
        {
            SetStateRaw(RemoteExplorerUiState.Error, exception.Message);
        }
        finally
        {
            TransferProgress.Visibility = Visibility.Collapsed;
            TransferProgress.Value = 0;
        }
    }

    private async Task UploadOneAsync(
        IRemoteFileSystem fileSystem,
        FileInfo localFile,
        RemotePath destination,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        SetStateRaw(
            RemoteExplorerUiState.Loading,
            FormatLocalize("Loc.Explorer.Message.Uploading", localFile.Name));
        await using var stream = new FileStream(
            localFile.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            StreamBufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var progress = new Progress<RemoteTransferProgress>(value =>
            UpdateTransferProgress(localFile.Name, value));
        await fileSystem.UploadAsync(
            stream,
            destination,
            localFile.Length,
            overwrite,
            progress,
            cancellationToken);
    }

    private async Task DownloadAsync(RemoteExplorerRow row, string destinationPath)
    {
        using var operation = BeginOperation();
        TransferProgress.Visibility = Visibility.Visible;
        var directory = Path.GetDirectoryName(destinationPath) ?? Environment.CurrentDirectory;
        var temporaryPath = Path.Combine(directory, $".serverdesk-download-{Guid.NewGuid():N}.part");
        var committed = false;

        try
        {
            var fileSystem = await EnsureConnectedAsync(operation.Token);
            SetStateRaw(
                RemoteExplorerUiState.Loading,
                FormatLocalize("Loc.Explorer.Message.Downloading", row.Name));
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             StreamBufferSize,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var progress = new Progress<RemoteTransferProgress>(value =>
                    UpdateTransferProgress(row.Name, value));
                await fileSystem.DownloadAsync(row.Path, stream, progress, operation.Token);
                await stream.FlushAsync(operation.Token);
            }

            operation.Token.ThrowIfCancellationRequested();
            File.Move(temporaryPath, destinationPath, overwrite: true);
            committed = true;
            SetStateRaw(
                RemoteExplorerUiState.Ready,
                FormatLocalize("Loc.Explorer.Message.Downloaded", row.Name, destinationPath));
        }
        catch (OperationCanceledException)
        {
            SetStateResource(RemoteExplorerUiState.Cancelled, "Loc.Explorer.Message.DownloadCancelled");
        }
        catch (RemoteFileSystemException exception)
        {
            SetStateRaw(RemoteExplorerProjection.Classify(exception.Error), $"{exception.Error.Code}: {exception.Error.Message}");
        }
        catch (Exception exception)
        {
            SetStateRaw(RemoteExplorerUiState.Error, exception.Message);
        }
        finally
        {
            if (!committed)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                    // Best-effort cleanup of caller-local temporary download file.
                }
            }

            TransferProgress.Visibility = Visibility.Collapsed;
            TransferProgress.Value = 0;
        }
    }

    private async Task LoadDirectoryAfterTransferAsync(CancellationToken cancellationToken)
    {
        var fileSystem = await EnsureConnectedAsync(cancellationToken);
        _loadedRows = RemoteExplorerProjection.Project(await fileSystem.ListAsync(_currentPath, cancellationToken));
        SetStateResource(
            _loadedRows.Count == 0 ? RemoteExplorerUiState.Empty : RemoteExplorerUiState.Ready,
            _loadedRows.Count == 0 ? "Loc.Explorer.Message.Empty" : "Loc.Explorer.Message.Loaded");
        ApplyLocalFilter();
    }

    private async Task<IRemoteFileSystem> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        _fileSystem ??= _fileSystemFactory.Create(_profile);
        if (!_fileSystem.IsConnected)
        {
            await _fileSystem.ConnectAsync(cancellationToken);
        }

        ConnectionValueText.SetResourceReference(TextBlock.TextProperty, "Loc.Explorer.Header.Connected");
        return _fileSystem;
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

    private void UpdateTransferProgress(string name, RemoteTransferProgress progress)
    {
        if (progress.TotalBytes is > 0)
        {
            TransferProgress.IsIndeterminate = false;
            TransferProgress.Value = Math.Clamp(100d * progress.BytesTransferred / progress.TotalBytes.Value, 0d, 100d);
        }
        else
        {
            TransferProgress.IsIndeterminate = true;
        }

        StatusMessageText.Text = $"{progress.Direction}: {name} · {RemoteExplorerProjection.FormatBytes(progress.BytesTransferred)}";
    }

    private void ApplyLocalFilter()
    {
        var query = SearchBox.Text;
        var visibleRows = RemoteExplorerProjection.Filter(_loadedRows, query);
        FileGrid.ItemsSource = visibleRows;
        ClearSearchButton.Visibility = string.IsNullOrWhiteSpace(query) ? Visibility.Collapsed : Visibility.Visible;
        FooterText.Text = string.IsNullOrWhiteSpace(query)
            ? FormatLocalize("Loc.Explorer.Footer.All", _loadedRows.Count)
            : FormatLocalize("Loc.Explorer.Footer.Filtered", visibleRows.Count, _loadedRows.Count);

        if (_currentState == RemoteExplorerUiState.Ready &&
            _loadedRows.Count > 0 &&
            visibleRows.Count == 0 &&
            !string.IsNullOrWhiteSpace(query))
        {
            GridStateOverlay.Visibility = Visibility.Visible;
            GridStateTitle.SetResourceReference(TextBlock.TextProperty, "Loc.Explorer.Overlay.SearchTitle");
            GridStateDetail.SetResourceReference(TextBlock.TextProperty, "Loc.Explorer.Overlay.SearchDetail");
        }
        else
        {
            RefreshGridOverlay();
        }

        UpdateCommandState();
    }

    private void SetStateResource(RemoteExplorerUiState state, string messageResourceKey)
    {
        _currentState = state;
        _statusResourceKey = messageResourceKey;
        _statusRawMessage = string.Empty;
        StatusStateText.SetResourceReference(TextBlock.TextProperty, StateResourceKey(state));
        StatusMessageText.SetResourceReference(TextBlock.TextProperty, messageResourceKey);
        RefreshStateChrome();
    }

    private void SetStateRaw(RemoteExplorerUiState state, string message)
    {
        _currentState = state;
        _statusResourceKey = null;
        _statusRawMessage = message;
        StatusStateText.SetResourceReference(TextBlock.TextProperty, StateResourceKey(state));
        StatusMessageText.Text = message;
        RefreshStateChrome();
    }

    private void RefreshStateChrome()
    {
        StatusCard.Style = (Style)FindResource(
            _currentState is RemoteExplorerUiState.Error or RemoteExplorerUiState.PermissionDenied
                ? "InlineErrorCard"
                : "InlineInfoCard");
        RefreshGridOverlay();
        UpdateCommandState();
    }

    private void RefreshGridOverlay()
    {
        string? titleKey = null;
        string? detailKey = null;
        string? rawDetail = null;

        if (_currentState == RemoteExplorerUiState.Loading)
        {
            titleKey = "Loc.Explorer.Overlay.LoadingTitle";
            detailKey = _statusResourceKey;
            rawDetail = _statusRawMessage;
        }
        else if (_currentState == RemoteExplorerUiState.Empty)
        {
            titleKey = "Loc.Explorer.Overlay.EmptyTitle";
            detailKey = "Loc.Explorer.Overlay.EmptyDetail";
        }
        else if (_loadedRows.Count == 0 &&
                 _currentState is RemoteExplorerUiState.Disconnected or
                     RemoteExplorerUiState.PermissionDenied or
                     RemoteExplorerUiState.Error or
                     RemoteExplorerUiState.Cancelled)
        {
            titleKey = StateResourceKey(_currentState);
            detailKey = _statusResourceKey;
            rawDetail = _statusRawMessage;
        }

        if (titleKey is null)
        {
            GridStateOverlay.Visibility = Visibility.Collapsed;
            return;
        }

        GridStateOverlay.Visibility = Visibility.Visible;
        GridStateTitle.SetResourceReference(TextBlock.TextProperty, titleKey);
        if (detailKey is not null)
        {
            GridStateDetail.SetResourceReference(TextBlock.TextProperty, detailKey);
        }
        else
        {
            GridStateDetail.Text = rawDetail;
        }
    }

    private void UpdateCommandState()
    {
        var selected = SelectedRow;
        var busy = IsBusy;

        BackButton.IsEnabled = !busy && _backHistory.Count > 0;
        ForwardButton.IsEnabled = !busy && _forwardHistory.Count > 0;
        UpButton.IsEnabled = !busy && _hasLoadedPath && _currentPath.Parent != _currentPath;
        EditPathButton.IsEnabled = !busy;
        RefreshButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        NewFolderButton.IsEnabled = !busy;
        UploadButton.IsEnabled = !busy;
        DownloadButton.IsEnabled = !busy && selected?.IsDownloadable == true;
        EditButton.IsEnabled = !busy && selected?.IsDownloadable == true;
        RenameButton.IsEnabled = !busy && selected is not null;
        PermissionsButton.IsEnabled = !busy && selected is not null;
        DeleteButton.IsEnabled = !busy && selected is not null;
        FileGrid.IsEnabled = !busy;
        AddressBox.IsEnabled = !busy;
        BreadcrumbPanel.IsEnabled = !busy;
    }

    private void SetAddressEditing(bool editing)
    {
        _isAddressEditing = editing;
        BreadcrumbScroll.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        AddressBox.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        if (editing)
        {
            AddressBox.Text = _currentPath.Value;
            AddressBox.Focus();
            AddressBox.SelectAll();
        }
        else
        {
            AddressBox.Text = _currentPath.Value;
            BuildBreadcrumbs();
        }
    }

    private void BuildBreadcrumbs()
    {
        BreadcrumbPanel.Children.Clear();
        foreach (var part in BuildBreadcrumbParts(_currentPath))
        {
            if (BreadcrumbPanel.Children.Count > 0)
            {
                BreadcrumbPanel.Children.Add(new TextBlock
                {
                    Text = "›",
                    Margin = new Thickness(2, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (System.Windows.Media.Brush)FindResource("MutedTextBrush"),
                });
            }

            var button = new Button
            {
                Content = part.Label,
                Tag = part.Path,
                Style = (Style)FindResource("GhostButton"),
                Padding = new Thickness(8, 4, 8, 4),
                MinHeight = 30,
                ToolTip = part.Path.Value,
            };
            AutomationProperties.SetName(button, part.Path.Value);
            button.Click += BreadcrumbOnClick;
            BreadcrumbPanel.Children.Add(button);
        }
    }

    private static IReadOnlyList<BreadcrumbPart> BuildBreadcrumbParts(RemotePath path)
    {
        if (path.Value == ".")
        {
            return [new BreadcrumbPart(".", path)];
        }

        var parts = new List<BreadcrumbPart>();
        if (path.IsAbsolute)
        {
            var root = RemotePath.Parse("/");
            parts.Add(new BreadcrumbPart("/", root));
            var current = root;
            foreach (var segment in path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                current = current.Combine(segment);
                parts.Add(new BreadcrumbPart(segment, current));
            }

            return parts;
        }

        RemotePath? relative = null;
        foreach (var segment in path.Value.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            relative = relative is null ? RemotePath.Parse(segment) : relative.Value.Combine(segment);
            parts.Add(new BreadcrumbPart(segment, relative.Value));
        }

        return parts;
    }

    private static string StateResourceKey(RemoteExplorerUiState state) => $"Loc.Explorer.State.{state}";

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

    private RemoteExplorerRow? SelectedRow => FileGrid.SelectedItem as RemoteExplorerRow;

    private sealed record BreadcrumbPart(string Label, RemotePath Path);

    private sealed class OperationScope : IDisposable
    {
        private readonly RemoteExplorerWindow _owner;
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        public OperationScope(RemoteExplorerWindow owner, CancellationTokenSource source)
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
