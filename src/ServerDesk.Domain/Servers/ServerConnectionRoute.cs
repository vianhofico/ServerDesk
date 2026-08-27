using ServerDesk.Domain.Secrets;

namespace ServerDesk.Domain.Servers;

public enum ServerConnectionRouteKind
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
        Guid serverProfileId,
        ServerConnectionRouteKind kind,
        string? proxyHost,
        int? proxyPort,
        string? proxyUsername,
        SecretReference? proxyCredentialReference,
        Guid? bastionProfileId)
    {
        if (serverProfileId == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(serverProfileId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ServerProfileId = serverProfileId;
        Kind = kind;

        if (IsProxyKind(kind))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(proxyHost);
            if (proxyHost.Any(char.IsWhiteSpace))
            {
                throw new ArgumentException("Proxy host cannot contain whitespace.", nameof(proxyHost));
            }

            if (proxyPort is null or < 1 or > 65535)
            {
                throw new ArgumentOutOfRangeException(nameof(proxyPort), "Proxy port must be between 1 and 65535.");
            }

            ProxyHost = proxyHost.Trim();
            ProxyPort = proxyPort.Value;
            ProxyUsername = string.IsNullOrWhiteSpace(proxyUsername) ? null : proxyUsername.Trim();
            ProxyCredentialReference = proxyCredentialReference;
            BastionProfileId = null;
            return;
        }

        if (kind == ServerConnectionRouteKind.Bastion)
        {
            if (bastionProfileId is null || bastionProfileId == Guid.Empty)
            {
                throw new ArgumentException("Bastion profile id is required.", nameof(bastionProfileId));
            }

            if (bastionProfileId == serverProfileId)
            {
                throw new ArgumentException("A server profile cannot use itself as a bastion.", nameof(bastionProfileId));
            }

            BastionProfileId = bastionProfileId;
            ProxyHost = null;
            ProxyPort = null;
            ProxyUsername = null;
            ProxyCredentialReference = null;
            return;
        }

        ProxyHost = null;
        ProxyPort = null;
        ProxyUsername = null;
        ProxyCredentialReference = null;
        BastionProfileId = null;
    }

    public Guid ServerProfileId { get; }

    public ServerConnectionRouteKind Kind { get; }

    public string? ProxyHost { get; }

    public int? ProxyPort { get; }

    public string? ProxyUsername { get; }

    public SecretReference? ProxyCredentialReference { get; }

    public Guid? BastionProfileId { get; }

    public bool IsDirect => Kind == ServerConnectionRouteKind.Direct;

    public bool IsProxy => IsProxyKind(Kind);

    public bool IsBastion => Kind == ServerConnectionRouteKind.Bastion;

    public static ServerConnectionRoute Direct(Guid serverProfileId) =>
        new(serverProfileId, ServerConnectionRouteKind.Direct, null, null, null, null, null);

    public static ServerConnectionRoute Proxy(
        Guid serverProfileId,
        ServerConnectionRouteKind kind,
        string proxyHost,
        int proxyPort,
        string? proxyUsername = null,
        SecretReference? proxyCredentialReference = null)
    {
        if (!IsProxyKind(kind))
        {
            throw new ArgumentException("Route kind must be an HTTP, SOCKS4 or SOCKS5 proxy.", nameof(kind));
        }

        return new ServerConnectionRoute(
            serverProfileId,
            kind,
            proxyHost,
            proxyPort,
            proxyUsername,
            proxyCredentialReference,
            null);
    }

    public static ServerConnectionRoute Bastion(Guid serverProfileId, Guid bastionProfileId) =>
        new(serverProfileId, ServerConnectionRouteKind.Bastion, null, null, null, null, bastionProfileId);

    public static ServerConnectionRoute Rehydrate(
        Guid serverProfileId,
        ServerConnectionRouteKind kind,
        string? proxyHost,
        int? proxyPort,
        string? proxyUsername,
        SecretReference? proxyCredentialReference,
        Guid? bastionProfileId) =>
        new(
            serverProfileId,
            kind,
            proxyHost,
            proxyPort,
            proxyUsername,
            proxyCredentialReference,
            bastionProfileId);

    private static bool IsProxyKind(ServerConnectionRouteKind kind) =>
        kind is ServerConnectionRouteKind.HttpProxy or
            ServerConnectionRouteKind.Socks4Proxy or
            ServerConnectionRouteKind.Socks5Proxy;
}
