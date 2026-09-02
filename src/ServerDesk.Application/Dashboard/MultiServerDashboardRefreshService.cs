using System.Collections.Concurrent;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Dashboard;

public sealed record MultiServerDashboardTarget(ServerProfile Profile, bool IsConnected);

public enum MultiServerDashboardUpdateState
{
    Disconnected,
    Refreshing,
    Available,
    Failed,
    Cancelled,
}

public sealed record MultiServerDashboardUpdate(
    Guid ServerProfileId,
    MultiServerDashboardUpdateState State,
    ServerDashboardSnapshot? Snapshot = null,
    RemoteError? Error = null);

public sealed record MultiServerDashboardRefreshOptions(int MaxConcurrency)
{
    public static MultiServerDashboardRefreshOptions Default { get; } = new(4);

    public void Validate()
    {
        if (MaxConcurrency is < 1 or > 16)
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaxConcurrency),
                "Multi-server dashboard concurrency must be between 1 and 16.");
        }
    }
}

public interface IMultiServerDashboardRefreshService
{
    Task RefreshAsync(
        IReadOnlyCollection<MultiServerDashboardTarget> targets,
        Func<MultiServerDashboardUpdate, ValueTask> publishAsync,
        CancellationToken cancellationToken = default);

    bool TryGetCachedSnapshot(Guid serverProfileId, out ServerDashboardSnapshot snapshot);
}

public sealed class MultiServerDashboardRefreshService : IMultiServerDashboardRefreshService
{
    private readonly IServerDashboardService _dashboardService;
    private readonly MultiServerDashboardRefreshOptions _options;
    private readonly ConcurrentDictionary<Guid, ServerDashboardSnapshot> _snapshots = new();

    public MultiServerDashboardRefreshService(
        IServerDashboardService dashboardService,
        MultiServerDashboardRefreshOptions options)
    {
        _dashboardService = dashboardService ?? throw new ArgumentNullException(nameof(dashboardService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public bool TryGetCachedSnapshot(Guid serverProfileId, out ServerDashboardSnapshot snapshot) =>
        _snapshots.TryGetValue(serverProfileId, out snapshot!);

    public async Task RefreshAsync(
        IReadOnlyCollection<MultiServerDashboardTarget> targets,
        Func<MultiServerDashboardUpdate, ValueTask> publishAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(targets);
        ArgumentNullException.ThrowIfNull(publishAsync);

        var targetArray = targets.ToArray();
        if (targetArray.Any(target => target.Profile is null))
        {
            throw new ArgumentException("Dashboard targets must contain a server profile.", nameof(targets));
        }

        if (targetArray.Select(target => target.Profile.Id).Distinct().Count() != targetArray.Length)
        {
            throw new ArgumentException("Dashboard targets must have unique server profile ids.", nameof(targets));
        }

        using var gate = new SemaphoreSlim(_options.MaxConcurrency, _options.MaxConcurrency);
        var refreshTasks = targetArray
            .Select(target => RefreshTargetAsync(target, publishAsync, gate, cancellationToken))
            .ToArray();
        await Task.WhenAll(refreshTasks).ConfigureAwait(false);
    }

    private async Task RefreshTargetAsync(
        MultiServerDashboardTarget target,
        Func<MultiServerDashboardUpdate, ValueTask> publishAsync,
        SemaphoreSlim gate,
        CancellationToken cancellationToken)
    {
        if (!target.IsConnected)
        {
            _snapshots.TryRemove(target.Profile.Id, out _);
            await publishAsync(new MultiServerDashboardUpdate(
                target.Profile.Id,
                MultiServerDashboardUpdateState.Disconnected)).ConfigureAwait(false);
            return;
        }

        var enteredGate = false;
        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            enteredGate = true;
            cancellationToken.ThrowIfCancellationRequested();
            _snapshots.TryRemove(target.Profile.Id, out _);

            await publishAsync(new MultiServerDashboardUpdate(
                target.Profile.Id,
                MultiServerDashboardUpdateState.Refreshing)).ConfigureAwait(false);

            var snapshot = await _dashboardService
                .GetAsync(target.Profile, cancellationToken)
                .ConfigureAwait(false);
            _snapshots[target.Profile.Id] = snapshot;

            await publishAsync(new MultiServerDashboardUpdate(
                target.Profile.Id,
                MultiServerDashboardUpdateState.Available,
                snapshot)).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _snapshots.TryRemove(target.Profile.Id, out _);
            await publishAsync(new MultiServerDashboardUpdate(
                target.Profile.Id,
                MultiServerDashboardUpdateState.Cancelled,
                Error: new RemoteError(
                    RemoteErrorCode.OperationCancelled,
                    "Dashboard refresh was cancelled."))).ConfigureAwait(false);
        }
        catch (ServerDashboardException exception)
        {
            _snapshots.TryRemove(target.Profile.Id, out _);
            await publishAsync(new MultiServerDashboardUpdate(
                target.Profile.Id,
                MultiServerDashboardUpdateState.Failed,
                Error: exception.Error)).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _snapshots.TryRemove(target.Profile.Id, out _);
            await publishAsync(new MultiServerDashboardUpdate(
                target.Profile.Id,
                MultiServerDashboardUpdateState.Failed,
                Error: new RemoteError(
                    RemoteErrorCode.CommandFailed,
                    "Dashboard refresh failed.",
                    exception.GetType().Name))).ConfigureAwait(false);
        }
        finally
        {
            if (enteredGate)
            {
                gate.Release();
            }
        }
    }
}
