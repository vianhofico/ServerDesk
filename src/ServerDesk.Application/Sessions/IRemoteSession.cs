using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Sessions;

public enum RemoteSessionState
{
    Created,
    Connecting,
    Connected,
    Reconnecting,
    Disconnecting,
    Disconnected,
    Faulted,
}

public sealed class RemoteSessionException : Exception
{
    public RemoteSessionException(RemoteError error, Exception? innerException = null)
        : base(error?.Message, innerException)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public RemoteError Error { get; }
}

public interface IRemoteSession : IAsyncDisposable
{
    Guid ServerProfileId { get; }

    RemoteSessionState State { get; }

    RemoteError? LastError { get; }

    string? ServerVersion { get; }

    DateTimeOffset? ConnectedAtUtc { get; }

    event Action<RemoteSessionState>? StateChanged;

    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IRemoteSessionFactory
{
    IRemoteSession Create(ServerProfile profile);
}
