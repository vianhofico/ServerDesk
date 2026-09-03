using ServerDesk.Application.Agent;

namespace ServerDesk.Infrastructure.Ssh.Agent;

public sealed class GrpcAgentTransportClientFactory : IAgentTransportClientFactory
{
    public IAgentTransportClient Create(int localPort) => new GrpcAgentTransportClient(localPort);
}
