using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Application.Terminal;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Infrastructure.Ssh;

public sealed class SshRemoteTerminalSessionFactory : IRemoteTerminalSessionFactory
{
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;

    public SshRemoteTerminalSessionFactory(
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

    public IRemoteTerminalSession Create(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new SshRemoteTerminalSession(profile, _clientFactory, _options);
    }
}

internal sealed class SshRemoteTerminalSession : IRemoteTerminalSession
{
    private const int ShellBufferSize = 64 * 1024;
    private const int ReadBufferSize = 16 * 1024;

    private readonly ServerProfile _profile;
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private SshClientLease? _lease;
    private ShellStream? _shellStream;
    private CancellationTokenSource? _lifetimeCancellation;
    private Task? _readerTask;
    private bool _disposed;

    public SshRemoteTerminalSession(
        ServerProfile profile,
        SshClientFactory clientFactory,
        SshSessionOptions options)
    {
        _profile = profile;
        _clientFactory = clientFactory;
        _options = options;
        State = TerminalSessionState.Created;
    }

    public Guid ServerProfileId => _profile.Id;

    public TerminalSessionState State { get; private set; }

    public RemoteError? LastError { get; private set; }

    public event Action<TerminalSessionState>? StateChanged;

    public event Action<string>? OutputReceived;

    public async ValueTask ConnectAsync(
        TerminalSize initialSize,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (State == TerminalSessionState.Connected && _lease?.Client.IsConnected == true)
            {
                return;
            }

            await DisposeConnectionAsync().ConfigureAwait(false);
            LastError = null;
            Transition(TerminalSessionState.Connecting);

            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(_options.ConnectTimeout);

            try
            {
                _lease = await _clientFactory.CreateAsync(
                        _profile,
                        SshChannelPurpose.Terminal,
                        timeoutCancellation.Token)
                    .ConfigureAwait(false);
                _lease.Client.ErrorOccurred += ClientOnErrorOccurred;
                await _lease.Client.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);
                if (!_lease.Client.IsConnected)
                {
                    throw new SshConnectionException("SSH.NET completed terminal connection establishment without an active session.");
                }

                timeoutCancellation.Token.ThrowIfCancellationRequested();
                _shellStream = _lease.Client.CreateShellStream(
                    "xterm-256color",
                    initialSize.Columns,
                    initialSize.Rows,
                    initialSize.PixelWidth,
                    initialSize.PixelHeight,
                    ShellBufferSize);
                _lifetimeCancellation = new CancellationTokenSource();
                _readerTask = ReadOutputLoopAsync(_shellStream, _lifetimeCancellation.Token);
                LastError = null;
                Transition(TerminalSessionState.Connected);
            }
            catch (Exception exception)
            {
                var timedOut = !cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested;
                var error = SshRemoteErrorMapper.Map(
                    exception,
                    _lease,
                    timedOut,
                    cancellationToken.IsCancellationRequested);
                LastError = error;
                await DisposeConnectionAsync().ConfigureAwait(false);
                Transition(error.Code == RemoteErrorCode.OperationCancelled
                    ? TerminalSessionState.Disconnected
                    : TerminalSessionState.Faulted);

                if (error.Code == RemoteErrorCode.OperationCancelled && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(error.Message, exception, cancellationToken);
                }

                throw exception is TerminalSessionException terminalException
                    ? terminalException
                    : new TerminalSessionException(error, exception);
            }
        }
        finally
        {
            _stateGate.Release();
        }
    }

    public async ValueTask SendAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        ThrowIfDisposed();
        var shellStream = GetConnectedShell();
        if (input.Length == 0)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            await shellStream.WriteAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            await shellStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateOperationException(exception, "send terminal input");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask ResizeAsync(
        TerminalSize size,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var shellStream = GetConnectedShell();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            shellStream.ChangeWindowSize(
                size.Columns,
                size.Rows,
                size.PixelWidth,
                size.PixelHeight);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw CreateOperationException(exception, "resize the remote PTY");
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lease is null && _shellStream is null)
            {
                LastError = null;
                Transition(TerminalSessionState.Disconnected);
                return;
            }

            Transition(TerminalSessionState.Disconnecting);
            try
            {
                await DisposeConnectionAsync(cancellationToken).ConfigureAwait(false);
                LastError = null;
                Transition(TerminalSessionState.Disconnected);
            }
            catch (OperationCanceledException exception)
            {
                LastError = new RemoteError(
                    RemoteErrorCode.OperationCancelled,
                    "Terminal disconnect was cancelled.");
                await DisposeConnectionAsync().ConfigureAwait(false);
                Transition(TerminalSessionState.Disconnected);
                throw new OperationCanceledException(LastError.Message, exception, cancellationToken);
            }
            catch (Exception exception)
            {
                LastError = SshRemoteErrorMapper.Map(
                    exception,
                    _lease,
                    timedOut: false,
                    callerCancelled: false);
                await DisposeConnectionAsync().ConfigureAwait(false);
                Transition(TerminalSessionState.Faulted);
                throw new TerminalSessionException(LastError, exception);
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
            await DisposeConnectionAsync().ConfigureAwait(false);
            State = TerminalSessionState.Disconnected;
        }
        finally
        {
            _stateGate.Release();
            _stateGate.Dispose();
            _writeGate.Dispose();
        }
    }

    private async Task ReadOutputLoopAsync(ShellStream shellStream, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReadBufferSize];
        var decoder = Encoding.UTF8.GetDecoder();
        var characters = new char[Encoding.UTF8.GetMaxCharCount(ReadBufferSize)];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var read = await shellStream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    if (!cancellationToken.IsCancellationRequested)
                    {
                        LastError = new RemoteError(
                            RemoteErrorCode.NetworkInterrupted,
                            "The remote terminal shell closed.");
                        Transition(TerminalSessionState.Disconnected);
                    }

                    return;
                }

                decoder.Convert(
                    buffer,
                    0,
                    read,
                    characters,
                    0,
                    characters.Length,
                    flush: false,
                    out _,
                    out var charsUsed,
                    out _);
                if (charsUsed > 0)
                {
                    OutputReceived?.Invoke(new string(characters, 0, charsUsed));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (IOException exception) when (cancellationToken.IsCancellationRequested)
        {
            _ = exception;
        }
        catch (Exception exception)
        {
            if (State is TerminalSessionState.Connected)
            {
                LastError = SshRemoteErrorMapper.Map(
                    exception,
                    _lease,
                    timedOut: false,
                    callerCancelled: false);
                Transition(TerminalSessionState.Faulted);
            }
        }
    }

    private async ValueTask DisposeConnectionAsync(CancellationToken cancellationToken = default)
    {
        var lifetimeCancellation = _lifetimeCancellation;
        var readerTask = _readerTask;
        var shellStream = _shellStream;
        var lease = _lease;

        _lifetimeCancellation = null;
        _readerTask = null;
        _shellStream = null;
        _lease = null;

        lifetimeCancellation?.Cancel();
        shellStream?.Dispose();

        if (readerTask is not null)
        {
            try
            {
                await readerTask
                    .WaitAsync(_options.DisconnectTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
            }
            catch (TimeoutException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (lease is not null)
        {
            lease.Client.ErrorOccurred -= ClientOnErrorOccurred;
            try
            {
                if (lease.Client.IsConnected)
                {
                    await Task.Run(lease.Client.Disconnect, CancellationToken.None)
                        .WaitAsync(_options.DisconnectTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (TimeoutException)
            {
            }
            finally
            {
                lease.Dispose();
            }
        }

        lifetimeCancellation?.Dispose();
    }

    private void ClientOnErrorOccurred(object? sender, ExceptionEventArgs eventArgs)
    {
        if (State != TerminalSessionState.Connected)
        {
            return;
        }

        LastError = SshRemoteErrorMapper.Map(
            eventArgs.Exception,
            _lease,
            timedOut: false,
            callerCancelled: false);
        Transition(TerminalSessionState.Faulted);
    }

    private ShellStream GetConnectedShell()
    {
        if (State != TerminalSessionState.Connected ||
            _lease?.Client.IsConnected != true ||
            _shellStream is null)
        {
            throw new TerminalSessionException(new RemoteError(
                RemoteErrorCode.NetworkInterrupted,
                "The remote terminal is not connected."));
        }

        return _shellStream;
    }

    private TerminalSessionException CreateOperationException(Exception exception, string action)
    {
        var error = SshRemoteErrorMapper.Map(
            exception,
            _lease,
            timedOut: false,
            callerCancelled: false);
        return new TerminalSessionException(
            error with { Message = $"ServerDesk could not {action}: {error.Message}" },
            exception);
    }

    private void Transition(TerminalSessionState state)
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
