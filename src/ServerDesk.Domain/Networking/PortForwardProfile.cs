namespace ServerDesk.Domain.Networking;

public enum PortForwardKind
{
    Local,
    Remote,
    Dynamic,
}

public sealed class PortForwardProfile
{
    private PortForwardProfile(
        Guid id,
        Guid serverProfileId,
        string name,
        PortForwardKind kind,
        string bindHost,
        int bindPort,
        string? destinationHost,
        int? destinationPort)
    {
        Id = id;
        ServerProfileId = serverProfileId;
        Name = name;
        Kind = kind;
        BindHost = bindHost;
        BindPort = bindPort;
        DestinationHost = destinationHost;
        DestinationPort = destinationPort;
    }

    public Guid Id { get; }

    public Guid ServerProfileId { get; }

    public string Name { get; }

    public PortForwardKind Kind { get; }

    public string BindHost { get; }

    public int BindPort { get; }

    public string? DestinationHost { get; }

    public int? DestinationPort { get; }

    public bool UsesLocalListener => Kind is PortForwardKind.Local or PortForwardKind.Dynamic;

    public bool UsesAutomaticPort => BindPort == 0;

    public static PortForwardProfile Create(
        Guid serverProfileId,
        string name,
        PortForwardKind kind,
        string bindHost,
        int bindPort,
        string? destinationHost = null,
        int? destinationPort = null) =>
        Create(
            Guid.NewGuid(),
            serverProfileId,
            name,
            kind,
            bindHost,
            bindPort,
            destinationHost,
            destinationPort);

    public static PortForwardProfile Create(
        Guid id,
        Guid serverProfileId,
        string name,
        PortForwardKind kind,
        string bindHost,
        int bindPort,
        string? destinationHost = null,
        int? destinationPort = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Port-forward profile id cannot be empty.", nameof(id));
        }

        if (serverProfileId == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(serverProfileId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        name = NormalizeRequired(name, nameof(name), 100);
        bindHost = NormalizeRequired(bindHost, nameof(bindHost), 255);
        if (IsWildcardBind(bindHost))
        {
            throw new ArgumentException(
                "Wildcard tunnel binds are disabled in this release. Bind to loopback or an explicit interface address.",
                nameof(bindHost));
        }

        if (bindPort is < 0 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(bindPort), "Bind port must be 0 (automatic) or between 1 and 65535.");
        }

        if (kind == PortForwardKind.Dynamic)
        {
            if (!string.IsNullOrWhiteSpace(destinationHost) || destinationPort is not null)
            {
                throw new ArgumentException("Dynamic/SOCKS forwarding does not use a fixed destination endpoint.");
            }

            return new PortForwardProfile(id, serverProfileId, name, kind, bindHost, bindPort, null, null);
        }

        destinationHost = NormalizeRequired(destinationHost, nameof(destinationHost), 255);
        if (destinationPort is null or < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(destinationPort),
                "Destination port must be between 1 and 65535 for local or remote forwarding.");
        }

        return new PortForwardProfile(
            id,
            serverProfileId,
            name,
            kind,
            bindHost,
            bindPort,
            destinationHost,
            destinationPort);
    }

    public static PortForwardProfile Rehydrate(
        Guid id,
        Guid serverProfileId,
        string name,
        PortForwardKind kind,
        string bindHost,
        int bindPort,
        string? destinationHost,
        int? destinationPort) =>
        Create(id, serverProfileId, name, kind, bindHost, bindPort, destinationHost, destinationPort);

    public bool ConflictsWith(PortForwardProfile other)
    {
        ArgumentNullException.ThrowIfNull(other);
        if (BindPort == 0 || other.BindPort == 0)
        {
            return false;
        }

        if (!string.Equals(BindHost, other.BindHost, StringComparison.OrdinalIgnoreCase) || BindPort != other.BindPort)
        {
            return false;
        }

        if (UsesLocalListener && other.UsesLocalListener)
        {
            return true;
        }

        return Kind == PortForwardKind.Remote &&
               other.Kind == PortForwardKind.Remote &&
               ServerProfileId == other.ServerProfileId;
    }

    private static string NormalizeRequired(string? value, string parameterName, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength)
        {
            throw new ArgumentException($"Value cannot exceed {maximumLength} characters.", parameterName);
        }

        if (normalized.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Value cannot contain NUL characters.", parameterName);
        }

        return normalized;
    }

    private static bool IsWildcardBind(string host) =>
        host is "*" or "0.0.0.0" or "::" or "[::]";
}
