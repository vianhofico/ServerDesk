namespace ServerDesk.Domain.Servers;

public sealed record ServerProfile(
    Guid Id,
    string Name,
    string Host,
    int Port,
    string Username,
    string? Environment,
    string? CredentialReference)
{
    public static ServerProfile Create(
        string name,
        string host,
        int port,
        string username,
        string? environment = null,
        string? credentialReference = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        return new ServerProfile(
            Guid.NewGuid(),
            name.Trim(),
            host.Trim(),
            port,
            username.Trim(),
            string.IsNullOrWhiteSpace(environment) ? null : environment.Trim(),
            credentialReference);
    }
}
