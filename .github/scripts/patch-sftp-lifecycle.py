from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise SystemExit(f"Expected {label} was not found.")
    return text.replace(old, new, 1)


source_path = Path("src/ServerDesk.Infrastructure.Ssh/SftpRemoteFileSystem.cs")
text = source_path.read_text(encoding="utf-8")

text = replace_once(
    text,
    """    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private SftpClient? _client;
    private SftpConnectionResources? _connectionResources;
    private bool _disposed;""",
    """    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly object _lifecycleSync = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly TaskCompletionSource _operationsDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SftpClient? _client;
    private SftpConnectionResources? _connectionResources;
    private int _activeOperations;
    private bool _disposed;
    private Task? _disposeTask;""",
    "SFTP lifecycle field block",
)

text = replace_once(
    text,
    """    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();""",
    """    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterLifecycleOperation(cancellationToken);
        cancellationToken = operation.Token;
        cancellationToken.ThrowIfCancellationRequested();""",
    "ConnectAsync prefix",
)

text = replace_once(
    text,
    """    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();""",
    """    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterLifecycleOperation(cancellationToken);
        cancellationToken = operation.Token;
        cancellationToken.ThrowIfCancellationRequested();""",
    "DisconnectAsync prefix",
)

client_line = "        var client = GetConnectedClient();"
client_count = text.count(client_line)
if client_count != 9:
    raise SystemExit(f"Expected 9 SFTP client operation entries, found {client_count}.")
text = text.replace(
    client_line,
    "        using var operation = await EnterSerializedOperationAsync(cancellationToken).ConfigureAwait(false);\n"
    "        cancellationToken = operation.Token;\n"
    "        cancellationToken.ThrowIfCancellationRequested();\n"
    "        var client = GetConnectedClient();",
)

text = replace_once(
    text,
    """    public async ValueTask DisposeAsync()
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
""",
    """    public ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            if (_disposeTask is not null)
            {
                return new ValueTask(_disposeTask);
            }

            _disposed = true;
            var drainTask = _activeOperations == 0
                ? Task.CompletedTask
                : _operationsDrained.Task;
            _disposeTask = DisposeCoreAsync(drainTask);
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync(Task drainTask)
    {
        await _disposeCancellation.CancelAsync().ConfigureAwait(false);
        await drainTask.ConfigureAwait(false);
        try
        {
            DisposeConnection();
        }
        finally
        {
            _connectionGate.Dispose();
            _disposeCancellation.Dispose();
        }
    }
""",
    "DisposeAsync block",
)

text = replace_once(
    text,
    """    private SftpClient GetConnectedClient()
    {
        ThrowIfDisposed();""",
    """    private OperationLease EnterLifecycleOperation(CancellationToken cancellationToken)
    {
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                _disposeCancellation.Token);
            _activeOperations++;
            return new OperationLease(this, operationCancellation);
        }
    }

    private async ValueTask<OperationLease> EnterSerializedOperationAsync(CancellationToken cancellationToken)
    {
        var operation = EnterLifecycleOperation(cancellationToken);
        try
        {
            await _connectionGate.WaitAsync(operation.Token).ConfigureAwait(false);
            operation.MarkGateAcquired();
            return operation;
        }
        catch
        {
            operation.Dispose();
            throw;
        }
    }

    private void EndOperation()
    {
        lock (_lifecycleSync)
        {
            _activeOperations--;
            if (_disposed && _activeOperations == 0)
            {
                _operationsDrained.TrySetResult();
            }
        }
    }

    private sealed class OperationLease : IDisposable
    {
        private readonly SftpRemoteFileSystem _owner;
        private readonly CancellationTokenSource _cancellation;
        private int _gateAcquired;
        private int _disposed;

        public OperationLease(
            SftpRemoteFileSystem owner,
            CancellationTokenSource cancellation)
        {
            _owner = owner;
            _cancellation = cancellation;
        }

        public CancellationToken Token => _cancellation.Token;

        public void MarkGateAcquired() => Interlocked.Exchange(ref _gateAcquired, 1);

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            try
            {
                if (Volatile.Read(ref _gateAcquired) != 0)
                {
                    _owner._connectionGate.Release();
                }
            }
            finally
            {
                _cancellation.Dispose();
                _owner.EndOperation();
            }
        }
    }

    private SftpClient GetConnectedClient()
    {""",
    "GetConnectedClient insertion point",
)

text = replace_once(
    text,
    """    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
""",
    "",
    "ThrowIfDisposed helper",
)

source_path.write_text(text, encoding="utf-8")


test_path = Path("tests/ServerDesk.Ssh.IntegrationTests/SftpIntegrationTests.cs")
test_text = test_path.read_text(encoding="utf-8")

test_text = replace_once(
    test_text,
    """    [Fact]
    public async Task PermissionDeniedAndDisconnectedChannelsMapToTypedErrors()""",
    """    [Fact]
    public async Task DisposeCancelsActiveUploadBeforeTearingDownChannel()
    {
        const long totalBytes = 32L * 1024 * 1024;
        var cancellationToken = TestContext.Current.CancellationToken;
        var root = UniqueRoot();
        var file = root.Combine("dispose-during-upload.bin");
        var fileSystem = CreateFileSystem();
        await fileSystem.ConnectAsync(cancellationToken);
        await fileSystem.CreateDirectoryAsync(root, cancellationToken);

        try
        {
            await using var source = new PatternReadStream(totalBytes, TimeSpan.FromMilliseconds(15));
            var progress = new SignalingProgress();
            var uploadTask = fileSystem.UploadAsync(
                    source,
                    file,
                    totalBytes,
                    progress: progress,
                    cancellationToken: cancellationToken)
                .AsTask();

            await progress.Started.WaitAsync(TimeSpan.FromSeconds(4), cancellationToken);
            var disposeTask = fileSystem.DisposeAsync().AsTask();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => uploadTask);
            await disposeTask;

            Assert.False(fileSystem.IsConnected);
            await Assert.ThrowsAsync<ObjectDisposedException>(() =>
                fileSystem.ListAsync(root, cancellationToken).AsTask());
            await fileSystem.DisposeAsync();
        }
        finally
        {
            await fileSystem.DisposeAsync();
        }

        await using var verifier = CreateFileSystem();
        await verifier.ConnectAsync(cancellationToken);
        try
        {
            var listed = await verifier.ListAsync(root, cancellationToken);
            Assert.DoesNotContain(listed, entry => entry.Name == file.Name);
            Assert.DoesNotContain(
                listed,
                entry => entry.Name.StartsWith(".serverdesk-upload-", StringComparison.Ordinal));
        }
        finally
        {
            await TryDeleteDirectoryAsync(verifier, root, cancellationToken);
        }
    }

    [Fact]
    public async Task PermissionDeniedAndDisconnectedChannelsMapToTypedErrors()""",
    "SFTP integration test insertion point",
)

test_text = replace_once(
    test_text,
    """    private sealed class RecordingProgress : IProgress<RemoteTransferProgress>
    {""",
    """    private sealed class SignalingProgress : IProgress<RemoteTransferProgress>
    {
        private readonly TaskCompletionSource _started =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Started => _started.Task;

        public void Report(RemoteTransferProgress value)
        {
            if (value.BytesTransferred > 0)
            {
                _started.TrySetResult();
            }
        }
    }

    private sealed class RecordingProgress : IProgress<RemoteTransferProgress>
    {""",
    "SFTP progress helper insertion point",
)

test_path.write_text(test_text, encoding="utf-8")
