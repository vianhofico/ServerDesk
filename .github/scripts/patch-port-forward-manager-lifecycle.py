from pathlib import Path


def replace_once(text: str, old: str, new: str, label: str) -> str:
    if old not in text:
        raise SystemExit(f"Expected {label} was not found")
    return text.replace(old, new, 1)


source_path = Path("src/ServerDesk.Application/PortForwarding/IPortForwarding.cs")
text = source_path.read_text(encoding="utf-8")

text = replace_once(
    text,
    """    private readonly ConcurrentDictionary<Guid, ActiveForward> _activeForwards = new();\n    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);\n    private bool _disposed;""",
    """    private readonly ConcurrentDictionary<Guid, ActiveForward> _activeForwards = new();\n    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);\n    private readonly object _lifecycleSync = new();\n    private readonly TaskCompletionSource _operationsDrained =\n        new(TaskCreationOptions.RunContinuationsAsynchronously);\n    private int _activeOperations;\n    private bool _disposed;\n    private Task? _disposeTask;""",
    "manager lifecycle fields",
)

text = replace_once(
    text,
    """    public ValueTask<IReadOnlyList<PortForwardProfile>> ListProfilesAsync(\n        Guid serverProfileId,\n        CancellationToken cancellationToken = default)\n    {\n        ThrowIfDisposed();\n        return _forwardRepository.ListForServerAsync(serverProfileId, cancellationToken);\n    }""",
    """    public async ValueTask<IReadOnlyList<PortForwardProfile>> ListProfilesAsync(\n        Guid serverProfileId,\n        CancellationToken cancellationToken = default)\n    {\n        using var operation = EnterOperation(cancellationToken);\n        return await _forwardRepository.ListForServerAsync(serverProfileId, cancellationToken).ConfigureAwait(false);\n    }""",
    "ListProfilesAsync block",
)

text = replace_once(
    text,
    """    public async ValueTask SaveProfileAsync(\n        PortForwardProfile profile,\n        CancellationToken cancellationToken = default)\n    {\n        ThrowIfDisposed();\n        ArgumentNullException.ThrowIfNull(profile);""",
    """    public async ValueTask SaveProfileAsync(\n        PortForwardProfile profile,\n        CancellationToken cancellationToken = default)\n    {\n        ArgumentNullException.ThrowIfNull(profile);\n        using var operation = EnterOperation(cancellationToken);""",
    "SaveProfileAsync prefix",
)

old_delete = """    public async ValueTask DeleteProfileAsync(\n        Guid profileId,\n        CancellationToken cancellationToken = default)\n    {\n        ThrowIfDisposed();\n        await StopAsync(profileId, cancellationToken).ConfigureAwait(false);\n        await _forwardRepository.DeleteAsync(profileId, cancellationToken).ConfigureAwait(false);\n        Changed?.Invoke(profileId);\n    }"""
new_delete = """    public async ValueTask DeleteProfileAsync(\n        Guid profileId,\n        CancellationToken cancellationToken = default)\n    {\n        using var operation = EnterOperation(cancellationToken);\n        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);\n        try\n        {\n            await StopCoreAsync(profileId, cancellationToken).ConfigureAwait(false);\n            await _forwardRepository.DeleteAsync(profileId, cancellationToken).ConfigureAwait(false);\n            Changed?.Invoke(profileId);\n        }\n        finally\n        {\n            _lifecycleGate.Release();\n        }\n    }"""
text = replace_once(text, old_delete, new_delete, "DeleteProfileAsync block")

text = replace_once(
    text,
    """    public bool TryGetRuntimeSnapshot(Guid profileId, out PortForwardRuntimeSnapshot snapshot)\n    {\n        ThrowIfDisposed();""",
    """    public bool TryGetRuntimeSnapshot(Guid profileId, out PortForwardRuntimeSnapshot snapshot)\n    {\n        using var operation = EnterOperation(CancellationToken.None);""",
    "TryGetRuntimeSnapshot prefix",
)

text = replace_once(
    text,
    """    public async ValueTask<PortForwardRuntimeSnapshot> StartAsync(\n        Guid profileId,\n        CancellationToken cancellationToken = default)\n    {\n        ThrowIfDisposed();\n        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);""",
    """    public async ValueTask<PortForwardRuntimeSnapshot> StartAsync(\n        Guid profileId,\n        CancellationToken cancellationToken = default)\n    {\n        using var operation = EnterOperation(cancellationToken);\n        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);""",
    "StartAsync prefix",
)

old_stop = """    public async ValueTask StopAsync(\n        Guid profileId,\n        CancellationToken cancellationToken = default)\n    {\n        ThrowIfDisposed();\n        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);\n        try\n        {\n            if (!_activeForwards.TryGetValue(profileId, out var active))\n            {\n                return;\n            }\n\n            try\n            {\n                await active.Session.StopAsync(cancellationToken).ConfigureAwait(false);\n            }\n            finally\n            {\n                _activeForwards.TryRemove(profileId, out _);\n                active.Session.StateChanged -= active.StateHandler;\n                await active.Session.DisposeAsync().ConfigureAwait(false);\n                Changed?.Invoke(profileId);\n            }\n        }\n        finally\n        {\n            _lifecycleGate.Release();\n        }\n    }"""
new_stop = """    public async ValueTask StopAsync(\n        Guid profileId,\n        CancellationToken cancellationToken = default)\n    {\n        using var operation = EnterOperation(cancellationToken);\n        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);\n        try\n        {\n            await StopCoreAsync(profileId, cancellationToken).ConfigureAwait(false);\n        }\n        finally\n        {\n            _lifecycleGate.Release();\n        }\n    }\n\n    private async ValueTask StopCoreAsync(\n        Guid profileId,\n        CancellationToken cancellationToken)\n    {\n        if (!_activeForwards.TryGetValue(profileId, out var active))\n        {\n            return;\n        }\n\n        try\n        {\n            await active.Session.StopAsync(cancellationToken).ConfigureAwait(false);\n        }\n        finally\n        {\n            _activeForwards.TryRemove(profileId, out _);\n            active.Session.StateChanged -= active.StateHandler;\n            await active.Session.DisposeAsync().ConfigureAwait(false);\n            Changed?.Invoke(profileId);\n        }\n    }"""
text = replace_once(text, old_stop, new_stop, "StopAsync block")

old_dispose = """    public async ValueTask DisposeAsync()\n    {\n        if (_disposed)\n        {\n            return;\n        }\n\n        _disposed = true;\n        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);\n        try\n        {\n            var active = _activeForwards.Values.ToArray();\n            _activeForwards.Clear();\n            foreach (var forward in active)\n            {\n                forward.Session.StateChanged -= forward.StateHandler;\n                try\n                {\n                    await forward.Session.DisposeAsync().ConfigureAwait(false);\n                }\n                catch\n                {\n                    // App shutdown must continue; disposing the SSH client closes the local socket best-effort.\n                }\n            }\n        }\n        finally\n        {\n            _lifecycleGate.Release();\n            _lifecycleGate.Dispose();\n        }\n    }"""
new_dispose = """    public ValueTask DisposeAsync()\n    {\n        lock (_lifecycleSync)\n        {\n            if (_disposeTask is not null)\n            {\n                return new ValueTask(_disposeTask);\n            }\n\n            _disposed = true;\n            var drainTask = _activeOperations == 0\n                ? Task.CompletedTask\n                : _operationsDrained.Task;\n            _disposeTask = DisposeCoreAsync(drainTask);\n            return new ValueTask(_disposeTask);\n        }\n    }\n\n    private async Task DisposeCoreAsync(Task drainTask)\n    {\n        await drainTask.ConfigureAwait(false);\n        try\n        {\n            var active = _activeForwards.Values.ToArray();\n            _activeForwards.Clear();\n            foreach (var forward in active)\n            {\n                forward.Session.StateChanged -= forward.StateHandler;\n                try\n                {\n                    await forward.Session.DisposeAsync().ConfigureAwait(false);\n                }\n                catch\n                {\n                    // App shutdown must continue; disposing the SSH client closes the local socket best-effort.\n                }\n            }\n        }\n        finally\n        {\n            _lifecycleGate.Dispose();\n        }\n    }"""
text = replace_once(text, old_dispose, new_dispose, "DisposeAsync block")

old_tail = """    private static PortForwardRuntimeSnapshot ToSnapshot(IPortForwardSession session) =>\n        new(session.ForwardProfileId, session.State, session.BoundPort, session.LastError);\n\n    private static PortForwardSessionException CreateException(RemoteErrorCode code, string message) =>\n        new(new RemoteError(code, message));\n\n    private void ThrowIfDisposed()\n    {\n        ObjectDisposedException.ThrowIf(_disposed, this);\n    }\n\n    private sealed record ActiveForward("""
new_tail = """    private OperationLease EnterOperation(CancellationToken cancellationToken)\n    {\n        cancellationToken.ThrowIfCancellationRequested();\n        lock (_lifecycleSync)\n        {\n            ObjectDisposedException.ThrowIf(_disposed, this);\n            _activeOperations++;\n            return new OperationLease(this);\n        }\n    }\n\n    private void EndOperation()\n    {\n        lock (_lifecycleSync)\n        {\n            _activeOperations--;\n            if (_disposed && _activeOperations == 0)\n            {\n                _operationsDrained.TrySetResult();\n            }\n        }\n    }\n\n    private sealed class OperationLease : IDisposable\n    {\n        private readonly PortForwardManager _owner;\n        private int _disposed;\n\n        public OperationLease(PortForwardManager owner)\n        {\n            _owner = owner;\n        }\n\n        public void Dispose()\n        {\n            if (Interlocked.Exchange(ref _disposed, 1) == 0)\n            {\n                _owner.EndOperation();\n            }\n        }\n    }\n\n    private static PortForwardRuntimeSnapshot ToSnapshot(IPortForwardSession session) =>\n        new(session.ForwardProfileId, session.State, session.BoundPort, session.LastError);\n\n    private static PortForwardSessionException CreateException(RemoteErrorCode code, string message) =>\n        new(new RemoteError(code, message));\n\n    private sealed record ActiveForward("""
text = replace_once(text, old_tail, new_tail, "manager lifecycle helper insertion point")

source_path.write_text(text, encoding="utf-8")


test_path = Path("tests/ServerDesk.Tests/PortForwardManagerLifecycleTests.cs")
if test_path.exists():
    raise SystemExit("PortForwardManager lifecycle test already exists")

test_path.write_text(
    r'''using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Profiles;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class PortForwardManagerLifecycleTests
{
    [Fact]
    public async Task DisposeDrainsOperationQueuedBehindLifecycleGate()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = ServerProfile.Create("Lifecycle server", "127.0.0.1", 22, "operator");
        var repository = new FirstListBlockingForwardRepository();
        var servers = new MemoryProfileRepository(server);
        var manager = new PortForwardManager(repository, servers, new UnusedSessionFactory());
        var first = PortForwardProfile.Create(
            server.Id,
            "First",
            PortForwardKind.Local,
            "127.0.0.1",
            0,
            "127.0.0.1",
            5432);
        var second = PortForwardProfile.Create(
            server.Id,
            "Second",
            PortForwardKind.Dynamic,
            "127.0.0.1",
            0);

        var firstSave = manager.SaveProfileAsync(first, cancellationToken).AsTask();
        await repository.FirstListStarted.Task.WaitAsync(cancellationToken);
        var secondSave = manager.SaveProfileAsync(second, cancellationToken).AsTask();

        var disposeTask = manager.DisposeAsync().AsTask();
        Assert.False(disposeTask.IsCompleted);

        repository.ReleaseFirstList.TrySetResult();
        await Task.WhenAll(firstSave, secondSave).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.Equal(2, repository.Count);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            manager.SaveProfileAsync(first, cancellationToken).AsTask());
        await manager.DisposeAsync();
    }

    [Fact]
    public async Task DeleteProfileRemainsAdmittedAcrossStopAndRepositoryDelete()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var server = ServerProfile.Create("Delete lifecycle server", "127.0.0.1", 22, "operator");
        var forward = PortForwardProfile.Create(
            server.Id,
            "Delete lifecycle",
            PortForwardKind.Local,
            "127.0.0.1",
            0,
            "127.0.0.1",
            5432);
        var repository = new BlockingDeleteForwardRepository(forward);
        var session = new BlockingStopSession(forward.Id);
        var manager = new PortForwardManager(
            repository,
            new MemoryProfileRepository(server),
            new SingleSessionFactory(session));

        await manager.StartAsync(forward.Id, cancellationToken);
        var deleteTask = manager.DeleteProfileAsync(forward.Id, cancellationToken).AsTask();
        await session.StopStarted.Task.WaitAsync(cancellationToken);

        var firstDispose = manager.DisposeAsync().AsTask();
        var secondDispose = manager.DisposeAsync().AsTask();
        Assert.False(firstDispose.IsCompleted);
        Assert.False(secondDispose.IsCompleted);

        session.ReleaseStop.TrySetResult();
        await repository.DeleteStarted.Task.WaitAsync(cancellationToken);
        Assert.False(firstDispose.IsCompleted);

        repository.ReleaseDelete.TrySetResult();
        await deleteTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
        await Task.WhenAll(firstDispose, secondDispose).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);

        Assert.False(repository.Contains(forward.Id));
        Assert.Equal(1, session.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            manager.StartAsync(forward.Id, cancellationToken).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            manager.StopAsync(forward.Id, cancellationToken).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            manager.DeleteProfileAsync(forward.Id, cancellationToken).AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            manager.ListProfilesAsync(server.Id, cancellationToken).AsTask());
        Assert.Throws<ObjectDisposedException>(() =>
            manager.TryGetRuntimeSnapshot(forward.Id, out _));
        await manager.DisposeAsync();
    }

    private sealed class FirstListBlockingForwardRepository : IPortForwardProfileRepository
    {
        private readonly Dictionary<Guid, PortForwardProfile> _profiles = [];
        private int _listCalls;

        public TaskCompletionSource FirstListStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstList { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Count => _profiles.Count;

        public ValueTask<IReadOnlyList<PortForwardProfile>> ListForServerAsync(
            Guid serverProfileId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<PortForwardProfile>>(
                _profiles.Values.Where(profile => profile.ServerProfileId == serverProfileId).ToArray());

        public async ValueTask<IReadOnlyList<PortForwardProfile>> ListAsync(
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _listCalls) == 1)
            {
                FirstListStarted.TrySetResult();
                await ReleaseFirstList.Task.WaitAsync(cancellationToken);
            }

            return _profiles.Values.ToArray();
        }

        public ValueTask<PortForwardProfile?> GetAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_profiles.GetValueOrDefault(id));

        public ValueTask UpsertAsync(
            PortForwardProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles[profile.Id] = profile;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles.Remove(id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingDeleteForwardRepository : IPortForwardProfileRepository
    {
        private readonly Dictionary<Guid, PortForwardProfile> _profiles;

        public BlockingDeleteForwardRepository(PortForwardProfile profile)
        {
            _profiles = new Dictionary<Guid, PortForwardProfile> { [profile.Id] = profile };
        }

        public TaskCompletionSource DeleteStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseDelete { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Contains(Guid id) => _profiles.ContainsKey(id);

        public ValueTask<IReadOnlyList<PortForwardProfile>> ListAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<PortForwardProfile>>(_profiles.Values.ToArray());

        public ValueTask<IReadOnlyList<PortForwardProfile>> ListForServerAsync(
            Guid serverProfileId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<PortForwardProfile>>(
                _profiles.Values.Where(profile => profile.ServerProfileId == serverProfileId).ToArray());

        public ValueTask<PortForwardProfile?> GetAsync(
            Guid id,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_profiles.GetValueOrDefault(id));

        public ValueTask UpsertAsync(
            PortForwardProfile profile,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles[profile.Id] = profile;
            return ValueTask.CompletedTask;
        }

        public async ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            DeleteStarted.TrySetResult();
            await ReleaseDelete.Task.WaitAsync(cancellationToken);
            _profiles.Remove(id);
        }
    }

    private sealed class MemoryProfileRepository : IProfileRepository
    {
        private readonly Dictionary<Guid, ServerProfile> _profiles;

        public MemoryProfileRepository(params ServerProfile[] profiles)
        {
            _profiles = profiles.ToDictionary(profile => profile.Id);
        }

        public ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ServerProfile>>(_profiles.Values.ToArray());

        public ValueTask<ServerProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(_profiles.GetValueOrDefault(id));
        }

        public ValueTask UpsertAsync(ServerProfile profile, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles[profile.Id] = profile;
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profiles.Remove(id);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class UnusedSessionFactory : IPortForwardSessionFactory
    {
        public IPortForwardSession Create(ServerProfile serverProfile, PortForwardProfile forwardProfile) =>
            throw new InvalidOperationException("This lifecycle test must not create a forwarding session.");
    }

    private sealed class SingleSessionFactory : IPortForwardSessionFactory
    {
        private readonly IPortForwardSession _session;

        public SingleSessionFactory(IPortForwardSession session)
        {
            _session = session;
        }

        public IPortForwardSession Create(ServerProfile serverProfile, PortForwardProfile forwardProfile) => _session;
    }

    private sealed class BlockingStopSession : IPortForwardSession
    {
        public BlockingStopSession(Guid forwardProfileId)
        {
            ForwardProfileId = forwardProfileId;
        }

        public Guid ForwardProfileId { get; }
        public PortForwardSessionState State { get; private set; } = PortForwardSessionState.Created;
        public int BoundPort { get; private set; }
        public ServerDesk.Domain.Errors.RemoteError? LastError => null;
        public int DisposeCount { get; private set; }

        public TaskCompletionSource StopStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseStop { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public event Action<PortForwardSessionState>? StateChanged;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = PortForwardSessionState.Active;
            BoundPort = 15432;
            StateChanged?.Invoke(State);
            return ValueTask.CompletedTask;
        }

        public async ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            State = PortForwardSessionState.Stopping;
            StateChanged?.Invoke(State);
            StopStarted.TrySetResult();
            await ReleaseStop.Task.WaitAsync(cancellationToken);
            State = PortForwardSessionState.Stopped;
            BoundPort = 0;
            StateChanged?.Invoke(State);
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            State = PortForwardSessionState.Stopped;
            BoundPort = 0;
            return ValueTask.CompletedTask;
        }
    }
}
''',
    encoding="utf-8",
)
