from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise SystemExit(f"Expected {label} was not found")
    return text.replace(old, new, 1)


source_path = Path("src/ServerDesk.Infrastructure.Ssh/SshRemoteSession.cs")
text = source_path.read_text(encoding="utf-8")

text = replace_once(
    text,
    """    private readonly SemaphoreSlim _stateGate = new(1, 1);\n    private SshClientLease? _lease;\n    private CancellationTokenSource? _monitorCancellation;\n    private Task? _monitorTask;\n    private bool _hasAttemptedConnection;\n    private bool _disposed;""",
    """    private readonly SemaphoreSlim _stateGate = new(1, 1);\n    private readonly object _lifecycleSync = new();\n    private readonly CancellationTokenSource _disposeCancellation = new();\n    private readonly TaskCompletionSource _operationsDrained =\n        new(TaskCreationOptions.RunContinuationsAsynchronously);\n    private SshClientLease? _lease;\n    private CancellationTokenSource? _monitorCancellation;\n    private Task? _monitorTask;\n    private int _activeOperations;\n    private bool _hasAttemptedConnection;\n    private volatile bool _disposed;\n    private Task? _disposeTask;""",
    "SSH session fields",
)

text = replace_once(
    text,
    """    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)\n    {\n        ThrowIfDisposed();\n        var gateEntered = false;""",
    """    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)\n    {\n        using var operation = EnterOperation(cancellationToken);\n        cancellationToken = operation.Token;\n        var gateEntered = false;""",
    "ConnectAsync prefix",
)

text = replace_once(
    text,
    """                await _lease.Client.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);\n                if (!_lease.Client.IsConnected)""",
    """                await _lease.Client.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);\n                cancellationToken.ThrowIfCancellationRequested();\n                if (!_lease.Client.IsConnected)""",
    "ConnectAsync post-connect cancellation point",
)

text = replace_once(
    text,
    """    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)\n    {\n        ThrowIfDisposed();\n        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);""",
    """    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)\n    {\n        using var operation = EnterOperation(cancellationToken);\n        cancellationToken = operation.Token;\n        cancellationToken.ThrowIfCancellationRequested();\n        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);""",
    "DisconnectAsync prefix",
)

old_dispose = """    public async ValueTask DisposeAsync()\n    {\n        if (_disposed)\n        {\n            return;\n        }\n\n        _disposed = true;\n        StopConnectionMonitor();\n        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);\n        try\n        {\n            DisposeCurrentLease();\n            State = RemoteSessionState.Disconnected;\n        }\n        finally\n        {\n            _stateGate.Release();\n            _stateGate.Dispose();\n        }\n    }\n"""
new_dispose = """    public ValueTask DisposeAsync()\n    {\n        lock (_lifecycleSync)\n        {\n            if (_disposeTask is not null)\n            {\n                return new ValueTask(_disposeTask);\n            }\n\n            _disposed = true;\n            StopConnectionMonitor();\n            var drainTask = _activeOperations == 0\n                ? Task.CompletedTask\n                : _operationsDrained.Task;\n            _disposeTask = DisposeCoreAsync(drainTask);\n            return new ValueTask(_disposeTask);\n        }\n    }\n\n    private async Task DisposeCoreAsync(Task drainTask)\n    {\n        await _disposeCancellation.CancelAsync().ConfigureAwait(false);\n        await drainTask.ConfigureAwait(false);\n        try\n        {\n            DisposeCurrentLease();\n            State = RemoteSessionState.Disconnected;\n        }\n        finally\n        {\n            _stateGate.Dispose();\n            _disposeCancellation.Dispose();\n        }\n    }\n"""
text = replace_once(text, old_dispose, new_dispose, "DisposeAsync block")

text = replace_once(
    text,
    """    private void ClientOnErrorOccurred(object? sender, ExceptionEventArgs eventArgs)\n    {\n        if (State != RemoteSessionState.Connected)""",
    """    private void ClientOnErrorOccurred(object? sender, ExceptionEventArgs eventArgs)\n    {\n        if (_disposed || State != RemoteSessionState.Connected)""",
    "client error disposal guard",
)

old_start_monitor = """    private void StartConnectionMonitor(SshClient client)\n    {\n        StopConnectionMonitor();\n        _monitorCancellation = new CancellationTokenSource();\n        _monitorTask = MonitorConnectionAsync(client, _monitorCancellation.Token);\n    }\n"""
new_start_monitor = """    private void StartConnectionMonitor(SshClient client)\n    {\n        lock (_lifecycleSync)\n        {\n            if (_disposed)\n            {\n                return;\n            }\n\n            StopConnectionMonitor();\n            _monitorCancellation = new CancellationTokenSource();\n            _monitorTask = MonitorConnectionAsync(client, _monitorCancellation.Token);\n        }\n    }\n"""
text = replace_once(text, old_start_monitor, new_start_monitor, "StartConnectionMonitor block")

text = replace_once(
    text,
    """                if (State != RemoteSessionState.Connected)\n                {\n                    return;\n                }\n\n                if (!client.IsConnected)\n                {\n                    LastError = new RemoteError(""",
    """                if (_disposed || State != RemoteSessionState.Connected)\n                {\n                    return;\n                }\n\n                if (!client.IsConnected)\n                {\n                    if (_disposed || cancellationToken.IsCancellationRequested)\n                    {\n                        return;\n                    }\n\n                    LastError = new RemoteError(""",
    "monitor disposal guard",
)

text = replace_once(
    text,
    """        catch (Exception exception)\n        {\n            if (State == RemoteSessionState.Connected)\n            {\n                LastError = SshRemoteErrorMapper.Map(""",
    """        catch (Exception exception)\n        {\n            if (!_disposed && State == RemoteSessionState.Connected)\n            {\n                LastError = SshRemoteErrorMapper.Map(""",
    "monitor exception disposal guard",
)

old_tail = """    private void Transition(RemoteSessionState state)\n    {\n        if (State == state)\n        {\n            return;\n        }\n\n        State = state;\n        StateChanged?.Invoke(state);\n    }\n\n    private static RemoteError CreateCancellationError() =>\n        new(RemoteErrorCode.OperationCancelled, \"SSH connection operation was cancelled.\");\n\n    private void ThrowIfDisposed()\n    {\n        ObjectDisposedException.ThrowIf(_disposed, this);\n    }\n"""
new_tail = """    private OperationLease EnterOperation(CancellationToken cancellationToken)\n    {\n        lock (_lifecycleSync)\n        {\n            ObjectDisposedException.ThrowIf(_disposed, this);\n            var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(\n                cancellationToken,\n                _disposeCancellation.Token);\n            _activeOperations++;\n            return new OperationLease(this, operationCancellation);\n        }\n    }\n\n    private void EndOperation()\n    {\n        lock (_lifecycleSync)\n        {\n            _activeOperations--;\n            if (_disposed && _activeOperations == 0)\n            {\n                _operationsDrained.TrySetResult();\n            }\n        }\n    }\n\n    private sealed class OperationLease : IDisposable\n    {\n        private readonly SshRemoteSession _owner;\n        private readonly CancellationTokenSource _cancellation;\n        private int _disposed;\n\n        public OperationLease(\n            SshRemoteSession owner,\n            CancellationTokenSource cancellation)\n        {\n            _owner = owner;\n            _cancellation = cancellation;\n        }\n\n        public CancellationToken Token => _cancellation.Token;\n\n        public void Dispose()\n        {\n            if (Interlocked.Exchange(ref _disposed, 1) != 0)\n            {\n                return;\n            }\n\n            _cancellation.Dispose();\n            _owner.EndOperation();\n        }\n    }\n\n    private void Transition(RemoteSessionState state)\n    {\n        if (State == state)\n        {\n            return;\n        }\n\n        State = state;\n        StateChanged?.Invoke(state);\n    }\n\n    private static RemoteError CreateCancellationError() =>\n        new(RemoteErrorCode.OperationCancelled, \"SSH connection operation was cancelled.\");\n"""
text = replace_once(text, old_tail, new_tail, "lifecycle helper insertion point")

source_path.write_text(text, encoding="utf-8")

test_path = Path("tests/ServerDesk.Tests/SshRemoteSessionLifecycleTests.cs")
if test_path.exists():
    raise SystemExit("Lifecycle test already exists")

test_path.write_text(
    r'''using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Tests;

public sealed class SshRemoteSessionLifecycleTests
{
    [Fact]
    public async Task DisposeAsyncCancelsAdmittedConnectBeforeDisposingStateGate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Control lifecycle",
            "example.invalid",
            22,
            "operator",
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var secrets = new BlockingSecretStore(reference);
        var factory = new SshRemoteSessionFactory(
            secrets,
            new UnexpectedHostTrustService(),
            new UnexpectedInteractivePrompt(),
            new SshSessionOptions(
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(250)));
        var session = factory.Create(profile);

        var connectTask = session.ConnectAsync(CancellationToken.None).AsTask();
        await secrets.ReadStarted.Task.WaitAsync(cancellationToken);

        var disposeTask = session.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<OperationCanceledException>(() => connectTask);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.Equal(RemoteSessionState.Disconnected, session.State);
        Assert.Equal(RemoteErrorCode.OperationCancelled, session.LastError?.Code);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.ConnectAsync(cancellationToken).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            session.DisconnectAsync(cancellationToken).AsTask());
        await session.DisposeAsync();
    }

    private sealed class BlockingSecretStore : ISecretStore
    {
        private readonly SecretReference _reference;

        public BlockingSecretStore(SecretReference reference)
        {
            _reference = reference;
        }

        public TaskCompletionSource ReadStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask SetAsync(
            SecretReference reference,
            string secret,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public async ValueTask<string?> GetAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(_reference, reference);
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }

        public ValueTask DeleteAsync(
            SecretReference reference,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnexpectedHostTrustService : IHostTrustService
    {
        public ValueTask<HostTrustVerification> VerifyAsync(
            HostKeyObservation observation,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The lifecycle test must cancel before host trust is reached.");
    }

    private sealed class UnexpectedInteractivePrompt : IInteractiveAuthenticationPrompt
    {
        public ValueTask<IReadOnlyList<string>?> PromptAsync(
            InteractiveAuthenticationChallenge challenge,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The lifecycle test must not request interactive authentication.");
    }
}
''',
    encoding="utf-8",
)
