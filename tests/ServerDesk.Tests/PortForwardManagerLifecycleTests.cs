using ServerDesk.Application.PortForwarding;
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
