using System.Collections.Concurrent;
using ServerDesk.Application.Profiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.PortForwarding;

public interface IPortForwardProfileRepository
{
    ValueTask<IReadOnlyList<PortForwardProfile>> ListAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<PortForwardProfile>> ListForServerAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default);

    ValueTask<PortForwardProfile?> GetAsync(
        Guid id,
        CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(
        PortForwardProfile profile,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        Guid id,
        CancellationToken cancellationToken = default);
}

public enum PortForwardSessionState
{
    Created,
    Starting,
    Active,
    Stopping,
    Stopped,
    Faulted,
}

public sealed class PortForwardSessionException : Exception
{
    public PortForwardSessionException(RemoteError error, Exception? innerException = null)
        : base(error?.Message, innerException)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public RemoteError Error { get; }
}

public interface IPortForwardSession : IAsyncDisposable
{
    Guid ForwardProfileId { get; }

    PortForwardSessionState State { get; }

    int BoundPort { get; }

    RemoteError? LastError { get; }

    event Action<PortForwardSessionState>? StateChanged;

    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);
}

public interface IPortForwardSessionFactory
{
    IPortForwardSession Create(ServerProfile serverProfile, PortForwardProfile forwardProfile);
}

public sealed record PortForwardRuntimeSnapshot(
    Guid ForwardProfileId,
    PortForwardSessionState State,
    int BoundPort,
    RemoteError? LastError);

public sealed class PortForwardManager : IAsyncDisposable
{
    private readonly IPortForwardProfileRepository _forwardRepository;
    private readonly IProfileRepository _serverRepository;
    private readonly IPortForwardSessionFactory _sessionFactory;
    private readonly ConcurrentDictionary<Guid, ActiveForward> _activeForwards = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _lifecycleSync = new();
    private readonly TaskCompletionSource _operationsDrained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeOperations;
    private bool _disposed;
    private Task? _disposeTask;

    public PortForwardManager(
        IPortForwardProfileRepository forwardRepository,
        IProfileRepository serverRepository,
        IPortForwardSessionFactory sessionFactory)
    {
        _forwardRepository = forwardRepository ?? throw new ArgumentNullException(nameof(forwardRepository));
        _serverRepository = serverRepository ?? throw new ArgumentNullException(nameof(serverRepository));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    public event Action<Guid>? Changed;

    public async ValueTask<IReadOnlyList<PortForwardProfile>> ListProfilesAsync(
        Guid serverProfileId,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(cancellationToken);
        return await _forwardRepository.ListForServerAsync(serverProfileId, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask SaveProfileAsync(
        PortForwardProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        using var operation = EnterOperation(cancellationToken);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_activeForwards.TryGetValue(profile.Id, out var active) &&
                active.Session.State is PortForwardSessionState.Starting or PortForwardSessionState.Active or PortForwardSessionState.Stopping)
            {
                throw CreateException(
                    RemoteErrorCode.AmbiguousState,
                    "Stop this tunnel before changing its saved configuration.");
            }

            var server = await _serverRepository.GetAsync(profile.ServerProfileId, cancellationToken).ConfigureAwait(false);
            if (server is null)
            {
                throw CreateException(
                    RemoteErrorCode.InvalidEndpoint,
                    "The selected server profile no longer exists.");
            }

            var savedProfiles = await _forwardRepository.ListAsync(cancellationToken).ConfigureAwait(false);
            var duplicate = savedProfiles.FirstOrDefault(candidate =>
                candidate.Id != profile.Id && profile.ConflictsWith(candidate));
            if (duplicate is not null)
            {
                throw CreateException(
                    RemoteErrorCode.PortInUse,
                    $"Tunnel '{profile.Name}' conflicts with saved tunnel '{duplicate.Name}' on {profile.BindHost}:{profile.BindPort}.");
            }

            await _forwardRepository.UpsertAsync(profile, cancellationToken).ConfigureAwait(false);
            Changed?.Invoke(profile.Id);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DeleteProfileAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(cancellationToken);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(profileId, cancellationToken).ConfigureAwait(false);
            await _forwardRepository.DeleteAsync(profileId, cancellationToken).ConfigureAwait(false);
            Changed?.Invoke(profileId);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public bool TryGetRuntimeSnapshot(Guid profileId, out PortForwardRuntimeSnapshot snapshot)
    {
        using var operation = EnterOperation(CancellationToken.None);
        if (_activeForwards.TryGetValue(profileId, out var active))
        {
            snapshot = ToSnapshot(active.Session);
            return true;
        }

        snapshot = default!;
        return false;
    }

    public async ValueTask<PortForwardRuntimeSnapshot> StartAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(cancellationToken);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        ActiveForward? active = null;
        try
        {
            var profile = await _forwardRepository.GetAsync(profileId, cancellationToken).ConfigureAwait(false)
                ?? throw CreateException(
                    RemoteErrorCode.InvalidEndpoint,
                    "The saved port-forward profile no longer exists.");
            var server = await _serverRepository.GetAsync(profile.ServerProfileId, cancellationToken).ConfigureAwait(false)
                ?? throw CreateException(
                    RemoteErrorCode.InvalidEndpoint,
                    "The server profile for this port forward no longer exists.");

            if (_activeForwards.TryGetValue(profileId, out var existing))
            {
                if (existing.Session.State is PortForwardSessionState.Active or PortForwardSessionState.Starting)
                {
                    return ToSnapshot(existing.Session);
                }

                _activeForwards.TryRemove(profileId, out _);
                existing.Session.StateChanged -= existing.StateHandler;
                await existing.Session.DisposeAsync().ConfigureAwait(false);
            }

            var conflict = _activeForwards.Values.FirstOrDefault(candidate =>
                candidate.Session.State is not PortForwardSessionState.Stopped and not PortForwardSessionState.Faulted &&
                profile.ConflictsWith(candidate.Profile));
            if (conflict is not null)
            {
                throw CreateException(
                    RemoteErrorCode.PortInUse,
                    $"Port forward '{profile.Name}' conflicts with active forward '{conflict.Profile.Name}' on {profile.BindHost}:{profile.BindPort}.");
            }

            var session = _sessionFactory.Create(server, profile);
            Action<PortForwardSessionState> stateHandler = _ => Changed?.Invoke(profile.Id);
            session.StateChanged += stateHandler;
            active = new ActiveForward(profile, session, stateHandler);
            if (!_activeForwards.TryAdd(profile.Id, active))
            {
                session.StateChanged -= stateHandler;
                await session.DisposeAsync().ConfigureAwait(false);
                throw CreateException(
                    RemoteErrorCode.AmbiguousState,
                    "Another operation changed this port forward while it was starting.");
            }

            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            Changed?.Invoke(profile.Id);
            return ToSnapshot(session);
        }
        catch
        {
            if (active is not null && active.Session.State != PortForwardSessionState.Faulted)
            {
                _activeForwards.TryRemove(active.Profile.Id, out _);
                active.Session.StateChanged -= active.StateHandler;
                await active.Session.DisposeAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        using var operation = EnterOperation(cancellationToken);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(profileId, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async ValueTask StopCoreAsync(
        Guid profileId,
        CancellationToken cancellationToken)
    {
        if (!_activeForwards.TryGetValue(profileId, out var active))
        {
            return;
        }

        try
        {
            await active.Session.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _activeForwards.TryRemove(profileId, out _);
            active.Session.StateChanged -= active.StateHandler;
            await active.Session.DisposeAsync().ConfigureAwait(false);
            Changed?.Invoke(profileId);
        }
    }

    public ValueTask DisposeAsync()
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
        await drainTask.ConfigureAwait(false);
        try
        {
            var active = _activeForwards.Values.ToArray();
            _activeForwards.Clear();
            foreach (var forward in active)
            {
                forward.Session.StateChanged -= forward.StateHandler;
                try
                {
                    await forward.Session.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // App shutdown must continue; disposing the SSH client closes the local socket best-effort.
                }
            }
        }
        finally
        {
            _lifecycleGate.Dispose();
        }
    }

    private OperationLease EnterOperation(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lifecycleSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _activeOperations++;
            return new OperationLease(this);
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
        private readonly PortForwardManager _owner;
        private int _disposed;

        public OperationLease(PortForwardManager owner)
        {
            _owner = owner;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                _owner.EndOperation();
            }
        }
    }

    private static PortForwardRuntimeSnapshot ToSnapshot(IPortForwardSession session) =>
        new(session.ForwardProfileId, session.State, session.BoundPort, session.LastError);

    private static PortForwardSessionException CreateException(RemoteErrorCode code, string message) =>
        new(new RemoteError(code, message));

    private sealed record ActiveForward(
        PortForwardProfile Profile,
        IPortForwardSession Session,
        Action<PortForwardSessionState> StateHandler);
}
