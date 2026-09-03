using ServerDesk.Application.Agent;
using Xunit;

namespace ServerDesk.Tests;

public sealed class AgentTransportContractTests
{
    [Fact]
    public void MajorProtocolMismatchIsExplicitlyIncompatible()
    {
        var peer = Peer(new AgentProtocolVersion(2, 0), AgentCapability.MetricsStreaming);

        var result = AgentCompatibilityPolicy.Evaluate(
            AgentProtocolVersion.Current,
            AgentCompatibilityPolicy.KnownCapabilities,
            peer);

        Assert.Equal(AgentConnectionState.Incompatible, result.State);
        Assert.Empty(result.Capabilities);
        Assert.Equal(AgentConnectionState.Incompatible, result.GetCapabilityState(AgentCapability.MetricsStreaming));
    }

    [Fact]
    public void CompatibleNegotiationUsesOnlyKnownMutualCapabilities()
    {
        var unknown = (AgentCapability)999;
        var peer = Peer(
            new AgentProtocolVersion(1, 7),
            AgentCapability.MetricsStreaming,
            AgentCapability.DockerEvents,
            unknown);

        var result = AgentCompatibilityPolicy.Evaluate(
            AgentProtocolVersion.Current,
            [AgentCapability.MetricsStreaming, AgentCapability.ProcessEvents, unknown],
            peer);

        Assert.Equal(AgentConnectionState.Available, result.State);
        var capability = Assert.Single(result.Capabilities);
        Assert.Equal(AgentCapability.MetricsStreaming, capability);
        Assert.Equal(AgentConnectionState.Available, result.GetCapabilityState(AgentCapability.MetricsStreaming));
        Assert.Equal(AgentConnectionState.Unsupported, result.GetCapabilityState(AgentCapability.ProcessEvents));
        Assert.DoesNotContain(unknown, result.Capabilities);
    }

    [Fact]
    public void VersionDoesNotInferUnadvertisedCapability()
    {
        var peer = Peer(new AgentProtocolVersion(1, 99), AgentCapability.MetricsStreaming);

        var result = AgentCompatibilityPolicy.Evaluate(
            AgentProtocolVersion.Current,
            AgentCompatibilityPolicy.KnownCapabilities,
            peer);

        Assert.Equal(AgentConnectionState.Unsupported, result.GetCapabilityState(AgentCapability.LogStreaming));
    }

    [Fact]
    public void MissingAgentVersionFailsNegotiationWithoutCapabilities()
    {
        var peer = new AgentPeerInfo(
            AgentProtocolVersion.Current,
            " ",
            new HashSet<AgentCapability> { AgentCapability.MetricsStreaming });

        var result = AgentCompatibilityPolicy.Evaluate(
            AgentProtocolVersion.Current,
            AgentCompatibilityPolicy.KnownCapabilities,
            peer);

        Assert.Equal(AgentConnectionState.Failed, result.State);
        Assert.Empty(result.Capabilities);
    }

    [Fact]
    public void ApplicationAssemblyDoesNotReferenceGrpcOrProtobuf()
    {
        var references = typeof(AgentProtocolVersion).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name => name.Contains("Grpc", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(references, name => name.Contains("Protobuf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void WireSchemaContainsOnlyNegotiationAndHealthControlSurface()
    {
        var schemaPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Agent", "agent_control.proto");
        var schema = File.ReadAllText(schemaPath);

        Assert.Contains("service AgentControl", schema, StringComparison.Ordinal);
        Assert.Contains("rpc Negotiate", schema, StringComparison.Ordinal);
        Assert.Contains("rpc Health", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("rpc Execute", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("rpc Run", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("credential", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("passphrase", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private_key", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("mutation", schema, StringComparison.OrdinalIgnoreCase);
    }

    private static AgentPeerInfo Peer(AgentProtocolVersion protocol, params AgentCapability[] capabilities) =>
        new(
            protocol,
            "1.0.0-test",
            new HashSet<AgentCapability>(capabilities),
            "linux",
            "x64");
}
