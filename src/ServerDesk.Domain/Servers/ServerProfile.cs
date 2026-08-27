using ServerDesk.Domain.Secrets;

namespace ServerDesk.Domain.Servers;

public sealed record ServerProfile
{
    private ServerProfile(
        Guid id,
        string name,
        string host,
        int port,
        string username,
        string? environment,
        SecretReference? credentialReference,
        ServerAuthenticationKind authenticationKind,
        string? privateKeyPath)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Server profile id cannot be empty.", nameof(id));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        if (!Enum.IsDefined(authenticationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(authenticationKind));
        }

        Id = id;
        Name = name.Trim();
        Host = host.Trim();
        Port = port;
        Username = username.Trim();
        Environment = string.IsNullOrWhiteSpace(environment) ? null : environment.Trim();
        CredentialReference = credentialReference;
        AuthenticationKind = authenticationKind;
        PrivateKeyPath = authenticationKind == ServerAuthenticationKind.PrivateKey && !string.IsNullOrWhiteSpace(privateKeyPath)
            ? privateKeyPath.Trim()
            : null;
    }

    public Guid Id { get; }

    public string Name { get; }

    public string Host { get; }

    public int Port { get; }

    public string Username { get; }

    public string? Environment { get; }

    public SecretReference? CredentialReference { get; }

    public ServerAuthenticationKind AuthenticationKind { get; }

    public string? PrivateKeyPath { get; }

    public static ServerProfile Create(
        string name,
        string host,
        int port,
        string username,
        string? environment = null,
        SecretReference? credentialReference = null,
        ServerAuthenticationKind authenticationKind = ServerAuthenticationKind.Password,
        string? privateKeyPath = null) =>
        Create(
            Guid.NewGuid(),
            name,
            host,
            port,
            username,
            environment,
            credentialReference,
            authenticationKind,
            privateKeyPath);

    public static ServerProfile Create(
        Guid id,
        string name,
        string host,
        int port,
        string username,
        string? environment = null,
        SecretReference? credentialReference = null,
        ServerAuthenticationKind authenticationKind = ServerAuthenticationKind.Password,
        string? privateKeyPath = null) =>
        new(
            id,
            name,
            host,
            port,
            username,
            environment,
            credentialReference,
            authenticationKind,
            privateKeyPath);

    public static ServerProfile Rehydrate(
        Guid id,
        string name,
        string host,
        int port,
        string username,
        string? environment,
        SecretReference? credentialReference,
        ServerAuthenticationKind authenticationKind = ServerAuthenticationKind.Password,
        string? privateKeyPath = null) =>
        new(
            id,
            name,
            host,
            port,
            username,
            environment,
            credentialReference,
            authenticationKind,
            privateKeyPath);
}
