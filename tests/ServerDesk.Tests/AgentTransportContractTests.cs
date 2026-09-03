using ServerDesk.Application.Agent;
using Xunit;

namespace ServerDesk.Tests;

public sealed class AgentTransportContractTests
{
    [Fact]
    public void MajorProtocolMismatchIsExplicitlyIncompatible()
    {
        var peer = Peer(new AgentProtocolVersion(2, 0), AgentCapability.MetricsStreaming);
        var result = AgentCompatibilityPolicy.Evaluate(AgentProtocolVersion.Current, AgentCompatibilityPolicy.KnownCapabilities, peer);
        Assert.Equal(AgentConnectionState.Incompatible, result.State);
        Assert.Empty(result.Capabilities);
    }

    [Fact]
    public void CompatibleNegotiationUsesOnlyKnownMutualCapabilities()
    {
        var unknown = (AgentCapability)999;
        var peer = Peer(new AgentProtocolVersion(1, 7), AgentCapability.MetricsStreaming, AgentCapability.ProcessEvents, unknown);
        var result = AgentCompatibilityPolicy.Evaluate(
            AgentProtocolVersion.Current,
            [AgentCapability.MetricsStreaming, AgentCapability.ProcessEvents, AgentCapability.ServiceEvents, unknown],
            peer);
        Assert.Equal(AgentConnectionState.Available, result.State);
        Assert.Equal(2, result.Capabilities.Count);
        Assert.Contains(AgentCapability.MetricsStreaming, result.Capabilities);
        Assert.Contains(AgentCapability.ProcessEvents, result.Capabilities);
        Assert.Equal(AgentConnectionState.Unsupported, result.GetCapabilityState(AgentCapability.ServiceEvents));
        Assert.DoesNotContain(unknown, result.Capabilities);
    }

    [Fact]
    public void VersionDoesNotInferUnadvertisedCapability()
    {
        var result = AgentCompatibilityPolicy.Evaluate(
            AgentProtocolVersion.Current,
            AgentCompatibilityPolicy.KnownCapabilities,
            Peer(new AgentProtocolVersion(1, 99), AgentCapability.MetricsStreaming));
        Assert.Equal(AgentConnectionState.Unsupported, result.GetCapabilityState(AgentCapability.LogStreaming));
    }

    [Fact]
    public void MissingAgentVersionFailsNegotiationWithoutCapabilities()
    {
        var result = AgentCompatibilityPolicy.Evaluate(
            AgentProtocolVersion.Current,
            AgentCompatibilityPolicy.KnownCapabilities,
            new AgentPeerInfo(AgentProtocolVersion.Current, " ", new HashSet<AgentCapability> { AgentCapability.MetricsStreaming }));
        Assert.Equal(AgentConnectionState.Failed, result.State);
        Assert.Empty(result.Capabilities);
    }

    [Fact]
    public void ApplicationAssemblyDoesNotReferenceGrpcOrProtobuf()
    {
        var references = typeof(AgentProtocolVersion).Assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty).ToArray();
        Assert.DoesNotContain(references, name => name.Contains("Grpc", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.Contains("Protobuf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WireSchemaContainsOnlyBoundedReadOnlyAgentSurface()
    {
        var schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Agent", "agent_control.proto"));
        Assert.Contains("rpc Negotiate", schema, StringComparison.Ordinal);
        Assert.Contains("rpc Health", schema, StringComparison.Ordinal);
        Assert.Contains("rpc StreamMetrics", schema, StringComparison.Ordinal);
        Assert.Contains("rpc StreamProcessEvents", schema, StringComparison.Ordinal);
        Assert.Contains("rpc StreamServiceEvents", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("rpc Execute", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rpc Run", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passphrase", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private_key", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mutation", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("command", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("argument", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("environment", schema, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentPeerInfo Peer(AgentProtocolVersion protocol, params AgentCapability[] capabilities) =>
        new(protocol, "1.0.0-test", new HashSet<AgentCapability>(capabilities), "linux", "x64");
}
