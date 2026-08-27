using ServerDesk.Application.Profiles;
using ServerDesk.Application.Routing;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.History;

public enum ConnectionAttemptOutcome
{
    Connected = 1,
    Cancelled = 2,
    AuthenticationFailed = 3,
    HostTrustFailed = 4,
    NetworkFailed = 5,
    Failed = 6,
}

public static class ConnectionHistoryPolicy
{
    public const int MaxEntries = 500;
    public const int DefaultUiLimit = 100;
}

public sealed record ConnectionHistoryEntry
{
    public ConnectionHistoryEntry(
        Guid id,
        Guid? serverProfileId,
        string profileName,
        string endpoint,
        string routeSummary,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        ConnectionAttemptOutcome outcome,
        RemoteErrorCode? failureCode)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Connection history id cannot be empty.", nameof(id));
        }

        if (serverProfileId == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty when present.", nameof(serverProfileId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(endpoint);
        ArgumentException.ThrowIfNullOrWhiteSpace(routeSummary);
        if (endedAtUtc < startedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(endedAtUtc), "End time cannot precede start time.");
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome));
        }

        Id = id;
        ServerProfileId = serverProfileId;
        ProfileName = TrimTo(profileName, 100);
        Endpoint = TrimTo(endpoint, 512);
        RouteSummary = TrimTo(routeSummary, 256);
        StartedAtUtc = startedAtUtc;
        EndedAtUtc = endedAtUtc;
        Outcome = outcome;
        FailureCode = failureCode;
    }

    public Guid Id { get; }

    public Guid? ServerProfileId { get; }

    public string ProfileName { get; }

    public string Endpoint { get; }

    public string RouteSummary { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset EndedAtUtc { get; }

    public ConnectionAttemptOutcome Outcome { get; }

    public RemoteErrorCode? FailureCode { get; }

    public TimeSpan Duration => EndedAtUtc - StartedAtUtc;

    private static string TrimTo(string value, int maximumLength)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maximumLength ? trimmed : trimmed[..maximumLength];
    }
}

public interface IConnectionHistoryRepository
{
    ValueTask AppendAsync(
        ConnectionHistoryEntry entry,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<ConnectionHistoryEntry>> ListRecentAsync(
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed class ConnectionHistoryRemoteSessionFactory : IRemoteSessionFactory
{
    private readonly IRemoteSessionFactory _innerFactory;
    private readonly IConnectionHistoryRepository _historyRepository;
    private readonly IConnectionRouteRepository _routeRepository;
    private readonly IProfileRepository _profileRepository;

    public ConnectionHistoryRemoteSessionFactory(
        IRemoteSessionFactory innerFactory,
        IConnectionHistoryRepository historyRepository,
        IConnectionRouteRepository routeRepository,
        IProfileRepository profileRepository)
    {
        _innerFactory = innerFactory;
        _historyRepository = historyRepository;
        _routeRepository = routeRepository;
        _profileRepository = profileRepository;
    }

    public IRemoteSession Create(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new ConnectionHistoryRemoteSession(
            _innerFactory.Create(profile),
            profile,
            _historyRepository,
            _routeRepository,
            _profileRepository);
    }

    private sealed class ConnectionHistoryRemoteSession : IRemoteSession
    {
        private readonly IRemoteSession _inner;
        private readonly ServerProfile _profile;
        private readonly IConnectionHistoryRepository _historyRepository;
        private readonly IConnectionRouteRepository _routeRepository;
        private readonly IProfileRepository _profileRepository;

        public ConnectionHistoryRemoteSession(
            IRemoteSession inner,
            ServerProfile profile,
            IConnectionHistoryRepository historyRepository,
            IConnectionRouteRepository routeRepository,
            IProfileRepository profileRepository)
        {
            _inner = inner;
            _profile = profile;
            _historyRepository = historyRepository;
            _routeRepository = routeRepository;
            _profileRepository = profileRepository;
        }

        public Guid ServerProfileId => _inner.ServerProfileId;

        public RemoteSessionState State => _inner.State;

        public RemoteError? LastError => _inner.LastError;

        public string? ServerVersion => _inner.ServerVersion;

        public DateTimeOffset? ConnectedAtUtc => _inner.ConnectedAtUtc;

        public event Action<RemoteSessionState>? StateChanged
        {
            add => _inner.StateChanged += value;
            remove => _inner.StateChanged -= value;
        }

        public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            var startedAtUtc = DateTimeOffset.UtcNow;
            var routeSummary = await BuildRouteSummarySafelyAsync().ConfigureAwait(false);

            try
            {
                await _inner.ConnectAsync(cancellationToken).ConfigureAwait(false);
                await AppendSafelyAsync(
                        startedAtUtc,
                        DateTimeOffset.UtcNow,
                        routeSummary,
                        ConnectionAttemptOutcome.Connected,
                        null)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await AppendSafelyAsync(
                        startedAtUtc,
                        DateTimeOffset.UtcNow,
                        routeSummary,
                        ConnectionAttemptOutcome.Cancelled,
                        RemoteErrorCode.OperationCancelled)
                    .ConfigureAwait(false);
                throw;
            }
            catch (RemoteSessionException exception)
            {
                await AppendSafelyAsync(
                        startedAtUtc,
                        DateTimeOffset.UtcNow,
                        routeSummary,
                        Classify(exception.Error.Code),
                        exception.Error.Code)
                    .ConfigureAwait(false);
                throw;
            }
            catch
            {
                await AppendSafelyAsync(
                        startedAtUtc,
                        DateTimeOffset.UtcNow,
                        routeSummary,
                        ConnectionAttemptOutcome.Failed,
                        null)
                    .ConfigureAwait(false);
                throw;
            }
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default) =>
            _inner.DisconnectAsync(cancellationToken);

        public ValueTask DisposeAsync() => _inner.DisposeAsync();

        private async ValueTask<string> BuildRouteSummarySafelyAsync()
        {
            try
            {
                var route = await _routeRepository.GetAsync(_profile.Id, CancellationToken.None).ConfigureAwait(false)
                    ?? ServerConnectionRoute.Direct(_profile.Id);
                return route.Kind switch
                {
                    ServerConnectionRouteKind.Direct => "Direct",
                    ServerConnectionRouteKind.HttpProxy => $"HTTP proxy via {route.ProxyHost}:{route.ProxyPort}",
                    ServerConnectionRouteKind.Socks4Proxy => $"SOCKS4 proxy via {route.ProxyHost}:{route.ProxyPort}",
                    ServerConnectionRouteKind.Socks5Proxy => $"SOCKS5 proxy via {route.ProxyHost}:{route.ProxyPort}",
                    ServerConnectionRouteKind.Bastion => await BuildBastionSummaryAsync(route).ConfigureAwait(false),
                    _ => "Unknown route",
                };
            }
            catch
            {
                return "Unknown route";
            }
        }

        private async ValueTask<string> BuildBastionSummaryAsync(ServerConnectionRoute route)
        {
            if (route.BastionProfileId is null)
            {
                return "Bastion";
            }

            var bastion = await _profileRepository.GetAsync(route.BastionProfileId.Value, CancellationToken.None)
                .ConfigureAwait(false);
            return bastion is null ? "Bastion (missing profile)" : $"Bastion via {bastion.Name}";
        }

        private async ValueTask AppendSafelyAsync(
            DateTimeOffset startedAtUtc,
            DateTimeOffset endedAtUtc,
            string routeSummary,
            ConnectionAttemptOutcome outcome,
            RemoteErrorCode? failureCode)
        {
            try
            {
                var entry = new ConnectionHistoryEntry(
                    Guid.NewGuid(),
                    _profile.Id,
                    _profile.Name,
                    $"{_profile.Username}@{_profile.Host}:{_profile.Port}",
                    routeSummary,
                    startedAtUtc,
                    endedAtUtc,
                    outcome,
                    failureCode);
                await _historyRepository.AppendAsync(entry, CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // Connection history is audit metadata and must never mask or change the remote connection result.
            }
        }

        private static ConnectionAttemptOutcome Classify(RemoteErrorCode code) =>
            code switch
            {
                RemoteErrorCode.AuthenticationFailed => ConnectionAttemptOutcome.AuthenticationFailed,
                RemoteErrorCode.HostKeyUnknown or RemoteErrorCode.HostKeyMismatch => ConnectionAttemptOutcome.HostTrustFailed,
                RemoteErrorCode.ConnectionFailed or RemoteErrorCode.NetworkInterrupted => ConnectionAttemptOutcome.NetworkFailed,
                RemoteErrorCode.OperationCancelled => ConnectionAttemptOutcome.Cancelled,
                _ => ConnectionAttemptOutcome.Failed,
            };
    }
}
