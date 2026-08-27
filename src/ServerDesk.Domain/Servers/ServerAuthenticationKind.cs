namespace ServerDesk.Domain.Servers;

public enum ServerAuthenticationKind
{
    Password = 0,
    PrivateKey = 1,
    SshAgent = 2,
    KeyboardInteractive = 3,
}
