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

public interface IRemoteSession : IAsyncDisposable
{
    Guid ServerProfileId { get; }

    RemoteSessionState State { get; }

    event Action<RemoteSessionState>? StateChanged;

    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IRemoteSessionFactory
{
    IRemoteSession Create(ServerProfile profile);
}
