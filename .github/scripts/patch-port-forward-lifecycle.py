from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise SystemExit(f"Expected {label} was not found.")
    return text.replace(old, new, 1)


path = Path("src/ServerDesk.Infrastructure.Ssh/SshPortForwardSession.cs")
text = path.read_text(encoding="utf-8")

text = replace_once(
    text,
    """    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private SshClientLease? _lease;
    private ForwardedPort? _forwardedPort;
    private bool _stopping;
    private bool _disposed;""",
    """    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly object _lifecycleSync = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly TaskCompletionSource _operationsDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SshClientLease? _lease;
    private ForwardedPort? _forwardedPort;
    private int _activeOperations;
    private bool _stopping;
    private bool _disposed;
    private Task? _disposeTask;""",
    "port-forward lifecycle field block",
)

text = replace_once(
    text,
    """    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);""",
    """    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(cancellationToken);
        cancellationToken = operation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);""",
    "StartAsync admission block",
)

text = replace_once(
    text,
    """    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);""",
    """    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(cancellationToken);
        cancellationToken = operation.Token;
        cancellationToken.ThrowIfCancellationRequested();
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);""",
    "StopAsync admission block",
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
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _stopping = true;
            DisposeConnection();
            BoundPort = 0;
            State = PortForwardSessionState.Stopped;
        }
        finally
        {
            _stopping = false;
            _stateGate.Release();
            _stateGate.Dispose();
        }
    }

    private void ForwardedPortOnException""",
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
            _stopping = true;
            DisposeConnection();
            BoundPort = 0;
            State = PortForwardSessionState.Stopped;
        }
        finally
        {
            _stopping = false;
            _stateGate.Dispose();
            _disposeCancellation.Dispose();
        }
    }

    private OperationLease EnterOperation(CancellationToken cancellationToken)
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
        private readonly SshPortForwardSession _owner;
        private readonly CancellationTokenSource _cancellation;
        private int _disposed;

        public OperationLease(
            SshPortForwardSession owner,
            CancellationTokenSource cancellation)
        {
            _owner = owner;
            _cancellation = cancellation;
        }

        public CancellationToken Token => _cancellation.Token;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            _cancellation.Dispose();
            _owner.EndOperation();
        }
    }

    private void ForwardedPortOnException""",
    "DisposeAsync and lifecycle helper block",
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

path.write_text(text, encoding="utf-8")


test_path = Path("tests/ServerDesk.Tests/SshPortForwardSessionLifecycleTests.cs")
if test_path.exists():
    raise SystemExit("Port-forward lifecycle regression test already exists.")

test_path.write_text(
    """using ServerDesk.Application.HostTrust;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Tests;

public sealed class SshPortForwardSessionLifecycleTests
{
    [Fact]
    public async Task DisposeAsyncCancelsAdmittedStartBeforeDisposingStateGate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Forward lifecycle",
            "example.invalid",
            22,
            "operator",
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var forwardProfile = PortForwardProfile.Create(
            profile.Id,
            "Lifecycle local forward",
            PortForwardKind.Local,
            "127.0.0.1",
            0,
            "127.0.0.1",
            8080);
        var secrets = new BlockingSecretStore(reference);
        var factory = new SshPortForwardSessionFactory(
            secrets,
            new UnexpectedHostTrustService(),
            new UnexpectedInteractivePrompt(),
            new SshSessionOptions(
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(250)));
        var forward = factory.Create(profile, forwardProfile);

        var startTask = forward.StartAsync(CancellationToken.None).AsTask();
        await secrets.ReadStarted.Task.WaitAsync(cancellationToken);

        var disposeTask = forward.DisposeAsync().AsTask();

        await Assert.ThrowsAsync<OperationCanceledException>(() => startTask);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.Equal(PortForwardSessionState.Stopped, forward.State);
        Assert.Equal(0, forward.BoundPort);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            forward.StopAsync(cancellationToken).AsTask());
        await forward.DisposeAsync();
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
""",
    encoding="utf-8",
)
