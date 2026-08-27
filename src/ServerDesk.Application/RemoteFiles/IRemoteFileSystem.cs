using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.RemoteFiles;

public readonly record struct RemotePath
{
    private RemotePath(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public bool IsAbsolute => Value.StartsWith("/", StringComparison.Ordinal);

    public string Name
    {
        get
        {
            if (Value is "/" or ".")
            {
                return Value;
            }

            var separator = Value.LastIndexOf('/');
            return separator < 0 ? Value : Value[(separator + 1)..];
        }
    }

    public RemotePath Parent
    {
        get
        {
            if (Value is "/" or ".")
            {
                return this;
            }

            var separator = Value.LastIndexOf('/');
            if (separator < 0)
            {
                return Parse(".");
            }

            return separator == 0
                ? Parse("/")
                : Parse(Value[..separator]);
        }
    }

    public static RemotePath Parse(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (path.Contains('\0'))
        {
            throw new ArgumentException("Remote paths cannot contain NUL characters.", nameof(path));
        }

        var absolute = path.StartsWith("/", StringComparison.Ordinal);
        var segments = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw new ArgumentException(
                        "Remote path traversal cannot escape the supplied root.",
                        nameof(path));
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        if (absolute)
        {
            return new RemotePath(segments.Count == 0 ? "/" : "/" + string.Join('/', segments));
        }

        return new RemotePath(segments.Count == 0 ? "." : string.Join('/', segments));
    }

    public RemotePath Combine(string childName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(childName);
        if (childName is "." or ".." || childName.Contains('/') || childName.Contains('\0'))
        {
            throw new ArgumentException("Child name must be a single remote path segment.", nameof(childName));
        }

        return Value switch
        {
            "/" => Parse("/" + childName),
            "." => Parse(childName),
            _ => Parse(Value + '/' + childName),
        };
    }

    public override string ToString() => Value;
}

public readonly record struct RemoteUnixPermissions
{
    private RemoteUnixPermissions(short mode)
    {
        Mode = mode;
    }

    public short Mode { get; }

    public static RemoteUnixPermissions FromMode(short mode)
    {
        var remaining = mode;
        for (var index = 0; index < 4; index++)
        {
            var digit = remaining % 10;
            if (digit is < 0 or > 7)
            {
                throw new ArgumentOutOfRangeException(nameof(mode), "Permission mode must contain only octal digits.");
            }

            remaining /= 10;
        }

        if (remaining != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(mode), "Permission mode may contain at most four octal digits.");
        }

        return new RemoteUnixPermissions(mode);
    }

    public override string ToString() => Mode.ToString("000", System.Globalization.CultureInfo.InvariantCulture);
}

public enum RemoteFileKind
{
    File,
    Directory,
    SymbolicLink,
    Other,
}

public sealed record RemoteFileEntry(
    RemotePath Path,
    string Name,
    RemoteFileKind Kind,
    long Size,
    DateTimeOffset? LastWriteTimeUtc,
    int? UserId,
    int? GroupId,
    RemoteUnixPermissions Permissions);

public enum RemoteTransferDirection
{
    Upload,
    Download,
}

public sealed record RemoteTransferProgress(
    RemoteTransferDirection Direction,
    long BytesTransferred,
    long? TotalBytes);

public sealed class RemoteFileSystemException : Exception
{
    public RemoteFileSystemException(RemoteError error, Exception? innerException = null)
        : base(error?.Message, innerException)
    {
        Error = error ?? throw new ArgumentNullException(nameof(error));
    }

    public RemoteError Error { get; }
}

public interface IRemoteFileSystem : IAsyncDisposable
{
    Guid ServerProfileId { get; }

    bool IsConnected { get; }

    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(
        RemotePath path,
        CancellationToken cancellationToken = default);

    ValueTask<RemoteFileEntry> StatAsync(
        RemotePath path,
        CancellationToken cancellationToken = default);

    ValueTask CreateDirectoryAsync(
        RemotePath path,
        CancellationToken cancellationToken = default);

    ValueTask RenameAsync(
        RemotePath source,
        RemotePath destination,
        bool overwrite = false,
        CancellationToken cancellationToken = default);

    ValueTask DeleteFileAsync(
        RemotePath path,
        CancellationToken cancellationToken = default);

    ValueTask DeleteDirectoryAsync(
        RemotePath path,
        CancellationToken cancellationToken = default);

    ValueTask SetPermissionsAsync(
        RemotePath path,
        RemoteUnixPermissions permissions,
        CancellationToken cancellationToken = default);

    ValueTask UploadAsync(
        Stream source,
        RemotePath destination,
        long? totalBytes = null,
        bool overwrite = false,
        IProgress<RemoteTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);

    ValueTask DownloadAsync(
        RemotePath source,
        Stream destination,
        IProgress<RemoteTransferProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface IRemoteFileSystemFactory
{
    IRemoteFileSystem Create(ServerProfile profile);
}
