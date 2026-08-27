using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Terminal;

public enum TerminalSessionState
{
    Created,
    Connecting,
    Connected,
    Disconnecting,
    Disconnected,
    Faulted,
}

public readonly record struct TerminalSize
{
    public TerminalSize(uint columns, uint rows, uint pixelWidth = 0, uint pixelHeight = 0)
    {
        if (columns is < 2 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(columns), "Terminal columns must be between 2 and 1000.");
        }

        if (rows is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(rows), "Terminal rows must be between 1 and 1000.");
        }

        if (pixelWidth > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelWidth));
        }

        if (pixelHeight > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelHeight));
        }

        Columns = columns;
        Rows = rows;
        PixelWidth = pixelWidth;
        PixelHeight = pixelHeight;
    }

    public uint Columns { get; }

    public uint Rows { get; }

    public uint PixelWidth { get; }

    public uint PixelHeight { get; }

    public static TerminalSize Default => new(120, 30);
}

public sealed class TerminalSessionException : Exception
{
    public TerminalSessionException(RemoteError error, Exception? innerException = null)
        : base(error?.Message, innerException)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public RemoteError Error { get; }
}

public interface IRemoteTerminalSession : IAsyncDisposable
{
    Guid ServerProfileId { get; }

    TerminalSessionState State { get; }

    RemoteError? LastError { get; }

    event Action<TerminalSessionState>? StateChanged;

    event Action<string>? OutputReceived;

    ValueTask ConnectAsync(
        TerminalSize initialSize,
        CancellationToken cancellationToken = default);

    ValueTask SendAsync(
        string input,
        CancellationToken cancellationToken = default);

    ValueTask ResizeAsync(
        TerminalSize size,
        CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);
}

public interface IRemoteTerminalSessionFactory
{
    IRemoteTerminalSession Create(ServerProfile profile);
}
