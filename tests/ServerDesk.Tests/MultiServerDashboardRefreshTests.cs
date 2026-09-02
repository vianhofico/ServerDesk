using System.Collections.Concurrent;
using ServerDesk.Application.Dashboard;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class MultiServerDashboardRefreshTests
{
    [Fact]
    public async Task DisconnectedTargetIsPublishedWithoutRemoteProbe()
    {
        var probeCount = 0;
        var dashboard = new DelegateDashboardService((profile, cancellationToken) =>
        {
            Interlocked.Increment(ref probeCount);
            return ValueTask.FromResult(CreateSnapshot(profile.Id));
        });
        var service = new MultiServerDashboardRefreshService(
            dashboard,
            MultiServerDashboardRefreshOptions.Default);
        var profile = CreateProfile("offline");
        var updates = new ConcurrentQueue<MultiServerDashboardUpdate>();

        await service.RefreshAsync(
            [new MultiServerDashboardTarget(profile, false)],
            update =>
            {
                updates.Enqueue(update);
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, probeCount);
        var update = Assert.Single(updates);
        Assert.Equal(profile.Id, update.ServerProfileId);
        Assert.Equal(MultiServerDashboardUpdateState.Disconnected, update.State);
    }

    [Fact]
    public async Task RefreshConcurrencyIsBounded()
    {
        var dashboard = new TrackingDashboardService(TimeSpan.FromMilliseconds(60));
        var service = new MultiServerDashboardRefreshService(
            dashboard,
            new MultiServerDashboardRefreshOptions(2));
        var targets = Enumerable.Range(1, 6)
            .Select(index => new MultiServerDashboardTarget(CreateProfile($"server-{index}"), true))
            .ToArray();
        var updates = new ConcurrentQueue<MultiServerDashboardUpdate>();

        await service.RefreshAsync(
            targets,
            update =>
            {
                updates.Enqueue(update);
                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.InRange(dashboard.MaxObservedConcurrency, 1, 2);
        Assert.Equal(6, updates.Count(update => update.State == MultiServerDashboardUpdateState.Available));
    }

    [Fact]
    public async Task FastServerPublishesBeforeSlowServerFinishes()
    {
        var slowProfile = CreateProfile("slow");
        var fastProfile = CreateProfile("fast");
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastPublished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dashboard = new DelegateDashboardService(async (profile, cancellationToken) =>
        {
            if (profile.Id == slowProfile.Id)
            {
                slowStarted.TrySetResult();
                await releaseSlow.Task.WaitAsync(cancellationToken);
            }

            return CreateSnapshot(profile.Id);
        });
        var service = new MultiServerDashboardRefreshService(
            dashboard,
            new MultiServerDashboardRefreshOptions(2));

        var refreshTask = service.RefreshAsync(
            [
                new MultiServerDashboardTarget(slowProfile, true),
                new MultiServerDashboardTarget(fastProfile, true),
            ],
            update =>
            {
                if (update.ServerProfileId == fastProfile.Id &&
                    update.State == MultiServerDashboardUpdateState.Available)
                {
                    fastPublished.TrySetResult();
                }

                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await fastPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.False(refreshTask.IsCompleted);
        releaseSlow.TrySetResult();
        await refreshTask;
    }

    [Fact]
    public async Task CancellationPublishesCancelledState()
    {
        using var cancellation = new CancellationTokenSource();
        var refreshing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new ConcurrentQueue<MultiServerDashboardUpdate>();
        var dashboard = new DelegateDashboardService(async (profile, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return CreateSnapshot(profile.Id);
        });
        var service = new MultiServerDashboardRefreshService(
            dashboard,
            MultiServerDashboardRefreshOptions.Default);
        var profile = CreateProfile("cancelled");

        var refreshTask = service.RefreshAsync(
            [new MultiServerDashboardTarget(profile, true)],
            update =>
            {
                updates.Enqueue(update);
                if (update.State == MultiServerDashboardUpdateState.Refreshing)
                {
                    refreshing.TrySetResult();
                }

                return ValueTask.CompletedTask;
            },
            cancellation.Token);

        await refreshing.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        await refreshTask;

        Assert.Contains(updates, update => update.State == MultiServerDashboardUpdateState.Cancelled);
        Assert.DoesNotContain(updates, update => update.State == MultiServerDashboardUpdateState.Available);
    }

    [Fact]
    public async Task UnexpectedFailureDoesNotLeakExceptionMessage()
    {
        const string secret = "credential-secret-must-not-leak";
        var dashboard = new DelegateDashboardService((profile, cancellationToken) =>
            ValueTask.FromException<ServerDashboardSnapshot>(new InvalidOperationException(secret)));
        var service = new MultiServerDashboardRefreshService(
            dashboard,
            MultiServerDashboardRefreshOptions.Default);
        var profile = CreateProfile("failure");
        MultiServerDashboardUpdate? failed = null;

        await service.RefreshAsync(
            [new MultiServerDashboardTarget(profile, true)],
            update =>
            {
                if (update.State == MultiServerDashboardUpdateState.Failed)
                {
                    failed = update;
                }

                return ValueTask.CompletedTask;
            },
            TestContext.Current.CancellationToken);

        Assert.NotNull(failed?.Error);
        Assert.DoesNotContain(secret, failed!.Error!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, failed.Error.TechnicalDetails ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(nameof(InvalidOperationException), failed.Error.TechnicalDetails);
    }

    private static ServerProfile CreateProfile(string name) =>
        ServerProfile.Create(name, $"{name}.example.test", 22, "serverdesk", "test");

    private static ServerDashboardSnapshot CreateSnapshot(Guid profileId) =>
        new(
            profileId,
            DateTimeOffset.UtcNow,
            DashboardSection<CpuMetrics>.Available(new CpuMetrics(12.5, 4)),
            DashboardSection<LoadMetrics>.Available(new LoadMetrics(0.2, 0.1, 0.05)),
            DashboardSection<UptimeMetrics>.Available(new UptimeMetrics(TimeSpan.FromHours(4))),
            DashboardSection<MemoryMetrics>.Available(new MemoryMetrics(100, 50, 50, 50, 0, 0, 0, null)),
            DashboardSection<NetworkMetrics>.Available(new NetworkMetrics(100, 200, 1, 2, [])),
            DashboardSection<IReadOnlyList<FileSystemMetrics>>.Available([]),
            []);

    private sealed class DelegateDashboardService : IServerDashboardService
    {
        private readonly Func<ServerProfile, CancellationToken, ValueTask<ServerDashboardSnapshot>> _handler;

        public DelegateDashboardService(
            Func<ServerProfile, CancellationToken, ValueTask<ServerDashboardSnapshot>> handler)
        {
            _handler = handler;
        }

        public ValueTask<ServerDashboardSnapshot> GetAsync(
            ServerProfile profile,
            CancellationToken cancellationToken = default) =>
            _handler(profile, cancellationToken);
    }

    private sealed class TrackingDashboardService : IServerDashboardService
    {
        private readonly TimeSpan _delay;
        private int _active;
        private int _maxObservedConcurrency;

        public TrackingDashboardService(TimeSpan delay)
        {
            _delay = delay;
        }

        public int MaxObservedConcurrency => Volatile.Read(ref _maxObservedConcurrency);

        public async ValueTask<ServerDashboardSnapshot> GetAsync(
            ServerProfile profile,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref _active);
            UpdateMaximum(active);
            try
            {
                await Task.Delay(_delay, cancellationToken);
                return CreateSnapshot(profile.Id);
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }

        private void UpdateMaximum(int active)
        {
            while (true)
            {
                var current = Volatile.Read(ref _maxObservedConcurrency);
                if (active <= current ||
                    Interlocked.CompareExchange(ref _maxObservedConcurrency, active, current) == current)
                {
                    return;
                }
            }
        }
    }
}
