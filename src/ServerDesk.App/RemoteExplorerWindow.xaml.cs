using System.Globalization;
using System.IO;
using System.Windows;
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
    private IRemoteFileSystem? _fileSystem;
    private CancellationTokenSource? _operationCancellation;
    private RemotePath _currentPath = RemotePath.Parse(".");
    private bool _hasLoadedPath;

    public RemoteExplorerWindow(
        IRemoteFileSystemFactory fileSystemFactory,
        ServerProfile profile,
        bool initiallyConnected)
    {
        _fileSystemFactory = fileSystemFactory ?? throw new ArgumentNullException(nameof(fileSystemFactory));
        _profile = profile ?? throw new ArgumentNullException(nameof(profile));
        _initiallyConnected = initiallyConnected;
        InitializeComponent();

        TitleText.Text = $"Explorer · {_profile.Name}";
        EndpointText.Text = $"{_profile.Username}@{_profile.Host}:{_profile.Port}";
        AddressBox.Text = _currentPath.Value;
        SetState(RemoteExplorerUiState.Disconnected, "Explorer is not connected yet. Refresh to establish an SFTP channel.");
    }

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
        if (_backHistory.TryPop(out var previous))
        {
            await LoadDirectoryAsync(previous, pushHistory: false);
        }
    }

    private async void UpOnClick(object sender, RoutedEventArgs e)
    {
        if (_hasLoadedPath && _currentPath.Parent != _currentPath)
        {
            await LoadDirectoryAsync(_currentPath.Parent, pushHistory: true);
        }
    }

    private async void AddressBoxOnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        try
        {
            await LoadDirectoryAsync(RemotePath.Parse(AddressBox.Text), pushHistory: true);
        }
        catch (ArgumentException exception)
        {
            SetState(RemoteExplorerUiState.Error, exception.Message);
        }
    }

    private async void FileGridOnMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (SelectedRow is { IsDirectory: true } row)
        {
            await LoadDirectoryAsync(row.Path, pushHistory: true);
        }
    }

    private async void NewFolderOnClick(object sender, RoutedEventArgs e)
    {
        var name = ExplorerTextPrompt.Show(this, "New remote folder", "Folder name:");
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
            SetState(RemoteExplorerUiState.Error, exception.Message);
            return;
        }

        if (await ExecuteMutationAsync(
                $"Creating {target.Value}…",
                (fileSystem, token) => fileSystem.CreateDirectoryAsync(target, token)))
        {
            await LoadDirectoryAsync(_currentPath, pushHistory: false);
        }
    }

    private async void RenameOnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            SetState(RemoteExplorerUiState.Error, "Select a file or folder to rename.");
            return;
        }

        var name = ExplorerTextPrompt.Show(this, "Rename remote item", "New name:", row.Name);
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
            SetState(RemoteExplorerUiState.Error, exception.Message);
            return;
        }

        if (await ExecuteMutationAsync(
                $"Renaming {row.Name}…",
                (fileSystem, token) => fileSystem.RenameAsync(row.Path, destination, overwrite: false, token)))
        {
            await LoadDirectoryAsync(_currentPath, pushHistory: false);
        }
    }

    private async void PermissionsOnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            SetState(RemoteExplorerUiState.Error, "Select an item to change permissions.");
            return;
        }

        var raw = ExplorerTextPrompt.Show(
            this,
            "Unix permissions",
            "Enter an octal mode such as 640 or 755. ServerDesk never widens permissions automatically.",
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
                throw new ArgumentException("Permission mode must contain octal digits only.");
            }

            permissions = RemoteUnixPermissions.FromMode(mode);
        }
        catch (ArgumentException exception)
        {
            SetState(RemoteExplorerUiState.Error, exception.Message);
            return;
        }

        if (MessageBox.Show(
                this,
                $"Change permissions for '{row.Name}' from {row.PermissionsText} to {permissions}?",
                "Confirm permission change",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            return;
        }

        if (await ExecuteMutationAsync(
                $"Changing permissions on {row.Name}…",
                (fileSystem, token) => fileSystem.SetPermissionsAsync(row.Path, permissions, token)))
        {
            await LoadDirectoryAsync(_currentPath, pushHistory: false);
        }
    }

    private async void DeleteOnClick(object sender, RoutedEventArgs e)
    {
        if (SelectedRow is not { } row)
        {
            SetState(RemoteExplorerUiState.Error, "Select an item to delete.");
            return;
        }

        var description = row.IsDirectory ? "empty directory" : "file";
        if (MessageBox.Show(
                this,
                $"Delete the remote {description} '{row.Path.Value}'?\n\nThis action cannot be undone by ServerDesk.",
                "Confirm remote delete",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        var succeeded = row.IsDirectory
            ? await ExecuteMutationAsync(
                $"Deleting {row.Name}…",
                (fileSystem, token) => fileSystem.DeleteDirectoryAsync(row.Path, token))
            : await ExecuteMutationAsync(
                $"Deleting {row.Name}…",
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
            Title = "Upload files to remote server",
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
            SetState(RemoteExplorerUiState.Error, "Select a file or symbolic link to download.");
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Download remote file",
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
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void WindowOnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            await UploadFilesAsync(paths.Where(File.Exists).ToArray());
        }
    }

    private async Task LoadDirectoryAsync(RemotePath path, bool pushHistory)
    {
        using var operation = BeginOperation();
        SetState(RemoteExplorerUiState.Loading, $"Loading {path.Value}…");
        FileGrid.ItemsSource = null;
        FooterText.Text = string.Empty;

        try
        {
            var fileSystem = await EnsureConnectedAsync(operation.Token);
            var entries = await fileSystem.ListAsync(path, operation.Token);
            var rows = RemoteExplorerProjection.Project(entries);

            if (pushHistory && _hasLoadedPath && path != _currentPath)
            {
                _backHistory.Push(_currentPath);
            }

            _currentPath = path;
            _hasLoadedPath = true;
            AddressBox.Text = path.Value;
            BackButton.IsEnabled = _backHistory.Count > 0;
            FileGrid.ItemsSource = rows;
            FooterText.Text = $"{rows.Count:N0} item(s) · SFTP · virtualized rows";
            SetState(
                rows.Count == 0 ? RemoteExplorerUiState.Empty : RemoteExplorerUiState.Ready,
                rows.Count == 0 ? "This directory is empty." : $"Loaded {rows.Count:N0} item(s). Double-click a folder to open it.");
        }
        catch (OperationCanceledException)
        {
            SetState(RemoteExplorerUiState.Cancelled, "Operation cancelled. No partial result was committed to the Explorer view.");
        }
        catch (RemoteFileSystemException exception)
        {
            SetState(RemoteExplorerProjection.Classify(exception.Error), $"{exception.Error.Code}: {exception.Error.Message}");
        }
        catch (Exception exception)
        {
            SetState(RemoteExplorerUiState.Error, exception.Message);
        }
    }

    private async Task<bool> ExecuteMutationAsync(
        string activity,
        Func<IRemoteFileSystem, CancellationToken, ValueTask> action)
    {
        using var operation = BeginOperation();
        SetState(RemoteExplorerUiState.Loading, activity);
        try
        {
            var fileSystem = await EnsureConnectedAsync(operation.Token);
            await action(fileSystem, operation.Token);
            SetState(RemoteExplorerUiState.Ready, "Remote operation completed.");
            return true;
        }
        catch (OperationCanceledException)
        {
            SetState(RemoteExplorerUiState.Cancelled, "Operation cancelled.");
        }
        catch (RemoteFileSystemException exception)
        {
            SetState(RemoteExplorerProjection.Classify(exception.Error), $"{exception.Error.Code}: {exception.Error.Message}");
        }
        catch (Exception exception)
        {
            SetState(RemoteExplorerUiState.Error, exception.Message);
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
                            $"'{destination.Value}' already exists. Replace it atomically?",
                            "Remote file conflict",
                            MessageBoxButton.YesNo,
                            MessageBoxImage.Warning) != MessageBoxResult.Yes)
                    {
                        continue;
                    }

                    await UploadOneAsync(fileSystem, fileInfo, destination, overwrite: true, operation.Token);
                }
            }

            SetState(RemoteExplorerUiState.Ready, $"Uploaded {localPaths.Count:N0} local item(s).");
            await LoadDirectoryAfterTransferAsync(operation.Token);
        }
        catch (OperationCanceledException)
        {
            SetState(RemoteExplorerUiState.Cancelled, "Upload cancelled. Atomic SFTP upload leaves no committed partial destination.");
        }
        catch (RemoteFileSystemException exception)
        {
            SetState(RemoteExplorerProjection.Classify(exception.Error), $"{exception.Error.Code}: {exception.Error.Message}");
        }
        catch (Exception exception)
        {
            SetState(RemoteExplorerUiState.Error, exception.Message);
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
        SetState(RemoteExplorerUiState.Loading, $"Uploading {localFile.Name}…");
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
            SetState(RemoteExplorerUiState.Loading, $"Downloading {row.Name}…");
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
            SetState(RemoteExplorerUiState.Ready, $"Downloaded {row.Name} safely to {destinationPath}.");
        }
        catch (OperationCanceledException)
        {
            SetState(RemoteExplorerUiState.Cancelled, "Download cancelled. The local destination was not replaced.");
        }
        catch (RemoteFileSystemException exception)
        {
            SetState(RemoteExplorerProjection.Classify(exception.Error), $"{exception.Error.Code}: {exception.Error.Message}");
        }
        catch (Exception exception)
        {
            SetState(RemoteExplorerUiState.Error, exception.Message);
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
        var rows = RemoteExplorerProjection.Project(await fileSystem.ListAsync(_currentPath, cancellationToken));
        FileGrid.ItemsSource = rows;
        FooterText.Text = $"{rows.Count:N0} item(s) · SFTP · virtualized rows";
        SetState(rows.Count == 0 ? RemoteExplorerUiState.Empty : RemoteExplorerUiState.Ready, $"Loaded {rows.Count:N0} item(s).");
    }

    private async Task<IRemoteFileSystem> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        _fileSystem ??= _fileSystemFactory.Create(_profile);
        if (!_fileSystem.IsConnected)
        {
            await _fileSystem.ConnectAsync(cancellationToken);
        }

        return _fileSystem;
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

        StatusText.Text = $"{progress.Direction}: {name} · {RemoteExplorerProjection.FormatBytes(progress.BytesTransferred)}";
    }

    private void SetState(RemoteExplorerUiState state, string message)
    {
        StatusText.Text = $"{state}: {message}";
    }

    private RemoteExplorerRow? SelectedRow => FileGrid.SelectedItem as RemoteExplorerRow;

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
            }

            _source.Dispose();
        }
    }
}
