using ServerDesk.Application.Dashboard;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class MultiServerDashboardSnapshotCacheTests
{
    [Fact]
    public async Task SuccessfulRefreshCachesSnapshotAndDisconnectedRefreshClearsIt()
    {
        var profile = ServerProfile.Create("server", "host.test", 22, "ops");
        var snapshot = CreateSnapshot(profile);
        var service = new MultiServerDashboardRefreshService(
            new StubDashboardService(snapshot),
            MultiServerDashboardRefreshOptions.Default);

        await service.RefreshAsync(
            [new MultiServerDashboardTarget(profile, true)],
            _ => ValueTask.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.True(service.TryGetCachedSnapshot(profile.Id, out var cached));
        Assert.Same(snapshot, cached);

        await service.RefreshAsync(
            [new MultiServerDashboardTarget(profile, false)],
            _ => ValueTask.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.False(service.TryGetCachedSnapshot(profile.Id, out _));
    }

    [Fact]
    public async Task CancellationAfterAvailablePublishDoesNotLeaveSnapshotCached()
    {
        var profile = ServerProfile.Create("server", "host.test", 22, "ops");
        var snapshot = CreateSnapshot(profile);
        var service = new MultiServerDashboardRefreshService(
            new StubDashboardService(snapshot),
            MultiServerDashboardRefreshOptions.Default);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken);
        var updates = new List<MultiServerDashboardUpdateState>();

        await service.RefreshAsync(
            [new MultiServerDashboardTarget(profile, true)],
            update =>
            {
                updates.Add(update.State);
                if (update.State == MultiServerDashboardUpdateState.Available)
                {
                    cancellation.Cancel();
                }

                return ValueTask.CompletedTask;
            },
            cancellation.Token);

        Assert.Contains(MultiServerDashboardUpdateState.Available, updates);
        Assert.Contains(MultiServerDashboardUpdateState.Cancelled, updates);
        Assert.False(service.TryGetCachedSnapshot(profile.Id, out _));
    }

    private static ServerDashboardSnapshot CreateSnapshot(ServerProfile profile) =>
        new(
            profile.Id,
            DateTimeOffset.UtcNow,
            DashboardSection<CpuMetrics>.Available(new CpuMetrics(10d, 4)),
            DashboardSection<LoadMetrics>.Available(new LoadMetrics(0.1d, 0.2d, 0.3d)),
            DashboardSection<UptimeMetrics>.Available(new UptimeMetrics(TimeSpan.FromHours(1))),
            DashboardSection<MemoryMetrics>.Available(new MemoryMetrics(1024, 512, 512, 50d, 0, 0, 0, null)),
            DashboardSection<NetworkMetrics>.Available(new NetworkMetrics(0, 0, 0d, 0d, [])),
            DashboardSection<IReadOnlyList<FileSystemMetrics>>.Available([]),
            []);

    private sealed class StubDashboardService : IServerDashboardService
    {
        private readonly ServerDashboardSnapshot _snapshot;

        public StubDashboardService(ServerDashboardSnapshot snapshot)
        {
            _snapshot = snapshot;
        }

        public ValueTask<ServerDashboardSnapshot> GetAsync(
            ServerProfile profile,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_snapshot);
    }
}
