using ServerDesk.Application.HostTrust;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Routing;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Application.Terminal;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Infrastructure.Ssh;

public sealed class RouteAwareRemoteSessionFactory : IRemoteSessionFactory
{
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;

    public RouteAwareRemoteSessionFactory(
        ISecretStore secretStore,
        IHostTrustService hostTrustService,
        IInteractiveAuthenticationPrompt interactivePrompt,
        SshSessionOptions options,
        IConnectionRouteRepository routeRepository,
        IProfileRepository profileRepository)
    {
        _options = options;
        _clientFactory = new SshClientFactory(
            secretStore,
            hostTrustService,
            interactivePrompt,
            options,
            routeRepository,
            profileRepository);
    }

    public IRemoteSession Create(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new SshRemoteSession(profile, _clientFactory, _options);
    }
}

public sealed class RouteAwareRemoteCommandExecutorFactory : IRemoteCommandExecutorFactory
{
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;

    public RouteAwareRemoteCommandExecutorFactory(
        ISecretStore secretStore,
        IHostTrustService hostTrustService,
        IInteractiveAuthenticationPrompt interactivePrompt,
        SshSessionOptions options,
        IConnectionRouteRepository routeRepository,
        IProfileRepository profileRepository)
    {
        _options = options;
        _clientFactory = new SshClientFactory(
            secretStore,
            hostTrustService,
            interactivePrompt,
            options,
            routeRepository,
            profileRepository);
    }

    public IRemoteCommandExecutor Create(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new SshRemoteCommandExecutor(profile, _clientFactory, _options);
    }
}

public sealed class RouteAwareRemoteTerminalSessionFactory : IRemoteTerminalSessionFactory
{
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;

    public RouteAwareRemoteTerminalSessionFactory(
        ISecretStore secretStore,
        IHostTrustService hostTrustService,
        IInteractiveAuthenticationPrompt interactivePrompt,
        SshSessionOptions options,
        IConnectionRouteRepository routeRepository,
        IProfileRepository profileRepository)
    {
        _options = options;
        _clientFactory = new SshClientFactory(
            secretStore,
            hostTrustService,
            interactivePrompt,
            options,
            routeRepository,
            profileRepository);
    }

    public IRemoteTerminalSession Create(ServerProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return new SshRemoteTerminalSession(profile, _clientFactory, _options);
    }
}

public sealed class RouteAwarePortForwardSessionFactory : IPortForwardSessionFactory
{
    private readonly SshClientFactory _clientFactory;
    private readonly SshSessionOptions _options;

    public RouteAwarePortForwardSessionFactory(
        ISecretStore secretStore,
        IHostTrustService hostTrustService,
        IInteractiveAuthenticationPrompt interactivePrompt,
        SshSessionOptions options,
        IConnectionRouteRepository routeRepository,
        IProfileRepository profileRepository)
    {
        _options = options;
        _clientFactory = new SshClientFactory(
            secretStore,
            hostTrustService,
            interactivePrompt,
            options,
            routeRepository,
            profileRepository);
    }

    public IPortForwardSession Create(ServerProfile serverProfile, PortForwardProfile forwardProfile)
    {
        ArgumentNullException.ThrowIfNull(serverProfile);
        ArgumentNullException.ThrowIfNull(forwardProfile);
        if (serverProfile.Id != forwardProfile.ServerProfileId)
        {
            throw new ArgumentException("Port-forward profile belongs to a different server.", nameof(forwardProfile));
        }

        return new SshPortForwardSession(serverProfile, forwardProfile, _clientFactory, _options);
    }
}
