using System.Net;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Profiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Networking;

namespace ServerDesk.Application.Databases;

public sealed record DatabaseTunnelEndpoint(
    Guid DatabaseProfileId,
    string LocalHost,
    int LocalPort,
    string RemoteHost,
    int RemotePort);

public interface IDatabaseTunnelLease : IAsyncDisposable
{
    DatabaseTunnelEndpoint Endpoint { get; }
}

public interface IDatabaseTunnelService
{
    ValueTask<IDatabaseTunnelLease> OpenAsync(
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken = default);
}

public sealed class DatabaseTunnelService : IDatabaseTunnelService
{
    public const string LoopbackHost = "127.0.0.1";

    private readonly IProfileRepository _serverRepository;
    private readonly IPortForwardSessionFactory _sessionFactory;

    public DatabaseTunnelService(
        IProfileRepository serverRepository,
        IPortForwardSessionFactory sessionFactory)
    {
        _serverRepository = serverRepository ?? throw new ArgumentNullException(nameof(serverRepository));
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
    }

    public async ValueTask<IDatabaseTunnelLease> OpenAsync(
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var server = await _serverRepository.GetAsync(profile.ServerProfileId, cancellationToken).ConfigureAwait(false)
            ?? throw new DatabaseTunnelException(new RemoteError(
                RemoteErrorCode.InvalidEndpoint,
                "The server profile for this database connection no longer exists."));

        var forward = PortForwardProfile.Create(
            Guid.NewGuid(),
            profile.ServerProfileId,
            $"Database tunnel {profile.Id:N}",
            PortForwardKind.Local,
            LoopbackHost,
            0,
            profile.RemoteHost,
            profile.RemotePort);
        var session = _sessionFactory.Create(server, forward);
        try
        {
            await session.StartAsync(cancellationToken).ConfigureAwait(false);
            if (session.State != PortForwardSessionState.Active || session.BoundPort is < 1 or > 65535)
            {
                throw new DatabaseTunnelException(new RemoteError(
                    RemoteErrorCode.ForwardingDenied,
                    "SSH forwarding returned without an active loopback listener."));
            }

            return new Lease(
                session,
                new DatabaseTunnelEndpoint(
                    profile.Id,
                    LoopbackHost,
                    session.BoundPort,
                    profile.RemoteHost,
                    profile.RemotePort));
        }
        catch
        {
            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch
            {
                // Preserve the original tunnel-start failure.
            }

            throw;
        }
    }

    private sealed class Lease : IDatabaseTunnelLease
    {
        private IPortForwardSession? _session;

        public Lease(IPortForwardSession session, DatabaseTunnelEndpoint endpoint)
        {
            _session = session;
            Endpoint = endpoint;
        }

        public DatabaseTunnelEndpoint Endpoint { get; }

        public async ValueTask DisposeAsync()
        {
            var session = Interlocked.Exchange(ref _session, null);
            if (session is null)
            {
                return;
            }

            Exception? cleanupError = null;
            try
            {
                if (session.State is PortForwardSessionState.Active or
                    PortForwardSessionState.Starting or
                    PortForwardSessionState.Stopping)
                {
                    await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception exception)
            {
                cleanupError = exception;
            }

            try
            {
                await session.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception exception) when (cleanupError is null)
            {
                cleanupError = exception;
            }

            if (cleanupError is not null)
            {
                throw cleanupError;
            }
        }
    }
}

public sealed class DatabaseTunnelException : Exception
{
    public DatabaseTunnelException(RemoteError error, Exception? innerException = null)
        : base(error?.Message, innerException)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public RemoteError Error { get; }
}

public sealed record DatabaseTunnelTestOptions(TimeSpan ConnectTimeout)
{
    public static DatabaseTunnelTestOptions Default { get; } = new(TimeSpan.FromSeconds(5));

    public void Validate()
    {
        if (ConnectTimeout <= TimeSpan.Zero || ConnectTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(ConnectTimeout));
        }
    }
}

public sealed record DatabaseTunnelTestResult(
    bool IsSuccess,
    DatabaseTunnelEndpoint? Endpoint,
    string Message,
    RemoteError? Error);

public interface IDatabaseTunnelConnectivityService
{
    Task<DatabaseTunnelTestResult> TestAsync(
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken = default);
}

public sealed class DatabaseTunnelConnectivityService : IDatabaseTunnelConnectivityService
{
    private readonly IDatabaseTunnelService _tunnelService;
    private readonly DatabaseTunnelTestOptions _options;
    private readonly IDatabaseEngineConnectivityProbe _engineProbe;

    public DatabaseTunnelConnectivityService(
        IDatabaseTunnelService tunnelService,
        DatabaseTunnelTestOptions options)
        : this(tunnelService, options, new DatabaseEngineConnectivityProbe())
    {
    }

    public DatabaseTunnelConnectivityService(
        IDatabaseTunnelService tunnelService,
        DatabaseTunnelTestOptions options,
        IDatabaseEngineConnectivityProbe engineProbe)
    {
        _tunnelService = tunnelService ?? throw new ArgumentNullException(nameof(tunnelService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _engineProbe = engineProbe ?? throw new ArgumentNullException(nameof(engineProbe));
        _options.Validate();
    }

    public async Task<DatabaseTunnelTestResult> TestAsync(
        DatabaseConnectionProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        try
        {
            await using var tunnel = await _tunnelService.OpenAsync(profile, cancellationToken).ConfigureAwait(false);
            var endpoint = tunnel.Endpoint;
            if (!IPAddress.TryParse(endpoint.LocalHost, out var localAddress) || !IPAddress.IsLoopback(localAddress))
            {
                return Failure(
                    RemoteErrorCode.InvalidEndpoint,
                    "Database tunnel did not bind to a loopback address.");
            }

            var probe = await _engineProbe.ProbeAsync(
                    profile.Engine,
                    localAddress,
                    endpoint.LocalPort,
                    _options.ConnectTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!probe.IsSuccess)
            {
                return new DatabaseTunnelTestResult(false, null, probe.Message, probe.Error);
            }

            return new DatabaseTunnelTestResult(
                true,
                endpoint,
                $"{probe.Message} Database credentials are not tested in M6.2.",
                null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DatabaseTunnelException exception)
        {
            return new DatabaseTunnelTestResult(false, null, exception.Error.Message, exception.Error);
        }
        catch (PortForwardSessionException exception)
        {
            return new DatabaseTunnelTestResult(false, null, exception.Error.Message, exception.Error);
        }
    }

    private static DatabaseTunnelTestResult Failure(RemoteErrorCode code, string message) =>
        new(false, null, message, new RemoteError(code, message));
}
