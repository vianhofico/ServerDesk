using System.Security.Cryptography;
using System.Text;
using ServerDesk.Application.Agent;
using ServerDesk.Domain.Operations;
using Xunit;

namespace ServerDesk.Tests;

public sealed class AgentReleaseSecurityTests
{
    [Fact]
    public void CanonicalManifestIsDeterministicAndFieldOrdered()
    {
        var manifest = Manifest("1.2.3", "x64", 7, new string('a', 64), 1700000000);

        var first = AgentReleaseManifestCanonicalizer.Write(manifest);
        var second = AgentReleaseManifestCanonicalizer.Write(manifest);
        var text = Encoding.UTF8.GetString(first);

        Assert.Equal(first, second);
        Assert.StartsWith("schema=1\nproduct=serverdesk-agent\nversion=1.2.3\n", text, StringComparison.Ordinal);
        Assert.EndsWith("released-unix-seconds=1700000000\n", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidPinnedSignatureAndArtifactProduceVerifiedTypes()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var artifact = Encoding.UTF8.GetBytes("certified-agent-binary");
        var envelope = Sign(signingKey, ManifestForArtifact("2.1.0", "x64", artifact));
        var verifier = Verifier(signingKey);

        var verifiedManifest = verifier.VerifyManifest(envelope, DateTimeOffset.UtcNow);
        var verifiedArtifact = verifier.VerifyArtifact(verifiedManifest, artifact);

        Assert.Equal(new AgentReleaseVersion(2, 1, 0), verifiedManifest.Version);
        Assert.Equal(AgentProtocolVersion.Current.Major, verifiedManifest.Protocol.Major);
        Assert.Equal("x64", verifiedManifest.Architecture);
        Assert.Equal(artifact.Length, verifiedArtifact.Length);
    }

    [Fact]
    public void TamperingAuthenticatedFieldAfterSigningFailsClosed()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var artifact = Encoding.UTF8.GetBytes("agent");
        var signed = Sign(signingKey, ManifestForArtifact("1.0.0", "x64", artifact));
        var tampered = signed with { Manifest = signed.Manifest with { Version = "9.9.9" } };

        var exception = Assert.Throws<AgentReleaseVerificationException>(() =>
            Verifier(signingKey).VerifyManifest(tampered, DateTimeOffset.UtcNow));

        Assert.Contains("signature", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownSigningKeyFailsBeforeManifestIsTrusted()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var artifact = Encoding.UTF8.GetBytes("agent");
        var signed = Sign(signingKey, ManifestForArtifact("1.0.0", "x64", artifact)) with { KeyId = "unknown-key" };

        var exception = Assert.Throws<AgentReleaseVerificationException>(() =>
            Verifier(signingKey).VerifyManifest(signed, DateTimeOffset.UtcNow));

        Assert.Contains("not trusted", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("windows", "x64")]
    [InlineData("linux", "x86")]
    public void AuthenticatedUnsupportedPlatformOrArchitectureFailsClosed(string platform, string architecture)
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var artifact = Encoding.UTF8.GetBytes("agent");
        var manifest = ManifestForArtifact("1.0.0", architecture, artifact) with
        {
            Platform = platform,
            ArtifactFileName = $"serverdesk-agent-{platform}-{architecture}",
        };
        var signed = Sign(signingKey, manifest);

        Assert.Throws<AgentReleaseVerificationException>(() =>
            Verifier(signingKey).VerifyManifest(signed, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void ArtifactLengthAndDigestMustMatchAuthenticatedManifest()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var artifact = Encoding.UTF8.GetBytes("agent-v1");
        var verifier = Verifier(signingKey);
        var manifest = verifier.VerifyManifest(
            Sign(signingKey, ManifestForArtifact("1.0.0", "x64", artifact)),
            DateTimeOffset.UtcNow);

        Assert.Throws<AgentReleaseVerificationException>(() => verifier.VerifyArtifact(manifest, Encoding.UTF8.GetBytes("short")));

        var sameLengthDifferentBytes = Encoding.UTF8.GetBytes("agent-v2");
        var exception = Assert.Throws<AgentReleaseVerificationException>(() => verifier.VerifyArtifact(manifest, sameLengthDifferentBytes));
        Assert.Contains("digest", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FutureTimestampAndMalformedCanonicalVersionFailClosedAfterValidSignature()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var artifact = Encoding.UTF8.GetBytes("agent");
        var now = DateTimeOffset.UtcNow;
        var future = ManifestForArtifact("01.0.0", "x64", artifact) with
        {
            ReleasedUnixSeconds = now.AddMinutes(10).ToUnixTimeSeconds(),
        };

        var exception = Assert.Throws<AgentReleaseVerificationException>(() => Verifier(signingKey).VerifyManifest(Sign(signingKey, future), now));
        Assert.True(
            exception.Message.Contains("version", StringComparison.OrdinalIgnoreCase) ||
            exception.Message.Contains("future", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void InstallAndUninstallPlansContainOnlyFixedAgentOwnedResources()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var artifactBytes = Encoding.UTF8.GetBytes("agent");
        var verifier = Verifier(signingKey);
        var verifiedManifest = verifier.VerifyManifest(Sign(signingKey, ManifestForArtifact("2.0.0", "arm64", artifactBytes)), DateTimeOffset.UtcNow);
        var verifiedArtifact = verifier.VerifyArtifact(verifiedManifest, artifactBytes);

        var install = AgentLifecyclePlanner.PlanInstall(verifiedArtifact);
        var uninstall = AgentLifecyclePlanner.PlanUninstall(new AgentReleaseVersion(2, 0, 0));
        var expectedPaths = new[]
        {
            AgentLifecyclePlanner.BinaryPath,
            AgentLifecyclePlanner.StateDirectory,
            AgentLifecyclePlanner.CacheDirectory,
            AgentLifecyclePlanner.ServiceUnitPath,
        };

        Assert.Equal(AgentLifecyclePlanner.ServiceUnit, install.ServiceUnit);
        Assert.Equal(expectedPaths, install.OwnedResources.Select(resource => resource.Path));
        Assert.Equal(expectedPaths, uninstall.OwnedResources.Select(resource => resource.Path));
        Assert.DoesNotContain(install.OwnedResources, resource => resource.Path.Contains("ssh", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(install.OwnedResources, resource => resource.Path.Contains("firewall", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(install.Steps, step => step.Risk == OperationRisk.Destructive);
        Assert.All(install.Steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Verification)));
        Assert.All(uninstall.Steps, step => Assert.False(string.IsNullOrWhiteSpace(step.Verification)));
    }

    [Fact]
    public void UpdatePlanRejectsDowngradeAndSameVersionReplacement()
    {
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var artifactBytes = Encoding.UTF8.GetBytes("agent");
        var verifier = Verifier(signingKey);
        var manifest = verifier.VerifyManifest(Sign(signingKey, ManifestForArtifact("2.0.0", "x64", artifactBytes)), DateTimeOffset.UtcNow);
        var artifact = verifier.VerifyArtifact(manifest, artifactBytes);

        Assert.Throws<InvalidOperationException>(() => AgentLifecyclePlanner.PlanUpdate(new AgentReleaseVersion(2, 0, 0), artifact));
        Assert.Throws<InvalidOperationException>(() => AgentLifecyclePlanner.PlanUpdate(new AgentReleaseVersion(3, 0, 0), artifact));

        var upgrade = AgentLifecyclePlanner.PlanUpdate(new AgentReleaseVersion(1, 9, 9), artifact);
        Assert.Equal(AgentLifecycleOperation.Update, upgrade.Operation);
        Assert.Equal(new AgentReleaseVersion(2, 0, 0), upgrade.TargetVersion);
    }

    [Fact]
    public void SigningContractContainsNoPrivateKeyMaterial()
    {
        var envelopeProperties = typeof(SignedAgentReleaseManifest).GetProperties().Select(property => property.Name).ToArray();
        var trustStoreProperties = typeof(AgentReleaseTrustStore).GetProperties().Select(property => property.Name).ToArray();

        Assert.DoesNotContain(envelopeProperties, name => name.Contains("Private", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(trustStoreProperties, name => name.Contains("Private", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(typeof(AgentLifecyclePlanner).Assembly.GetReferencedAssemblies(), reference =>
            (reference.Name ?? string.Empty).Contains("Grpc", StringComparison.OrdinalIgnoreCase));
    }

    private static AgentReleaseVerifier Verifier(ECDsa signingKey)
    {
        var publicKey = signingKey.ExportSubjectPublicKeyInfo();
        return new AgentReleaseVerifier(new AgentReleaseTrustStore(new Dictionary<string, byte[]>
        {
            ["test-release-key"] = publicKey,
        }));
    }

    private static SignedAgentReleaseManifest Sign(ECDsa key, AgentReleaseManifestDocument manifest)
    {
        var payload = AgentReleaseManifestCanonicalizer.Write(manifest);
        var signature = key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        return new SignedAgentReleaseManifest(manifest, "test-release-key", signature);
    }

    private static AgentReleaseManifestDocument ManifestForArtifact(string version, string architecture, byte[] artifact) =>
        Manifest(
            version,
            architecture,
            artifact.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(artifact)),
            DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds());

    private static AgentReleaseManifestDocument Manifest(
        string version,
        string architecture,
        long length,
        string digest,
        long releasedUnixSeconds) =>
        new(
            1,
            "serverdesk-agent",
            version,
            AgentProtocolVersion.Current.Major,
            AgentProtocolVersion.Current.Minor,
            "linux",
            architecture,
            $"serverdesk-agent-linux-{architecture}",
            length,
            digest,
            releasedUnixSeconds);
}
