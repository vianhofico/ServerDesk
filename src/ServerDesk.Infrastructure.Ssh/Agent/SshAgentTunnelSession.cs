using ServerDesk.Application.Agent;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.PortForwarding;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Networking;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Infrastructure.Ssh.Agent;

public sealed class SshAgentTunnelSessionFactory : IAgentTunnelSessionFactory
{
    private readonly IPortForwardSessionFactory _forwardSessionFactory;

    public SshAgentTunnelSessionFactory(IPortForwardSessionFactory forwardSessionFactory)
    {
        _forwardSessionFactory = forwardSessionFactory ?? throw new ArgumentNullException(nameof(forwardSessionFactory));
    }

    public SshAgentTunnelSessionFactory(
        ISecretStore secretStore,
        IHostTrustService hostTrustService,
        IInteractiveAuthenticationPrompt interactivePrompt,
        SshSessionOptions options)
        : this(new SshPortForwardSessionFactory(secretStore, hostTrustService, interactivePrompt, options))
    {
    }

    public IAgentTunnelSession Create(ServerProfile serverProfile, int agentPort)
    {
        ArgumentNullException.ThrowIfNull(serverProfile);
        if (agentPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(agentPort));
        }

        var transientForward = PortForwardProfile.Create(
            serverProfile.Id,
            "ServerDesk agent tunnel",
            PortForwardKind.Local,
            "127.0.0.1",
            0,
            "127.0.0.1",
            agentPort);
        return new SshAgentTunnelSession(_forwardSessionFactory.Create(serverProfile, transientForward));
    }

    private sealed class SshAgentTunnelSession : IAgentTunnelSession
    {
        private readonly IPortForwardSession _forwardSession;

        public SshAgentTunnelSession(IPortForwardSession forwardSession)
        {
            _forwardSession = forwardSession ?? throw new ArgumentNullException(nameof(forwardSession));
        }

        public AgentTunnelState State => MapState(_forwardSession.State);

        public int LocalPort => State == AgentTunnelState.Active ? _forwardSession.BoundPort : 0;

        public async ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            await _forwardSession.StartAsync(cancellationToken).ConfigureAwait(false);
            if (_forwardSession.State != PortForwardSessionState.Active || _forwardSession.BoundPort is < 1 or > 65535)
            {
                throw new InvalidOperationException("SSH agent tunnel did not become active on a local port.");
            }
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default) =>
            _forwardSession.StopAsync(cancellationToken);

        public ValueTask DisposeAsync() => _forwardSession.DisposeAsync();

        private static AgentTunnelState MapState(PortForwardSessionState state) =>
            state switch
            {
                PortForwardSessionState.Created => AgentTunnelState.Created,
                PortForwardSessionState.Starting => AgentTunnelState.Starting,
                PortForwardSessionState.Active => AgentTunnelState.Active,
                PortForwardSessionState.Stopping => AgentTunnelState.Stopping,
                PortForwardSessionState.Stopped => AgentTunnelState.Stopped,
                PortForwardSessionState.Faulted => AgentTunnelState.Faulted,
                _ => AgentTunnelState.Faulted,
            };
    }
}
