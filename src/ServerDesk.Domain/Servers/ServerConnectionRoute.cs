using ServerDesk.Domain.Secrets;

namespace ServerDesk.Domain.Servers;

public enum ServerRouteKind
{
    Direct = 0,
    HttpProxy = 1,
    Socks4Proxy = 2,
    Socks5Proxy = 3,
    Bastion = 4,
}

public sealed record ServerConnectionRoute
{
    private ServerConnectionRoute(
        ServerRouteKind kind,
        string? proxyHost,
        int? proxyPort,
        string? proxyUsername,
        SecretReference? proxyCredentialReference,
        Guid? bastionProfileId)
    {
        Kind = kind;
        ProxyHost = proxyHost;
        ProxyPort = proxyPort;
        ProxyUsername = proxyUsername;
        ProxyCredentialReference = proxyCredentialReference;
        BastionProfileId = bastionProfileId;
    }

    public ServerRouteKind Kind { get; }

    public string? ProxyHost { get; }

    public int? ProxyPort { get; }

    public string? ProxyUsername { get; }

    public SecretReference? ProxyCredentialReference { get; }

    public Guid? BastionProfileId { get; }

    public bool IsProxy => Kind is ServerRouteKind.HttpProxy or ServerRouteKind.Socks4Proxy or ServerRouteKind.Socks5Proxy;

    public static ServerConnectionRoute Direct { get; } =
        new(ServerRouteKind.Direct, null, null, null, null, null);

    public static ServerConnectionRoute Proxy(
        ServerRouteKind kind,
        string host,
        int port,
        string? username = null,
        SecretReference? credentialReference = null)
    {
        if (kind is not ServerRouteKind.HttpProxy and
            not ServerRouteKind.Socks4Proxy and
            not ServerRouteKind.Socks5Proxy)
        {
            throw new ArgumentOutOfRangeException(nameof(kind), "Route kind must be an HTTP, SOCKS4 or SOCKS5 proxy.");
        }

        host = NormalizeHost(host, nameof(host));
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Proxy port must be between 1 and 65535.");
        }

        username = NormalizeOptional(username, 128, nameof(username));
        if (credentialReference is not null && string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("A proxy password cannot be stored without a proxy username.", nameof(credentialReference));
        }

        return new ServerConnectionRoute(kind, host, port, username, credentialReference, null);
    }

    public static ServerConnectionRoute Bastion(Guid bastionProfileId)
    {
        if (bastionProfileId == Guid.Empty)
        {
            throw new ArgumentException("Bastion profile id cannot be empty.", nameof(bastionProfileId));
        }

        return new ServerConnectionRoute(ServerRouteKind.Bastion, null, null, null, null, bastionProfileId);
    }

    public static ServerConnectionRoute Rehydrate(
        ServerRouteKind kind,
        string? proxyHost,
        int? proxyPort,
        string? proxyUsername,
        SecretReference? proxyCredentialReference,
        Guid? bastionProfileId) =>
        kind switch
        {
            ServerRouteKind.Direct => Direct,
            ServerRouteKind.HttpProxy or ServerRouteKind.Socks4Proxy or ServerRouteKind.Socks5Proxy =>
                Proxy(
                    kind,
                    proxyHost ?? throw new ArgumentException("Proxy host is required for a proxy route."),
                    proxyPort ?? throw new ArgumentException("Proxy port is required for a proxy route."),
                    proxyUsername,
                    proxyCredentialReference),
            ServerRouteKind.Bastion => Bastion(
                bastionProfileId ?? throw new ArgumentException("Bastion profile is required for a bastion route.")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    private static string NormalizeHost(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Host is required.", parameterName);
        }

        var normalized = value.Trim();
        if (normalized.Length > 255 || normalized.Any(char.IsWhiteSpace) || normalized.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Host must be 255 characters or fewer and cannot contain whitespace or NUL.", parameterName);
        }

        return normalized;
    }

    private static string? NormalizeOptional(string? value, int maximumLength, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        if (normalized.Length > maximumLength || normalized.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException($"Value must be {maximumLength} characters or fewer and cannot contain NUL.", parameterName);
        }

        return normalized;
    }
}
