using System.Buffers;
using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Routing;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Infrastructure.Ssh;

public sealed class RouteAwareSftpRemoteFileSystemFactory : IRemoteFileSystemFactory
{
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;

    public RouteAwareSftpRemoteFileSystemFactory(
        ISecretStore secretStore,
        IHostTrustService hostTrustService,
        IInteractiveAuthenticationPrompt interactivePrompt,
        SshSessionOptions options,
        IConnectionRouteRepository routeRepository,
        IProfileRepository profileRepository)
    {
        _options = options;
        _clientFactory = new SshClientFactory(
            secretStore,
            hostTrustService,
            interactivePrompt,
            options,
            routeRepository,
            profileRepository);
    }

    public IRemoteFileSystem Create(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new RouteAwareSftpRemoteFileSystem(profile, _clientFactory, _options);
    }
}

internal sealed class RouteAwareSftpRemoteFileSystem : IRemoteFileSystem
{
    private const int TransferBufferSize = 64 * 1024;

    private readonly ServerProfile _profile;
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private SftpClient? _client;
    private SshConnectionPlan? _plan;
    private bool _disposed;

    public RouteAwareSftpRemoteFileSystem(
        ServerProfile profile,
        SshClientFactory clientFactory,
        SshSessionOptions options)
    {
        _profile = profile;
        _clientFactory = clientFactory;
        _options = options;
    }

    public Guid ServerProfileId => _profile.Id;

    public bool IsConnected => _client?.IsConnected == true;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client?.IsConnected == true)
            {
                return;
            }

            DisposeConnection();
            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(_options.ConnectTimeout);
            try
            {
                _plan = await _clientFactory.CreateConnectionPlanAsync(
                        _profile,
                        SshChannelPurpose.FileTransfer,
                        timeoutCancellation.Token)
                    .ConfigureAwait(false);
                var plan = _plan;
                var client = new SftpClient(plan.ConnectionInfo)
                {
                    KeepAliveInterval = _options.KeepAliveInterval,
                };
                client.HostKeyReceived += (_, eventArgs) =>
                    _clientFactory.VerifyHostKey(_profile, plan.State, eventArgs, timeoutCancellation.Token);
                _client = client;

                await client.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);
                if (!client.IsConnected)
                {
                    throw new SshConnectionException(
                        "SSH.NET completed SFTP connection establishment without an active session.");
                }
            }
            catch (Exception exception)
            {
                var timedOut = !cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested;
                var error = MapConnectError(
                    exception,
                    _plan?.State,
                    timedOut,
                    cancellationToken.IsCancellationRequested);
                DisposeConnection();

                if (error.Code == RemoteErrorCode.OperationCancelled && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(error.Message, exception, cancellationToken);
                }

                throw exception is RemoteFileSystemException fileSystemException
                    ? fileSystemException
                    : new RemoteFileSystemException(error, exception);
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _connectionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is null)
            {
                return;
            }

            try
            {
                if (_client.IsConnected)
                {
                    await Task.Run(_client.Disconnect, CancellationToken.None)
                        .WaitAsync(_options.DisconnectTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (TimeoutException)
            {
                // Disposal below still closes the local socket and any route resources.
            }
            catch (Exception exception)
            {
                throw MapOperationException(exception, "disconnect from SFTP", path: null);
            }
            finally
            {
                DisposeConnection();
            }
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(
        RemotePath path,
        CancellationToken cancellationToken = default)
    {
        var client = GetConnectedClient();
        try
        {
            var entries = new List<RemoteFileEntry>();
            await foreach (var file in client.ListDirectoryAsync(path.Value, cancellationToken)
                               .WithCancellation(cancellationToken)
                               .ConfigureAwait(false))
            {
                if (file.Name is not "." and not "..")
                {
                    entries.Add(ToEntry(file));
                }
            }

            return entries;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapOperationException(exception, "list remote directory", path);
        }
    }

    public async ValueTask<RemoteFileEntry> StatAsync(
        RemotePath path,
        CancellationToken cancellationToken = default)
    {
        var client = GetConnectedClient();
        try
        {
            return ToEntry(await client.GetAsync(path.Value, cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapOperationException(exception, "read remote file metadata", path);
        }
    }

    public async ValueTask CreateDirectoryAsync(
        RemotePath path,
        CancellationToken cancellationToken = default)
    {
        var client = GetConnectedClient();
        try
        {
            if (await client.ExistsAsync(path.Value, cancellationToken).ConfigureAwait(false))
            {
                throw CreateError(RemoteErrorCode.PathConflict, $"Remote path '{path.Value}' already exists.");
            }

            await client.CreateDirectoryAsync(path.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapOperationException(exception, "create remote directory", path, RemoteErrorCode.PathConflict);
        }
    }

    public async ValueTask RenameAsync(
        RemotePath source,
        RemotePath destination,
        bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        var client = GetConnectedClient();
        try
        {
            if (!await client.ExistsAsync(source.Value, cancellationToken).ConfigureAwait(false))
            {
                throw CreateError(RemoteErrorCode.PathNotFound, $"Remote path '{source.Value}' was not found.");
            }

            if (!overwrite && await client.ExistsAsync(destination.Value, cancellationToken).ConfigureAwait(false))
            {
                throw CreateError(RemoteErrorCode.PathConflict, $"Remote path '{destination.Value}' already exists.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (overwrite)
            {
                await Task.Run(
                        () => client.RenameFile(source.Value, destination.Value, isPosix: true),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await client.RenameFileAsync(source.Value, destination.Value, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapOperationException(exception, "rename remote path", source, RemoteErrorCode.PathConflict);
        }
    }

    public async ValueTask DeleteFileAsync(
        RemotePath path,
        CancellationToken cancellationToken = default)
    {
        var client = GetConnectedClient();
        try
        {
            await client.DeleteFileAsync(path.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapOperationException(exception, "delete remote file", path);
        }
    }

    public async ValueTask DeleteDirectoryAsync(
        RemotePath path,
        CancellationToken cancellationToken = default)
    {
        var client = GetConnectedClient();
        try
        {
            await client.DeleteDirectoryAsync(path.Value, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapOperationException(exception, "delete remote directory", path);
        }
    }

    public async ValueTask SetPermissionsAsync(
        RemotePath path,
        RemoteUnixPermissions permissions,
        CancellationToken cancellationToken = default)
    {
        var client = GetConnectedClient();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Run(
                    () => client.ChangePermissions(path.Value, permissions.Mode),
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapOperationException(exception, "change remote permissions", path);
        }
    }

    public async ValueTask UploadAsync(
        Stream source,
        RemotePath destination,
        long? totalBytes = null,
        bool overwrite = false,
        IProgress<RemoteTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException("Upload source stream must be readable.", nameof(source));
        }

        if (totalBytes is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBytes));
        }

        if (destination.Value is "/" or ".")
        {
            throw new ArgumentException("Upload destination must identify a file.", nameof(destination));
        }

        var client = GetConnectedClient();
        var temporaryPath = destination.Parent.Combine($".serverdesk-upload-{Guid.NewGuid():N}.part");
        var committed = false;
        try
        {
            if (!overwrite && await client.ExistsAsync(destination.Value, cancellationToken).ConfigureAwait(false))
            {
                throw CreateError(RemoteErrorCode.PathConflict, $"Remote path '{destination.Value}' already exists.");
            }

            await using (var remoteStream = await client.OpenAsync(
                             temporaryPath.Value,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             cancellationToken)
                         .ConfigureAwait(false))
            {
                await CopyBoundedAsync(
                        source,
                        remoteStream,
                        RemoteTransferDirection.Upload,
                        totalBytes,
                        progress,
                        cancellationToken)
                    .ConfigureAwait(false);
                await remoteStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (!overwrite && await client.ExistsAsync(destination.Value, cancellationToken).ConfigureAwait(false))
            {
                throw CreateError(
                    RemoteErrorCode.PathConflict,
                    $"Remote path '{destination.Value}' was created while the upload was in progress.");
            }

            if (overwrite)
            {
                await Task.Run(
                        () => client.RenameFile(temporaryPath.Value, destination.Value, isPosix: true),
                        CancellationToken.None)
                    .ConfigureAwait(false);
            }
            else
            {
                await client.RenameFileAsync(temporaryPath.Value, destination.Value, cancellationToken)
                    .ConfigureAwait(false);
            }

            committed = true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapOperationException(exception, "upload remote file", destination, RemoteErrorCode.PathConflict);
        }
        finally
        {
            if (!committed)
            {
                await TryDeleteTemporaryFileAsync(temporaryPath).ConfigureAwait(false);
            }
        }
    }

    public async ValueTask DownloadAsync(
        RemotePath source,
        Stream destination,
        IProgress<RemoteTransferProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("Download destination stream must be writable.", nameof(destination));
        }

        var client = GetConnectedClient();
        try
        {
            var metadata = await client.GetAsync(source.Value, cancellationToken).ConfigureAwait(false);
            if (!metadata.IsRegularFile && !metadata.IsSymbolicLink)
            {
                throw CreateError(
                    RemoteErrorCode.PathConflict,
                    $"Remote path '{source.Value}' is not a downloadable file.");
            }

            await using var remoteStream = await client.OpenAsync(
                    source.Value,
                    FileMode.Open,
                    FileAccess.Read,
                    cancellationToken)
                .ConfigureAwait(false);
            await CopyBoundedAsync(
                    remoteStream,
                    destination,
                    RemoteTransferDirection.Download,
                    metadata.Length >= 0 ? metadata.Length : null,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw MapOperationException(exception, "download remote file", source);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _connectionGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            DisposeConnection();
        }
        finally
        {
            _connectionGate.Release();
            _connectionGate.Dispose();
        }
    }

    private async ValueTask TryDeleteTemporaryFileAsync(RemotePath temporaryPath)
    {
        var client = _client;
        if (client?.IsConnected != true)
        {
            return;
        }

        try
        {
            if (await client.ExistsAsync(temporaryPath.Value, CancellationToken.None).ConfigureAwait(false))
            {
                await client.DeleteFileAsync(temporaryPath.Value, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
            // Best effort. Explorer refresh exposes an orphaned staged file if cleanup cannot complete.
        }
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        RemoteTransferDirection direction,
        long? totalBytes,
        IProgress<RemoteTransferProgress>? progress,
        CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(TransferBufferSize);
        long transferred = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(
                        buffer.AsMemory(0, TransferBufferSize),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                transferred += read;
                progress?.Report(new RemoteTransferProgress(direction, transferred, totalBytes));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private SftpClient GetConnectedClient()
    {
        ThrowIfDisposed();
        if (_client?.IsConnected != true)
        {
            throw CreateError(
                RemoteErrorCode.NetworkInterrupted,
                "The SFTP channel is not connected. Connect it before performing remote filesystem operations.");
        }

        return _client;
    }

    private void DisposeConnection()
    {
        _client?.Dispose();
        _client = null;
        _plan?.Dispose();
        _plan = null;
    }

    private static RemoteFileEntry ToEntry(ISftpFile file)
    {
        var attributes = file.Attributes;
        DateTimeOffset? lastWrite = attributes.LastWriteTimeUtc == DateTime.MinValue
            ? null
            : new DateTimeOffset(DateTime.SpecifyKind(attributes.LastWriteTimeUtc, DateTimeKind.Utc));
        var kind = file.IsSymbolicLink
            ? RemoteFileKind.SymbolicLink
            : file.IsDirectory
                ? RemoteFileKind.Directory
                : file.IsRegularFile
                    ? RemoteFileKind.File
                    : RemoteFileKind.Other;

        return new RemoteFileEntry(
            RemotePath.Parse(file.FullName),
            file.Name,
            kind,
            file.Length,
            lastWrite,
            file.UserId < 0 ? null : file.UserId,
            file.GroupId < 0 ? null : file.GroupId,
            RemoteUnixPermissions.FromMode(BuildPermissionMode(attributes)));
    }

    private static short BuildPermissionMode(SftpFileAttributes attributes)
    {
        var special = (attributes.IsUIDBitSet ? 4 : 0) +
                      (attributes.IsGroupIDBitSet ? 2 : 0) +
                      (attributes.IsStickyBitSet ? 1 : 0);
        var owner = (attributes.OwnerCanRead ? 4 : 0) +
                    (attributes.OwnerCanWrite ? 2 : 0) +
                    (attributes.OwnerCanExecute ? 1 : 0);
        var group = (attributes.GroupCanRead ? 4 : 0) +
                    (attributes.GroupCanWrite ? 2 : 0) +
                    (attributes.GroupCanExecute ? 1 : 0);
        var other = (attributes.OthersCanRead ? 4 : 0) +
                    (attributes.OthersCanWrite ? 2 : 0) +
                    (attributes.OthersCanExecute ? 1 : 0);
        return (short)(special * 1000 + owner * 100 + group * 10 + other);
    }

    private static RemoteError MapConnectError(
        Exception exception,
        SshConnectionState? state,
        bool timedOut,
        bool callerCancelled)
    {
        if (exception is RemoteFileSystemException fileSystemException)
        {
            return fileSystemException.Error;
        }

        if (exception is RemoteSessionException sessionException)
        {
            return sessionException.Error;
        }

        if (callerCancelled || state?.HostTrustBridgeException is OperationCanceledException ||
            state?.InteractiveAuthenticationCancelled == true)
        {
            return new RemoteError(RemoteErrorCode.OperationCancelled, "SFTP connection operation was cancelled.");
        }

        if (state?.HostTrustFailure is { } hostTrustFailure)
        {
            return hostTrustFailure.Outcome switch
            {
                HostTrustOutcome.RejectedUnknown => new RemoteError(
                    RemoteErrorCode.HostKeyUnknown,
                    "The SSH server identity was not trusted for SFTP."),
                HostTrustOutcome.RejectedChangedKey or HostTrustOutcome.RejectedChangedKeyAndForgotten => new RemoteError(
                    RemoteErrorCode.HostKeyMismatch,
                    "The SSH server identity does not match the saved host key. SFTP was blocked."),
                _ => new RemoteError(RemoteErrorCode.ConnectionFailed, "SSH host verification failed while opening SFTP."),
            };
        }

        if (state?.HostTrustBridgeException is { } hostTrustException)
        {
            return new RemoteError(
                RemoteErrorCode.ConnectionFailed,
                "ServerDesk could not complete SSH host verification for SFTP.",
                $"{hostTrustException.GetType().Name}: {hostTrustException.Message}");
        }

        if (state?.InteractiveAuthenticationException is { } interactiveException)
        {
            return new RemoteError(
                RemoteErrorCode.AuthenticationFailed,
                "Interactive SSH authentication for SFTP could not be completed.",
                $"{interactiveException.GetType().Name}: {interactiveException.Message}");
        }

        if (timedOut)
        {
            return new RemoteError(
                RemoteErrorCode.ConnectionFailed,
                "SFTP connection timed out before a secure file-transfer channel was established.");
        }

        return exception switch
        {
            SshAuthenticationException => new RemoteError(
                RemoteErrorCode.AuthenticationFailed,
                "SSH authentication failed while opening SFTP."),
            ProxyException => new RemoteError(
                RemoteErrorCode.ConnectionFailed,
                "ServerDesk could not establish the configured SSH proxy route for SFTP.",
                exception.Message),
            SocketException => new RemoteError(
                RemoteErrorCode.ConnectionFailed,
                "ServerDesk could not reach the SSH server for SFTP.",
                exception.Message),
            SshConnectionException => new RemoteError(
                RemoteErrorCode.ConnectionFailed,
                "The secure SFTP channel could not be established.",
                exception.Message),
            IOException => new RemoteError(
                RemoteErrorCode.NetworkInterrupted,
                "The SSH connection was interrupted while opening SFTP.",
                exception.Message),
            _ => new RemoteError(
                RemoteErrorCode.ConnectionFailed,
                "ServerDesk could not establish the SFTP channel.",
                $"{exception.GetType().Name}: {exception.Message}"),
        };
    }

    private static RemoteFileSystemException MapOperationException(
        Exception exception,
        string action,
        RemotePath? path,
        RemoteErrorCode? sshFailureCode = null)
    {
        if (exception is RemoteFileSystemException fileSystemException)
        {
            return fileSystemException;
        }

        var pathSuffix = path is null ? string.Empty : $" '{path.Value.Value}'";
        var error = exception switch
        {
            SftpPermissionDeniedException => new RemoteError(
                RemoteErrorCode.PermissionDenied,
                $"Permission was denied while trying to {action}{pathSuffix}."),
            SftpPathNotFoundException => new RemoteError(
                RemoteErrorCode.PathNotFound,
                $"Remote path{pathSuffix} was not found."),
            SshConnectionException => new RemoteError(
                RemoteErrorCode.NetworkInterrupted,
                $"The SSH connection was interrupted while trying to {action}{pathSuffix}.",
                exception.Message),
            SocketException or IOException or ObjectDisposedException => new RemoteError(
                RemoteErrorCode.NetworkInterrupted,
                $"The SFTP channel was interrupted while trying to {action}{pathSuffix}.",
                exception.Message),
            SshException when sshFailureCode is not null => new RemoteError(
                sshFailureCode.Value,
                $"The remote server could not {action}{pathSuffix} because the target state conflicts with the request.",
                exception.Message),
            _ => new RemoteError(
                RemoteErrorCode.CommandFailed,
                $"The remote server could not {action}{pathSuffix}.",
                $"{exception.GetType().Name}: {exception.Message}"),
        };
        return new RemoteFileSystemException(error, exception);
    }

    private static RemoteFileSystemException CreateError(
        RemoteErrorCode code,
        string message,
        Exception? innerException = null) =>
        new(
            new RemoteError(
                code,
                message,
                innerException is null ? null : $"{innerException.GetType().Name}: {innerException.Message}"),
            innerException);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
