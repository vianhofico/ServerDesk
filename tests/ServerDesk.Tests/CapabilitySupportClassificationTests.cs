using ServerDesk.Application.Capabilities;
using Xunit;

namespace ServerDesk.Tests;

public sealed class CapabilitySupportClassificationTests
{
    public static TheoryData<CapabilityState, CapabilitySupportClassification> Cases =>
        new()
        {
            {
                CapabilityState.Available("1.0"),
                CapabilitySupportClassification.Available
            },
            {
                CapabilityState.Unavailable("docker was not found in PATH."),
                CapabilitySupportClassification.Absent
            },
            {
                CapabilityState.Unavailable("The Docker CLI does not provide the Compose plugin."),
                CapabilitySupportClassification.Unsupported
            },
            {
                CapabilityState.PermissionDenied("nginx exists but cannot be executed by the current user."),
                CapabilitySupportClassification.PermissionDenied
            },
            {
                CapabilityState.Unknown("git exists but its version probe returned exit code 2."),
                CapabilitySupportClassification.Unknown
            },
        };

    [Theory]
    [MemberData(nameof(Cases))]
    public void ClassifierKeepsCertificationStatesDistinct(
        CapabilityState state,
        CapabilitySupportClassification expected)
    {
        Assert.Equal(expected, CapabilitySupportClassifier.Classify(state));
    }

    [Theory]
    [InlineData("unsupported feature")]
    [InlineData("feature is not supported")]
    [InlineData("client does not support this operation")]
    [InlineData("unknown command: compose")]
    [InlineData("compose is not a docker command")]
    public void KnownUnsupportedResponsesAreNeverCollapsedIntoAbsent(string detail)
    {
        var state = CapabilityState.Unavailable(detail);

        Assert.Equal(
            CapabilitySupportClassification.Unsupported,
            CapabilitySupportClassifier.Classify(state));
    }
}
