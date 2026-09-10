namespace ServerDesk.App;

/// <summary>
/// Coordinates short-lived asynchronous operations with owner teardown.
/// Once draining starts, no new operation can be admitted and the drain task
/// completes only after every previously admitted lease has been released.
/// </summary>
internal sealed class AsyncOperationDrain
{
    private readonly object _sync = new();
    private TaskCompletionSource? _drained;
    private int _activeOperations;
    private bool _stopping;

    public bool IsStopping
    {
        get
        {
            lock (_sync)
            {
                return _stopping;
            }
        }
    }

    public OperationLease EnterOrThrow(object owner)
    {
        if (TryEnter(out var lease))
        {
            return lease;
        }

        throw new ObjectDisposedException(owner.GetType().FullName);
    }

    public bool TryEnter(out OperationLease lease)
    {
        lock (_sync)
        {
            if (_stopping)
            {
                lease = default;
                return false;
            }

            _activeOperations++;
            lease = new OperationLease(this);
            return true;
        }
    }

    public bool TryRunWhileAccepting(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        lock (_sync)
        {
            if (_stopping)
            {
                return false;
            }

            action();
            return true;
        }
    }

    public Task StopAndDrainAsync()
    {
        lock (_sync)
        {
            _stopping = true;
            if (_activeOperations == 0)
            {
                return Task.CompletedTask;
            }

            _drained ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return _drained.Task;
        }
    }

    private void Exit()
    {
        TaskCompletionSource? drained = null;
        lock (_sync)
        {
            if (_activeOperations <= 0)
            {
                return;
            }

            _activeOperations--;
            if (_stopping && _activeOperations == 0)
            {
                drained = _drained;
            }
        }

        drained?.TrySetResult();
    }

    internal struct OperationLease : IDisposable
    {
        private AsyncOperationDrain? _owner;

        internal OperationLease(AsyncOperationDrain owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _owner, null)?.Exit();
        }
    }
}
