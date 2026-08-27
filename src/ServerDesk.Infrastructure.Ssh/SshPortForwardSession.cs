using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Infrastructure.Ssh;

public sealed class SshPortForwardSessionFactory : IPortForwardSessionFactory
{
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;

    public SshPortForwardSessionFactory(
        ISecretStore secretStore,
        IHostTrustService hostTrustService,
        IInteractiveAuthenticationPrompt interactivePrompt,
        SshSessionOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clientFactory = new SshClientFactory(
            secretStore ?? throw new ArgumentNullException(nameof(secretStore)),
            hostTrustService ?? throw new ArgumentNullException(nameof(hostTrustService)),
            interactivePrompt ?? throw new ArgumentNullException(nameof(interactivePrompt)),
            options);
    }

    public IPortForwardSession Create(ServerProfile serverProfile, PortForwardProfile forwardProfile)
    {
        ArgumentNullException.ThrowIfNull(serverProfile);
        ArgumentNullException.ThrowIfNull(forwardProfile);
        if (serverProfile.Id != forwardProfile.ServerProfileId)
        {
            throw new ArgumentException("Port-forward profile belongs to a different server.", nameof(forwardProfile));
        }

        return new SshPortForwardSession(serverProfile, forwardProfile, _clientFactory, _options);
    }
}

internal sealed class SshPortForwardSession : IPortForwardSession
{
    private readonly ServerProfile _serverProfile;
    private readonly PortForwardProfile _forwardProfile;
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private SshClientLease? _lease;
    private ForwardedPort? _forwardedPort;
    private bool _stopping;
    private bool _disposed;

    public SshPortForwardSession(
        ServerProfile serverProfile,
        PortForwardProfile forwardProfile,
        SshClientFactory clientFactory,
        SshSessionOptions options)
    {
        _serverProfile = serverProfile;
        _forwardProfile = forwardProfile;
        _clientFactory = clientFactory;
        _options = options;
        State = PortForwardSessionState.Created;
    }

    public Guid ForwardProfileId => _forwardProfile.Id;

    public PortForwardSessionState State { get; private set; }

    public int BoundPort { get; private set; }

    public RemoteError? LastError { get; private set; }

    public event Action<PortForwardSessionState>? StateChanged;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == PortForwardSessionState.Active && _forwardedPort?.IsStarted == true)
            {
                return;
            }

            DisposeConnection();
            LastError = null;
            BoundPort = 0;
            Transition(PortForwardSessionState.Starting);

            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(_options.ConnectTimeout);
            try
            {
                _lease = await _clientFactory.CreateAsync(
                        _serverProfile,
                        SshChannelPurpose.PortForward,
                        timeoutCancellation.Token)
                    .ConfigureAwait(false);
                _lease.Client.ErrorOccurred += ClientOnErrorOccurred;
                await _lease.Client.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);
                if (!_lease.Client.IsConnected)
                {
                    throw new SshConnectionException("SSH connection closed before the port forward could start.");
                }

                var forwardedPort = CreateForwardedPort(_forwardProfile);
                forwardedPort.Exception += ForwardedPortOnException;
                forwardedPort.Closing += ForwardedPortOnClosing;
                _lease.Client.AddForwardedPort(forwardedPort);
                _forwardedPort = forwardedPort;

                cancellationToken.ThrowIfCancellationRequested();
                await Task.Run(forwardedPort.Start, CancellationToken.None)
                    .WaitAsync(_options.ConnectTimeout, cancellationToken)
                    .ConfigureAwait(false);
                if (!forwardedPort.IsStarted)
                {
                    throw new SshException("SSH.NET returned without an active forwarded port.");
                }

                BoundPort = GetBoundPort(forwardedPort);
                LastError = null;
                Transition(PortForwardSessionState.Active);
            }
            catch (Exception exception)
            {
                var timedOut = !cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested;
                LastError = MapStartError(
                    exception,
                    _lease,
                    timedOut,
                    cancellationToken.IsCancellationRequested,
                    _forwardProfile);
                DisposeConnection();
                Transition(LastError.Code == RemoteErrorCode.OperationCancelled
                    ? PortForwardSessionState.Stopped
                    : PortForwardSessionState.Faulted);

                if (LastError.Code == RemoteErrorCode.OperationCancelled && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(LastError.Message, exception, cancellationToken);
                }

                throw exception is PortForwardSessionException forwardException
                    ? forwardException
                    : new PortForwardSessionException(LastError, exception);
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lease is null && _forwardedPort is null)
            {
                LastError = null;
                BoundPort = 0;
                Transition(PortForwardSessionState.Stopped);
                return;
            }

            _stopping = true;
            Transition(PortForwardSessionState.Stopping);
            try
            {
                if (_forwardedPort?.IsStarted == true)
                {
                    await Task.Run(_forwardedPort.Stop, CancellationToken.None)
                        .WaitAsync(_options.DisconnectTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (_lease?.Client.IsConnected == true)
                {
                    await Task.Run(_lease.Client.Disconnect, CancellationToken.None)
                        .WaitAsync(_options.DisconnectTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }

                LastError = null;
                BoundPort = 0;
                Transition(PortForwardSessionState.Stopped);
            }
            catch (OperationCanceledException exception)
            {
                LastError = new RemoteError(
                    RemoteErrorCode.OperationCancelled,
                    "Stopping the SSH port forward was cancelled.");
                BoundPort = 0;
                Transition(PortForwardSessionState.Stopped);
                throw new OperationCanceledException(LastError.Message, exception, cancellationToken);
            }
            catch (Exception exception)
            {
                LastError = MapRuntimeError(exception, _forwardProfile);
                BoundPort = 0;
                Transition(PortForwardSessionState.Faulted);
                throw new PortForwardSessionException(LastError, exception);
            }
            finally
            {
                DisposeConnection();
                _stopping = false;
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _stopping = true;
            DisposeConnection();
            BoundPort = 0;
            State = PortForwardSessionState.Stopped;
        }
        finally
        {
            _stopping = false;
            _stateGate.Release();
            _stateGate.Dispose();
        }
    }

    private void ForwardedPortOnException(object? sender, ExceptionEventArgs eventArgs)
    {
        if (_disposed || State is PortForwardSessionState.Stopping or PortForwardSessionState.Stopped)
        {
            return;
        }

        LastError = MapRuntimeError(eventArgs.Exception, _forwardProfile);
        if (eventArgs.Exception is SshConnectionException or ObjectDisposedException)
        {
            Transition(PortForwardSessionState.Faulted);
        }
        else
        {
            StateChanged?.Invoke(State);
        }
    }

    private void ForwardedPortOnClosing(object? sender, EventArgs e)
    {
        if (_disposed || _stopping || State != PortForwardSessionState.Active)
        {
            return;
        }

        LastError = new RemoteError(
            RemoteErrorCode.NetworkInterrupted,
            $"Port forward '{_forwardProfile.Name}' stopped because the SSH connection closed.");
        BoundPort = 0;
        Transition(PortForwardSessionState.Faulted);
    }

    private void ClientOnErrorOccurred(object? sender, ExceptionEventArgs eventArgs)
    {
        if (_disposed || _stopping || State is PortForwardSessionState.Stopped or PortForwardSessionState.Faulted)
        {
            return;
        }

        LastError = MapRuntimeError(eventArgs.Exception, _forwardProfile);
        BoundPort = 0;
        Transition(PortForwardSessionState.Faulted);
    }

    private void DisposeConnection()
    {
        if (_forwardedPort is not null)
        {
            _forwardedPort.Exception -= ForwardedPortOnException;
            _forwardedPort.Closing -= ForwardedPortOnClosing;
            try
            {
                if (_forwardedPort.IsStarted)
                {
                    _forwardedPort.Stop();
                }
            }
            catch
            {
                // Disposing the SSH client below is the final cleanup path.
            }

            _forwardedPort.Dispose();
            _forwardedPort = null;
        }

        if (_lease is not null)
        {
            _lease.Client.ErrorOccurred -= ClientOnErrorOccurred;
            _lease.Dispose();
            _lease = null;
        }
    }

    private static ForwardedPort CreateForwardedPort(PortForwardProfile profile) =>
        profile.Kind switch
        {
            PortForwardKind.Local => new ForwardedPortLocal(
                profile.BindHost,
                checked((uint)profile.BindPort),
                profile.DestinationHost!,
                checked((uint)profile.DestinationPort!.Value)),
            PortForwardKind.Remote => new ForwardedPortRemote(
                profile.BindHost,
                checked((uint)profile.BindPort),
                profile.DestinationHost!,
                checked((uint)profile.DestinationPort!.Value)),
            PortForwardKind.Dynamic => new ForwardedPortDynamic(
                profile.BindHost,
                checked((uint)profile.BindPort)),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), "Unsupported port-forward kind."),
        };

    private static int GetBoundPort(ForwardedPort forwardedPort) =>
        forwardedPort switch
        {
            ForwardedPortLocal local => checked((int)local.BoundPort),
            ForwardedPortRemote remote => checked((int)remote.BoundPort),
            ForwardedPortDynamic dynamic => checked((int)dynamic.BoundPort),
            _ => throw new InvalidOperationException("Unknown forwarded-port implementation."),
        };

    private static RemoteError MapStartError(
        Exception exception,
        SshClientLease? lease,
        bool timedOut,
        bool callerCancelled,
        PortForwardProfile profile)
    {
        if (exception is PortForwardSessionException forwardException)
        {
            return forwardException.Error;
        }

        if (exception is SocketException socketException && socketException.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            return new RemoteError(
                RemoteErrorCode.PortInUse,
                $"{profile.BindHost}:{profile.BindPort} is already in use. Choose another bind port.",
                socketException.Message);
        }

        if (exception is ArgumentException or ArgumentOutOfRangeException)
        {
            return new RemoteError(
                RemoteErrorCode.InvalidEndpoint,
                "The port-forward endpoint is invalid.",
                exception.Message);
        }

        if (exception is SshException && profile.Kind == PortForwardKind.Remote)
        {
            return new RemoteError(
                RemoteErrorCode.ForwardingDenied,
                "The SSH server denied remote port forwarding. Check AllowTcpForwarding/GatewayPorts policy.",
                exception.Message);
        }

        return SshRemoteErrorMapper.Map(exception, lease, timedOut, callerCancelled);
    }

    private static RemoteError MapRuntimeError(Exception exception, PortForwardProfile profile) =>
        exception switch
        {
            SocketException socketException when socketException.SocketErrorCode == SocketError.AddressAlreadyInUse =>
                new RemoteError(
                    RemoteErrorCode.PortInUse,
                    $"{profile.BindHost}:{profile.BindPort} is already in use.",
                    socketException.Message),
            SocketException socketException => new RemoteError(
                RemoteErrorCode.NetworkInterrupted,
                $"A forwarded connection for '{profile.Name}' failed.",
                socketException.Message),
            SshConnectionException => new RemoteError(
                RemoteErrorCode.NetworkInterrupted,
                $"The SSH connection carrying port forward '{profile.Name}' was interrupted.",
                exception.Message),
            SshException when profile.Kind == PortForwardKind.Remote => new RemoteError(
                RemoteErrorCode.ForwardingDenied,
                $"The SSH server rejected remote forwarding for '{profile.Name}'.",
                exception.Message),
            ObjectDisposedException => new RemoteError(
                RemoteErrorCode.NetworkInterrupted,
                $"Port forward '{profile.Name}' was closed unexpectedly."),
            _ => new RemoteError(
                RemoteErrorCode.NetworkInterrupted,
                $"Port forward '{profile.Name}' encountered an error.",
                $"{exception.GetType().Name}: {exception.Message}"),
        };

    private void Transition(PortForwardSessionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
