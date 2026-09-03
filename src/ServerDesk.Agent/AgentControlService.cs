using Grpc.Core;
using ServerDesk.Agent.Contracts.V1;

namespace ServerDesk.Agent;

public sealed class AgentControlService : AgentControl.AgentControlBase
{
    private readonly AgentRuntimeInfo _runtimeInfo;

    public AgentControlService(AgentRuntimeInfo runtimeInfo)
    {
        _runtimeInfo = runtimeInfo ?? throw new ArgumentNullException(nameof(runtimeInfo));
    }

    public override Task<NegotiateResponse> Negotiate(
        NegotiateRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        var response = new NegotiateResponse
        {
            Protocol = new ProtocolVersion
            {
                Major = 1,
                Minor = 0,
            },
            AgentVersion = _runtimeInfo.Version,
            Platform = _runtimeInfo.Platform,
            Architecture = _runtimeInfo.Architecture,
        };

        // Streaming capabilities are introduced by later M8 slices only after their bounded APIs exist.
        return Task.FromResult(response);
    }

    public override Task<HealthResponse> Health(
        HealthRequest request,
        ServerCallContext context)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Task.FromResult(new HealthResponse
        {
            State = (AgentHealthState)1,
            AgentVersion = _runtimeInfo.Version,
            StartedUnixMs = _runtimeInfo.StartedAtUtc.ToUnixTimeMilliseconds(),
        });
    }
}
