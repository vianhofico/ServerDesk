using System.Diagnostics;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Infrastructure.Ssh;

public sealed class SshRemoteCommandExecutorFactory : IRemoteCommandExecutorFactory
{
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;

    public SshRemoteCommandExecutorFactory(
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

    public IRemoteCommandExecutor Create(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new SshRemoteCommandExecutor(profile, _clientFactory, _options);
    }
}

internal sealed class SshRemoteCommandExecutor : IRemoteCommandExecutor
{
    private readonly ServerProfile _profile;
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SshClientLease? _lease;
    private bool _disposed;

    public SshRemoteCommandExecutor(
        ServerProfile profile,
        SshClientFactory clientFactory,
        SshSessionOptions options)
    {
        _profile = profile;
        _clientFactory = clientFactory;
        _options = options;
    }

    public Guid ServerProfileId => _profile.Id;

    public async Task<RemoteExecutionResult> ExecuteAsync(
        RemoteCommandSpec command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ThrowIfDisposed();
        ValidateCommand(command);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            var lease = _lease ?? throw new InvalidOperationException("SSH command connection is unavailable.");
            var commandText = PosixCommandLine.Build(command);
            using var sshCommand = lease.Client.CreateCommand(commandText, Encoding.UTF8);
            sshCommand.CommandTimeout = command.Timeout;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                await sshCommand.ExecuteAsync(cancellationToken).ConfigureAwait(false);
                stopwatch.Stop();
                return RemoteExecutionResult.Success(new RemoteCommandResult(
                    sshCommand.ExitStatus ?? -1,
                    sshCommand.Result ?? string.Empty,
                    sshCommand.Error ?? string.Empty,
                    stopwatch.Elapsed));
            }
            catch (OperationCanceledException)
            {
                stopwatch.Stop();
                return RemoteExecutionResult.Failure(new RemoteError(
                    RemoteErrorCode.OperationCancelled,
                    "Remote command execution was cancelled."));
            }
            catch (SshOperationTimeoutException exception)
            {
                stopwatch.Stop();
                return RemoteExecutionResult.Failure(new RemoteError(
                    RemoteErrorCode.CommandTimeout,
                    "Remote command exceeded its configured timeout.",
                    exception.Message));
            }
            catch (Exception exception)
            {
                stopwatch.Stop();
                return RemoteExecutionResult.Failure(MapCommandError(exception, lease));
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            DisposeLease();
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async ValueTask EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_lease?.Client.IsConnected == true)
        {
            return;
        }

        DisposeLease();
        using var timeoutCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCancellation.CancelAfter(_options.ConnectTimeout);
        SshClientLease? lease = null;
        try
        {
            lease = await _clientFactory.CreateAsync(
                    _profile,
                    SshChannelPurpose.Command,
                    timeoutCancellation.Token)
                .ConfigureAwait(false);
            await lease.Client.ConnectAsync(timeoutCancellation.Token).ConfigureAwait(false);
            if (!lease.Client.IsConnected)
            {
                throw new SshConnectionException("SSH command channel could not establish a secure connection.");
            }

            _lease = lease;
        }
        catch (Exception exception)
        {
            var timedOut = !cancellationToken.IsCancellationRequested && timeoutCancellation.IsCancellationRequested;
            var error = SshRemoteErrorMapper.Map(
                exception,
                lease,
                timedOut,
                cancellationToken.IsCancellationRequested);
            lease?.Dispose();
            throw new RemoteCommandConnectionException(error, exception);
        }
    }

    private void DisposeLease()
    {
        _lease?.Dispose();
        _lease = null;
    }

    private static void ValidateCommand(RemoteCommandSpec command)
    {
        if (string.IsNullOrWhiteSpace(command.Executable))
        {
            throw new ArgumentException("Remote command executable cannot be empty.", nameof(command));
        }

        if (command.Executable.Contains('\0', StringComparison.Ordinal) ||
            command.Arguments.Any(argument => argument.Contains('\0', StringComparison.Ordinal)))
        {
            throw new ArgumentException("Remote command tokens cannot contain NUL characters.", nameof(command));
        }

        if (command.Timeout <= TimeSpan.Zero || command.Timeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(command), "Remote command timeout must be between 1 millisecond and 5 minutes.");
        }

        if (command.Environment is not null)
        {
            foreach (var name in command.Environment.Keys)
            {
                if (!PosixCommandLine.IsEnvironmentName(name))
                {
                    throw new ArgumentException($"Invalid environment variable name '{name}'.", nameof(command));
                }
            }
        }
    }

    private static RemoteError MapCommandError(Exception exception, SshClientLease lease)
    {
        var transport = SshRemoteErrorMapper.Map(
            exception,
            lease,
            timedOut: false,
            callerCancelled: false);
        return transport.Code switch
        {
            RemoteErrorCode.AuthenticationFailed or
            RemoteErrorCode.HostKeyUnknown or
            RemoteErrorCode.HostKeyMismatch or
            RemoteErrorCode.OperationCancelled => transport,
            _ => new RemoteError(
                RemoteErrorCode.CommandFailed,
                "ServerDesk could not execute the remote command.",
                $"{exception.GetType().Name}: {exception.Message}"),
        };
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal sealed class RemoteCommandConnectionException : Exception
{
    public RemoteCommandConnectionException(RemoteError error, Exception innerException)
        : base(error.Message, innerException)
    {
        Error = error;
    }

    public RemoteError Error { get; }
}

internal static class PosixCommandLine
{
    public static string Build(RemoteCommandSpec command)
    {
        var invocation = new StringBuilder();
        if (command.Environment is { Count: > 0 })
        {
            invocation.Append("env");
            foreach (var pair in command.Environment.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                invocation.Append(' ')
                    .Append(pair.Key)
                    .Append('=')
                    .Append(Quote(pair.Value));
            }

            invocation.Append(' ');
        }

        invocation.Append(Quote(command.Executable));
        foreach (var argument in command.Arguments)
        {
            invocation.Append(' ').Append(Quote(argument));
        }

        if (string.IsNullOrWhiteSpace(command.WorkingDirectory))
        {
            return invocation.ToString();
        }

        return $"cd -- {Quote(command.WorkingDirectory)} && {invocation}";
    }

    public static string Quote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";

    public static bool IsEnvironmentName(string value)
    {
        if (string.IsNullOrEmpty(value) || !(char.IsAsciiLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!(char.IsAsciiLetterOrDigit(character) || character == '_'))
            {
                return false;
            }
        }

        return true;
    }
}
