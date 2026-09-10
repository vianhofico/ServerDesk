from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise SystemExit(f"Expected {label} was not found.")
    return text.replace(old, new, 1)


path = Path("src/ServerDesk.Infrastructure.Ssh/SshRemoteCommandExecutor.cs")
text = path.read_text(encoding="utf-8")

text = replace_once(
    text,
    """    private readonly SemaphoreSlim _gate = new(1, 1);
    private SshClientLease? _lease;
    private bool _disposed;""",
    """    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _lifecycleSync = new();
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly TaskCompletionSource _operationsDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SshClientLease? _lease;
    private int _activeOperations;
    private bool _disposed;
    private Task? _disposeTask;""",
    "command executor lifecycle fields",
)

text = replace_once(
    text,
    """        ArgumentNullException.ThrowIfNull(command);
        ThrowIfDisposed();
        ValidateCommand(command);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);""",
    """        ArgumentNullException.ThrowIfNull(command);
        using var operation = EnterOperation(cancellationToken);
        cancellationToken = operation.Token;
        ValidateCommand(command);
        cancellationToken.ThrowIfCancellationRequested();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);""",
    "ExecuteAsync admission block",
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
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            DisposeLease();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async ValueTask EnsureConnectedAsync""",
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
            DisposeLease();
        }
        finally
        {
            _gate.Dispose();
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
        private readonly SshRemoteCommandExecutor _owner;
        private readonly CancellationTokenSource _cancellation;
        private int _disposed;

        public OperationLease(
            SshRemoteCommandExecutor owner,
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

    private async ValueTask EnsureConnectedAsync""",
    "DisposeAsync and lifecycle helper insertion",
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


test_path = Path("tests/ServerDesk.Tests/SshRemoteCommandExecutorLifecycleTests.cs")
if test_path.exists():
    raise SystemExit("Lifecycle regression test already exists.")

test_path.write_text(
    """using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Tests;

public sealed class SshRemoteCommandExecutorLifecycleTests
{
    [Fact]
    public async Task DisposeAsyncCancelsAdmittedExecutionBeforeDisposingGate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForServerProfile(profileId);
        var profile = ServerProfile.Create(
            profileId,
            "Command lifecycle",
            "example.invalid",
            22,
            "operator",
            credentialReference: reference,
            authenticationKind: ServerAuthenticationKind.Password);
        var secrets = new BlockingSecretStore(reference);
        var factory = new SshRemoteCommandExecutorFactory(
            secrets,
            new UnexpectedHostTrustService(),
            new UnexpectedInteractivePrompt(),
            new SshSessionOptions(
                TimeSpan.FromSeconds(8),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(250)));
        var executor = factory.Create(profile);

        var executionTask = executor.ExecuteAsync(
            RemoteCommandSpec.ReadOnly("uname", "-s"),
            CancellationToken.None);
        await secrets.ReadStarted.Task.WaitAsync(cancellationToken);

        var disposeTask = executor.DisposeAsync().AsTask();
        var result = await executionTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.OperationCancelled, result.Error?.Code);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            executor.ExecuteAsync(
                RemoteCommandSpec.ReadOnly("uname", "-s"),
                cancellationToken));
        await executor.DisposeAsync();
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
