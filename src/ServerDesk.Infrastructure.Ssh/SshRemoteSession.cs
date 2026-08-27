using System.Net.Sockets;
using Renci.SshNet;
using Renci.SshNet.Common;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Infrastructure.Ssh;

public sealed record SshSessionOptions(
    TimeSpan ConnectTimeout,
    TimeSpan DisconnectTimeout,
    TimeSpan KeepAliveInterval,
    TimeSpan ConnectionMonitorInterval)
{
    public static SshSessionOptions Default { get; } = new(
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(2));
}

public sealed class SshRemoteSessionFactory : IRemoteSessionFactory
{
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;

    public SshRemoteSessionFactory(
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

    public IRemoteSession Create(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new SshRemoteSession(profile, _clientFactory, _options);
    }
}

internal enum SshChannelPurpose
{
    Control,
    Command,
    FileTransfer,
    Terminal,
    PortForward,
}

internal sealed class SshClientFactory
{
    private readonly ISecretStore _secretStore;
    private readonly IHostTrustService _hostTrustService;
    private readonly IInteractiveAuthenticationPrompt _interactivePrompt;
    private readonly SshSessionOptions _options;

    public SshClientFactory(
        ISecretStore secretStore,
        IHostTrustService hostTrustService,
        IInteractiveAuthenticationPrompt interactivePrompt,
        SshSessionOptions options)
    {
        _secretStore = secretStore;
        _hostTrustService = hostTrustService;
        _interactivePrompt = interactivePrompt;
        _options = options;
    }

    public async ValueTask<SshClientLease> CreateAsync(
        ServerProfile profile,
        SshChannelPurpose purpose,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var resources = new List<IDisposable>();
        AuthenticationMethod authenticationMethod;

        switch (profile.AuthenticationKind)
        {
            case ServerAuthenticationKind.Password:
                var password = await GetRequiredSecretAsync(profile, "SSH password", cancellationToken)
                    .ConfigureAwait(false);
                authenticationMethod = new PasswordAuthenticationMethod(profile.Username, password);
                resources.Add(authenticationMethod);
                break;

            case ServerAuthenticationKind.PrivateKey:
                if (string.IsNullOrWhiteSpace(profile.PrivateKeyPath))
                {
                    throw CreateSessionException(
                        RemoteErrorCode.AuthenticationFailed,
                        "This server profile does not have a private-key path.");
                }

                var passphrase = profile.CredentialReference is null
                    ? null
                    : await _secretStore.GetAsync(profile.CredentialReference.Value, cancellationToken)
                        .ConfigureAwait(false);

                IPrivateKeySource privateKey;
                try
                {
                    privateKey = string.IsNullOrEmpty(passphrase)
                        ? new PrivateKeyFile(profile.PrivateKeyPath)
                        : new PrivateKeyFile(profile.PrivateKeyPath, passphrase);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException or SshException or ArgumentException)
                {
                    throw CreateSessionException(
                        RemoteErrorCode.AuthenticationFailed,
                        "ServerDesk could not load the configured SSH private key.",
                        exception);
                }

                if (privateKey is IDisposable privateKeyDisposable)
                {
                    resources.Add(privateKeyDisposable);
                }

                authenticationMethod = new PrivateKeyAuthenticationMethod(profile.Username, privateKey);
                resources.Add(authenticationMethod);
                break;

            case ServerAuthenticationKind.KeyboardInteractive:
                var keyboardInteractive = new KeyboardInteractiveAuthenticationMethod(profile.Username);
                authenticationMethod = keyboardInteractive;
                resources.Add(authenticationMethod);
                break;

            case ServerAuthenticationKind.SshAgent:
                throw CreateSessionException(
                    RemoteErrorCode.CapabilityUnavailable,
                    "SSH-agent authentication is not available in the current SSH transport yet. Use a private key, password or keyboard-interactive profile.");

            default:
                throw CreateSessionException(
                    RemoteErrorCode.CapabilityUnavailable,
                    "The configured SSH authentication type is not supported.");
        }

        var connectionInfo = new ConnectionInfo(
            profile.Host,
            profile.Port,
            profile.Username,
            authenticationMethod)
        {
            Timeout = _options.ConnectTimeout,
            ChannelCloseTimeout = _options.DisconnectTimeout,
        };

        var client = new SshClient(connectionInfo)
        {
            KeepAliveInterval = _options.KeepAliveInterval,
        };
        var lease = new SshClientLease(client, resources, purpose);

        client.HostKeyReceived += (_, eventArgs) => VerifyHostKey(profile, lease, eventArgs, cancellationToken);
        if (authenticationMethod is KeyboardInteractiveAuthenticationMethod interactiveMethod)
        {
            interactiveMethod.AuthenticationPrompt += (_, eventArgs) =>
                HandleInteractiveAuthentication(profile, lease, eventArgs, cancellationToken);
        }

        return lease;
    }

    private async ValueTask<string> GetRequiredSecretAsync(
        ServerProfile profile,
        string description,
        CancellationToken cancellationToken)
    {
        if (profile.CredentialReference is null)
        {
            throw CreateSessionException(
                RemoteErrorCode.AuthenticationFailed,
                $"The stored {description} reference is missing. Edit the server profile and enter the credential again.");
        }

        var secret = await _secretStore.GetAsync(profile.CredentialReference.Value, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrEmpty(secret))
        {
            throw CreateSessionException(
                RemoteErrorCode.AuthenticationFailed,
                $"The stored {description} could not be found. Edit the server profile and enter the credential again.");
        }

        return secret;
    }

    private void VerifyHostKey(
        ServerProfile profile,
        SshClientLease lease,
        HostKeyEventArgs eventArgs,
        CancellationToken cancellationToken)
    {
        try
        {
            var observation = HostKeyObservation.Create(
                profile.Host,
                profile.Port,
                eventArgs.HostKeyName,
                HostKeyFingerprint.FromHostKey(eventArgs.HostKey));
            var verification = _hostTrustService.VerifyAsync(observation, cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            eventArgs.CanTrust = verification.IsTrusted;
            if (!verification.IsTrusted)
            {
                lease.HostTrustFailure = verification;
            }
        }
        catch (OperationCanceledException exception)
        {
            lease.HostTrustBridgeException = exception;
            eventArgs.CanTrust = false;
        }
        catch (Exception exception)
        {
            lease.HostTrustBridgeException = exception;
            eventArgs.CanTrust = false;
        }
    }

    private void HandleInteractiveAuthentication(
        ServerProfile profile,
        SshClientLease lease,
        AuthenticationPromptEventArgs eventArgs,
        CancellationToken cancellationToken)
    {
        try
        {
            var challenge = new InteractiveAuthenticationChallenge(
                profile.Username,
                eventArgs.Instruction,
                eventArgs.Prompts
                    .Select(prompt => new InteractiveAuthenticationPrompt(
                        prompt.Id,
                        prompt.Request,
                        !prompt.IsEchoed))
                    .ToArray());
            var responses = _interactivePrompt.PromptAsync(challenge, cancellationToken)
                .AsTask()
                .GetAwaiter()
                .GetResult();

            if (responses is null)
            {
                lease.InteractiveAuthenticationCancelled = true;
                return;
            }

            if (responses.Count != eventArgs.Prompts.Count)
            {
                lease.InteractiveAuthenticationException = new InvalidOperationException(
                    "The interactive authentication prompt returned an unexpected number of responses.");
                return;
            }

            for (var index = 0; index < eventArgs.Prompts.Count; index++)
            {
                eventArgs.Prompts[index].Response = responses[index];
            }
        }
        catch (OperationCanceledException exception)
        {
            lease.InteractiveAuthenticationCancelled = true;
            lease.InteractiveAuthenticationException = exception;
        }
        catch (Exception exception)
        {
            lease.InteractiveAuthenticationException = exception;
        }
    }

    private static RemoteSessionException CreateSessionException(
        RemoteErrorCode code,
        string message,
        Exception? innerException = null) =>
        new(
            new RemoteError(
                code,
                message,
                innerException is null ? null : $"{innerException.GetType().Name}: {innerException.Message}"),
            innerException);
}

internal sealed class SshClientLease : IDisposable
{
    private readonly IReadOnlyList<IDisposable> _resources;
    private bool _disposed;

    public SshClientLease(
        SshClient client,
        IReadOnlyList<IDisposable> resources,
        SshChannelPurpose purpose)
    {
        Client = client;
        _resources = resources;
        Purpose = purpose;
    }

    public SshClient Client { get; }

    public SshChannelPurpose Purpose { get; }

    public HostTrustVerification? HostTrustFailure { get; set; }

    public Exception? HostTrustBridgeException { get; set; }

    public bool InteractiveAuthenticationCancelled { get; set; }

    public Exception? InteractiveAuthenticationException { get; set; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Client.Dispose();
        for (var index = _resources.Count - 1; index >= 0; index--)
        {
            _resources[index].Dispose();
        }
    }
}

internal sealed class SshRemoteSession : IRemoteSession
{
    private readonly ServerProfile _profile;
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private SshClientLease? _lease;
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private bool _hasAttemptedConnection;
    private bool _disposed;

    public SshRemoteSession(
        ServerProfile profile,
        SshClientFactory clientFactory,
        SshSessionOptions options)
    {
        _profile = profile;
        _clientFactory = clientFactory;
        _options = options;
        State = RemoteSessionState.Created;
    }

    public Guid ServerProfileId => _profile.Id;

    public RemoteSessionState State { get; private set; }

    public RemoteError? LastError { get; private set; }

    public string? ServerVersion { get; private set; }

    public DateTimeOffset? ConnectedAtUtc { get; private set; }

    public event Action<RemoteSessionState>? StateChanged;

    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var gateEntered = false;
        try
        {
            await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
        }
        catch (OperationCanceledException)
        {
            LastError = CreateCancellationError();
            Transition(RemoteSessionState.Disconnected);
            throw;
        }

        try
        {
            if (State == RemoteSessionState.Connected && _lease?.Client.IsConnected == true)
            {
                return;
            }

            DisposeCurrentLease();
            LastError = null;
            ServerVersion = null;
            ConnectedAtUtc = null;
            Transition(_hasAttemptedConnection ? RemoteSessionState.Reconnecting : RemoteSessionState.Connecting);
            _hasAttemptedConnection = true;

            using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCancellation.CancelAfter(_options.ConnectTimeout);

            try
            {
                _lease = await _clientFactory.CreateAsync(
                        _profile,
                        SshChannelPurpose.Control,
                        timeoutCancellation.Token)
                    .ConfigureAwait(false);
                _lease.Client.ErrorOccurred += ClientOnErrorOccurred;

                await _lease.Client.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);
                if (!_lease.Client.IsConnected)
                {
                    throw new SshConnectionException("SSH.NET completed connection establishment without an active session.");
                }

                ServerVersion = _lease.Client.ConnectionInfo.ServerVersion;
                ConnectedAtUtc = DateTimeOffset.UtcNow;
                LastError = null;
                Transition(RemoteSessionState.Connected);
                StartConnectionMonitor(_lease.Client);
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
                DisposeCurrentLease();
                Transition(error.Code == RemoteErrorCode.OperationCancelled
                    ? RemoteSessionState.Disconnected
                    : RemoteSessionState.Faulted);

                if (error.Code == RemoteErrorCode.OperationCancelled && cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(error.Message, exception, cancellationToken);
                }

                throw exception is RemoteSessionException sessionException
                    ? sessionException
                    : new RemoteSessionException(error, exception);
            }
        }
        finally
        {
            if (gateEntered)
            {
                _stateGate.Release();
            }
        }
    }

    public async ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_lease is null)
            {
                LastError = null;
                Transition(RemoteSessionState.Disconnected);
                return;
            }

            Transition(RemoteSessionState.Disconnecting);
            StopConnectionMonitor();

            try
            {
                if (_lease.Client.IsConnected)
                {
                    var disconnectTask = Task.Run(_lease.Client.Disconnect, CancellationToken.None);
                    await disconnectTask
                        .WaitAsync(_options.DisconnectTimeout, cancellationToken)
                        .ConfigureAwait(false);
                }

                LastError = null;
                ServerVersion = null;
                ConnectedAtUtc = null;
                Transition(RemoteSessionState.Disconnected);
            }
            catch (OperationCanceledException exception)
            {
                LastError = CreateCancellationError();
                Transition(RemoteSessionState.Disconnected);
                throw new OperationCanceledException(LastError.Message, exception, cancellationToken);
            }
            catch (TimeoutException exception)
            {
                LastError = new RemoteError(
                    RemoteErrorCode.NetworkInterrupted,
                    "SSH disconnect did not complete in time; the local socket was closed.",
                    exception.Message);
                Transition(RemoteSessionState.Disconnected);
            }
            catch (Exception exception)
            {
                LastError = SshRemoteErrorMapper.Map(
                    exception,
                    _lease,
                    timedOut: false,
                    callerCancelled: false);
                Transition(RemoteSessionState.Faulted);
                throw new RemoteSessionException(LastError, exception);
            }
            finally
            {
                DisposeCurrentLease();
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
        StopConnectionMonitor();
        await _stateGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            DisposeCurrentLease();
            State = RemoteSessionState.Disconnected;
        }
        finally
        {
            _stateGate.Release();
            _stateGate.Dispose();
        }
    }

    private void ClientOnErrorOccurred(object? sender, ExceptionEventArgs eventArgs)
    {
        if (State != RemoteSessionState.Connected)
        {
            return;
        }

        LastError = SshRemoteErrorMapper.Map(
            eventArgs.Exception,
            _lease,
            timedOut: false,
            callerCancelled: false);
        Transition(RemoteSessionState.Faulted);
    }

    private void StartConnectionMonitor(SshClient client)
    {
        StopConnectionMonitor();
        _monitorCancellation = new CancellationTokenSource();
        _monitorTask = MonitorConnectionAsync(client, _monitorCancellation.Token);
    }

    private async Task MonitorConnectionAsync(SshClient client, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.ConnectionMonitorInterval, cancellationToken).ConfigureAwait(false);
                if (State != RemoteSessionState.Connected)
                {
                    return;
                }

                if (!client.IsConnected)
                {
                    LastError = new RemoteError(
                        RemoteErrorCode.NetworkInterrupted,
                        "The SSH connection was closed by the server or network.");
                    Transition(RemoteSessionState.Disconnected);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception exception)
        {
            if (State == RemoteSessionState.Connected)
            {
                LastError = SshRemoteErrorMapper.Map(
                    exception,
                    _lease,
                    timedOut: false,
                    callerCancelled: false);
                Transition(RemoteSessionState.Faulted);
            }
        }
    }

    private void StopConnectionMonitor()
    {
        _monitorCancellation?.Cancel();
        _monitorCancellation?.Dispose();
        _monitorCancellation = null;
        _monitorTask = null;
    }

    private void DisposeCurrentLease()
    {
        StopConnectionMonitor();
        if (_lease is null)
        {
            return;
        }

        _lease.Client.ErrorOccurred -= ClientOnErrorOccurred;
        _lease.Dispose();
        _lease = null;
    }

    private void Transition(RemoteSessionState state)
    {
        if (State == state)
        {
            return;
        }

        State = state;
        StateChanged?.Invoke(state);
    }

    private static RemoteError CreateCancellationError() =>
        new(RemoteErrorCode.OperationCancelled, "SSH connection operation was cancelled.");

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal static class SshRemoteErrorMapper
{
    public static RemoteError Map(
        Exception exception,
        SshClientLease? lease,
        bool timedOut,
        bool callerCancelled)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is RemoteSessionException sessionException)
        {
            return sessionException.Error;
        }

        if (callerCancelled ||
            exception is OperationCanceledException && !timedOut ||
            lease?.HostTrustBridgeException is OperationCanceledException ||
            lease?.InteractiveAuthenticationCancelled == true)
        {
            return new RemoteError(
                RemoteErrorCode.OperationCancelled,
                "SSH connection operation was cancelled.");
        }

        if (lease?.HostTrustFailure is { } hostTrustFailure)
        {
            return hostTrustFailure.Outcome switch
            {
                HostTrustOutcome.RejectedUnknown => new RemoteError(
                    RemoteErrorCode.HostKeyUnknown,
                    "The SSH server identity was not trusted."),
                HostTrustOutcome.RejectedChangedKey or HostTrustOutcome.RejectedChangedKeyAndForgotten => new RemoteError(
                    RemoteErrorCode.HostKeyMismatch,
                    "The SSH server identity does not match the saved host key. The connection was blocked."),
                _ => new RemoteError(
                    RemoteErrorCode.ConnectionFailed,
                    "SSH host verification failed."),
            };
        }

        if (lease?.HostTrustBridgeException is { } hostTrustException)
        {
            return new RemoteError(
                RemoteErrorCode.ConnectionFailed,
                "ServerDesk could not complete SSH host verification.",
                $"{hostTrustException.GetType().Name}: {hostTrustException.Message}");
        }

        if (lease?.InteractiveAuthenticationException is { } interactiveException)
        {
            return new RemoteError(
                RemoteErrorCode.AuthenticationFailed,
                "Interactive SSH authentication could not be completed.",
                $"{interactiveException.GetType().Name}: {interactiveException.Message}");
        }

        if (timedOut)
        {
            return new RemoteError(
                RemoteErrorCode.ConnectionFailed,
                "SSH connection timed out before a secure session was established.");
        }

        return exception switch
        {
            SshAuthenticationException => new RemoteError(
                RemoteErrorCode.AuthenticationFailed,
                "SSH authentication failed. Check the username and configured credential."),
            SocketException => new RemoteError(
                RemoteErrorCode.ConnectionFailed,
                "ServerDesk could not reach the SSH server.",
                exception.Message),
            SshConnectionException => new RemoteError(
                RemoteErrorCode.ConnectionFailed,
                "The SSH session could not be established.",
                exception.Message),
            IOException => new RemoteError(
                RemoteErrorCode.NetworkInterrupted,
                "The SSH connection was interrupted.",
                exception.Message),
            ObjectDisposedException => new RemoteError(
                RemoteErrorCode.NetworkInterrupted,
                "The SSH connection was closed unexpectedly."),
            _ => new RemoteError(
                RemoteErrorCode.ConnectionFailed,
                "ServerDesk could not establish the SSH connection.",
                $"{exception.GetType().Name}: {exception.Message}"),
        };
    }
}
