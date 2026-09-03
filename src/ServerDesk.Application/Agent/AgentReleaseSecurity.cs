using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ServerDesk.Domain.Operations;

namespace ServerDesk.Application.Agent;

public readonly record struct AgentReleaseVersion(int Major, int Minor, int Patch) : IComparable<AgentReleaseVersion>
{
    public static bool TryParse(string? value, out AgentReleaseVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts.Any(part => !IsCanonicalPart(part)))
        {
            return false;
        }

        if (!int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        version = new AgentReleaseVersion(major, minor, patch);
        return true;
    }

    public int CompareTo(AgentReleaseVersion other)
    {
        var major = Major.CompareTo(other.Major);
        if (major != 0)
        {
            return major;
        }

        var minor = Minor.CompareTo(other.Minor);
        return minor != 0 ? minor : Patch.CompareTo(other.Patch);
    }

    public override string ToString() => FormattableString.Invariant($"{Major}.{Minor}.{Patch}");

    private static bool IsCanonicalPart(string value) =>
        value.Length > 0 &&
        (value.Length == 1 || value[0] != '0') &&
        value.All(char.IsAsciiDigit);
}

public sealed record AgentReleaseManifestDocument(
    int SchemaVersion,
    string Product,
    string Version,
    int ProtocolMajor,
    int ProtocolMinor,
    string Platform,
    string Architecture,
    string ArtifactFileName,
    long ArtifactLength,
    string ArtifactSha256,
    long ReleasedUnixSeconds);

public sealed record SignedAgentReleaseManifest(
    AgentReleaseManifestDocument Manifest,
    string KeyId,
    byte[] Signature);

public sealed class AgentReleaseTrustStore
{
    private readonly IReadOnlyDictionary<string, byte[]> _keys;

    public AgentReleaseTrustStore(IReadOnlyDictionary<string, byte[]> subjectPublicKeyInfoById)
    {
        ArgumentNullException.ThrowIfNull(subjectPublicKeyInfoById);
        if (subjectPublicKeyInfoById.Count is < 1 or > 16)
        {
            throw new ArgumentException("Agent release trust store must contain between 1 and 16 pinned public keys.", nameof(subjectPublicKeyInfoById));
        }

        var copy = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var pair in subjectPublicKeyInfoById)
        {
            ValidateKeyId(pair.Key);
            if (pair.Value is null || pair.Value.Length is < 64 or > 512)
            {
                throw new ArgumentException("Pinned agent release public keys must be bounded DER SubjectPublicKeyInfo values.", nameof(subjectPublicKeyInfoById));
            }

            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(pair.Value, out var bytesRead);
            if (bytesRead != pair.Value.Length || key.KeySize != 256)
            {
                throw new ArgumentException("Pinned agent release keys must be ECDSA P-256 SubjectPublicKeyInfo values.", nameof(subjectPublicKeyInfoById));
            }

            copy.Add(pair.Key, pair.Value.ToArray());
        }

        _keys = copy;
    }

    internal bool TryGet(string keyId, out byte[] subjectPublicKeyInfo)
    {
        if (_keys.TryGetValue(keyId, out var value))
        {
            subjectPublicKeyInfo = value.ToArray();
            return true;
        }

        subjectPublicKeyInfo = [];
        return false;
    }

    internal static void ValidateKeyId(string keyId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        if (keyId.Length > 64 || !keyId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            throw new ArgumentException("Agent release key id must be a bounded ASCII identifier.", nameof(keyId));
        }
    }
}

public sealed class VerifiedAgentReleaseManifest
{
    internal VerifiedAgentReleaseManifest(
        AgentReleaseVersion version,
        AgentProtocolVersion protocol,
        string architecture,
        string artifactFileName,
        long artifactLength,
        string artifactSha256,
        DateTimeOffset releasedAtUtc,
        string keyId)
    {
        Version = version;
        Protocol = protocol;
        Architecture = architecture;
        ArtifactFileName = artifactFileName;
        ArtifactLength = artifactLength;
        ArtifactSha256 = artifactSha256;
        ReleasedAtUtc = releasedAtUtc;
        KeyId = keyId;
    }

    public AgentReleaseVersion Version { get; }

    public AgentProtocolVersion Protocol { get; }

    public string Architecture { get; }

    public string ArtifactFileName { get; }

    public long ArtifactLength { get; }

    public string ArtifactSha256 { get; }

    public DateTimeOffset ReleasedAtUtc { get; }

    public string KeyId { get; }
}

public sealed class VerifiedAgentArtifact
{
    internal VerifiedAgentArtifact(VerifiedAgentReleaseManifest manifest, byte[] content)
    {
        Manifest = manifest;
        Content = content;
    }

    public VerifiedAgentReleaseManifest Manifest { get; }

    internal byte[] Content { get; }

    public long Length => Content.LongLength;
}

public sealed class AgentReleaseVerificationException : Exception
{
    public AgentReleaseVerificationException(string message)
        : base(message)
    {
    }
}

public sealed class AgentReleaseVerifier
{
    public const int CurrentSchemaVersion = 1;
    public const string ProductId = "serverdesk-agent";
    public const string PlatformId = "linux";
    public const long MaximumArtifactLength = 256L * 1024 * 1024;
    public static TimeSpan MaximumFutureClockSkew { get; } = TimeSpan.FromMinutes(5);

    private readonly AgentReleaseTrustStore _trustStore;

    public AgentReleaseVerifier(AgentReleaseTrustStore trustStore)
    {
        _trustStore = trustStore ?? throw new ArgumentNullException(nameof(trustStore));
    }

    public VerifiedAgentReleaseManifest VerifyManifest(SignedAgentReleaseManifest envelope, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(envelope.Manifest);
        ArgumentNullException.ThrowIfNull(envelope.Signature);
        AgentReleaseTrustStore.ValidateKeyId(envelope.KeyId);
        if (!_trustStore.TryGet(envelope.KeyId, out var subjectPublicKeyInfo))
        {
            throw new AgentReleaseVerificationException("Agent release signing key is not trusted.");
        }

        var canonical = AgentReleaseManifestCanonicalizer.Write(envelope.Manifest);
        var verified = false;
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(subjectPublicKeyInfo, out var bytesRead);
            if (bytesRead != subjectPublicKeyInfo.Length || key.KeySize != 256)
            {
                throw new AgentReleaseVerificationException("Pinned agent release key is invalid.");
            }

            verified = key.VerifyData(
                canonical,
                envelope.Signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (CryptographicException exception)
        {
            throw new AgentReleaseVerificationException($"Agent release signature verification failed: {exception.GetType().Name}.");
        }

        if (!verified)
        {
            throw new AgentReleaseVerificationException("Agent release manifest signature is invalid.");
        }

        return ValidateAuthenticatedManifest(envelope.Manifest, envelope.KeyId, nowUtc);
    }

    public VerifiedAgentArtifact VerifyArtifact(VerifiedAgentReleaseManifest manifest, ReadOnlySpan<byte> artifact)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        if (artifact.Length != manifest.ArtifactLength)
        {
            throw new AgentReleaseVerificationException("Agent artifact length does not match the authenticated manifest.");
        }

        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(artifact, digest);
        var actual = Convert.ToHexStringLower(digest);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(actual),
                Encoding.ASCII.GetBytes(manifest.ArtifactSha256)))
        {
            throw new AgentReleaseVerificationException("Agent artifact digest does not match the authenticated manifest.");
        }

        return new VerifiedAgentArtifact(manifest, artifact.ToArray());
    }

    private static VerifiedAgentReleaseManifest ValidateAuthenticatedManifest(
        AgentReleaseManifestDocument manifest,
        string keyId,
        DateTimeOffset nowUtc)
    {
        if (manifest.SchemaVersion != CurrentSchemaVersion)
        {
            throw new AgentReleaseVerificationException("Agent release manifest schema is unsupported.");
        }

        if (!string.Equals(manifest.Product, ProductId, StringComparison.Ordinal))
        {
            throw new AgentReleaseVerificationException("Agent release product id is invalid.");
        }

        if (!AgentReleaseVersion.TryParse(manifest.Version, out var version))
        {
            throw new AgentReleaseVerificationException("Agent release version is not a canonical three-part version.");
        }

        if (manifest.ProtocolMajor != AgentProtocolVersion.Current.Major || manifest.ProtocolMinor < 0)
        {
            throw new AgentReleaseVerificationException("Agent release protocol is incompatible with this ServerDesk build.");
        }

        if (!string.Equals(manifest.Platform, PlatformId, StringComparison.Ordinal))
        {
            throw new AgentReleaseVerificationException("Agent release platform must be linux.");
        }

        if (manifest.Architecture is not ("x64" or "arm64"))
        {
            throw new AgentReleaseVerificationException("Agent release architecture is unsupported.");
        }

        var expectedFileName = $"serverdesk-agent-linux-{manifest.Architecture}";
        if (!string.Equals(manifest.ArtifactFileName, expectedFileName, StringComparison.Ordinal))
        {
            throw new AgentReleaseVerificationException("Agent artifact file name is not the fixed name for the authenticated architecture.");
        }

        if (manifest.ArtifactLength is < 1 or > MaximumArtifactLength)
        {
            throw new AgentReleaseVerificationException("Agent artifact length is outside the allowed range.");
        }

        if (manifest.ArtifactSha256.Length != 64 ||
            manifest.ArtifactSha256.Any(character => !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
        {
            throw new AgentReleaseVerificationException("Agent artifact SHA-256 must be 64 lowercase hexadecimal characters.");
        }

        DateTimeOffset releasedAtUtc;
        try
        {
            releasedAtUtc = DateTimeOffset.FromUnixTimeSeconds(manifest.ReleasedUnixSeconds);
        }
        catch (ArgumentOutOfRangeException exception)
        {
            throw new AgentReleaseVerificationException($"Agent release timestamp is invalid: {exception.GetType().Name}.");
        }

        if (releasedAtUtc > nowUtc.ToUniversalTime() + MaximumFutureClockSkew)
        {
            throw new AgentReleaseVerificationException("Agent release timestamp is too far in the future.");
        }

        return new VerifiedAgentReleaseManifest(
            version,
            new AgentProtocolVersion(manifest.ProtocolMajor, manifest.ProtocolMinor),
            manifest.Architecture,
            manifest.ArtifactFileName,
            manifest.ArtifactLength,
            manifest.ArtifactSha256,
            releasedAtUtc,
            keyId);
    }
}

internal static class AgentReleaseManifestCanonicalizer
{
    public static byte[] Write(AgentReleaseManifestDocument manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var builder = new StringBuilder(512);
        Append(builder, "schema", manifest.SchemaVersion.ToString(CultureInfo.InvariantCulture));
        Append(builder, "product", manifest.Product);
        Append(builder, "version", manifest.Version);
        Append(builder, "protocol-major", manifest.ProtocolMajor.ToString(CultureInfo.InvariantCulture));
        Append(builder, "protocol-minor", manifest.ProtocolMinor.ToString(CultureInfo.InvariantCulture));
        Append(builder, "platform", manifest.Platform);
        Append(builder, "architecture", manifest.Architecture);
        Append(builder, "artifact-file", manifest.ArtifactFileName);
        Append(builder, "artifact-length", manifest.ArtifactLength.ToString(CultureInfo.InvariantCulture));
        Append(builder, "artifact-sha256", manifest.ArtifactSha256);
        Append(builder, "released-unix-seconds", manifest.ReleasedUnixSeconds.ToString(CultureInfo.InvariantCulture));
        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private static void Append(StringBuilder builder, string key, string? value)
    {
        if (value is null || value.Length > 512 || value.Any(character => character is '\r' or '\n' or '\0'))
        {
            throw new AgentReleaseVerificationException($"Agent release manifest field '{key}' is malformed.");
        }

        builder.Append(key).Append('=').Append(value).Append('\n');
    }
}

public enum AgentLifecycleOperation
{
    Install,
    Update,
    Uninstall,
}

public enum AgentOwnedResourceKind
{
    Binary,
    StateDirectory,
    CacheDirectory,
    ServiceUnit,
}

public sealed record AgentOwnedResource(AgentOwnedResourceKind Kind, string Path);

public sealed record AgentLifecyclePlanStep(
    int Sequence,
    OperationRisk Risk,
    string Action,
    string Verification);

public sealed record AgentLifecyclePlan(
    Guid PlanId,
    AgentLifecycleOperation Operation,
    AgentReleaseVersion? CurrentVersion,
    AgentReleaseVersion? TargetVersion,
    string ServiceUnit,
    IReadOnlyList<AgentOwnedResource> OwnedResources,
    IReadOnlyList<AgentLifecyclePlanStep> Steps);

public static class AgentLifecyclePlanner
{
    public const string ServiceUnit = "serverdesk-agent.service";
    public const string BinaryPath = "/opt/serverdesk-agent/serverdesk-agent";
    public const string StateDirectory = "/var/lib/serverdesk-agent";
    public const string CacheDirectory = "/var/cache/serverdesk-agent";
    public const string ServiceUnitPath = "/etc/systemd/system/serverdesk-agent.service";

    private static readonly IReadOnlyList<AgentOwnedResource> FixedOwnedResources =
    [
        new(AgentOwnedResourceKind.Binary, BinaryPath),
        new(AgentOwnedResourceKind.StateDirectory, StateDirectory),
        new(AgentOwnedResourceKind.CacheDirectory, CacheDirectory),
        new(AgentOwnedResourceKind.ServiceUnit, ServiceUnitPath),
    ];

    public static AgentLifecyclePlan PlanInstall(VerifiedAgentArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        return new AgentLifecyclePlan(
            Guid.NewGuid(),
            AgentLifecycleOperation.Install,
            null,
            artifact.Manifest.Version,
            ServiceUnit,
            CopyOwnedResources(),
            [
                Step(1, OperationRisk.Mutating, "Stage verified agent artifact in agent-owned cache.", "Remote staged SHA-256 and length must match authenticated manifest."),
                Step(2, OperationRisk.Mutating, "Install agent binary and fixed systemd unit in agent-owned paths.", "Installed binary digest and unit identity must be re-read."),
                Step(3, OperationRisk.Mutating, "Reload systemd and enable the fixed agent service.", "Unit must be loaded and enabled."),
                Step(4, OperationRisk.Destructive, "Start the fixed agent service.", "Service must be active and tunneled health/version negotiation must match the target release."),
            ]);
    }

    public static AgentLifecyclePlan PlanUpdate(AgentReleaseVersion currentVersion, VerifiedAgentArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        if (artifact.Manifest.Version.CompareTo(currentVersion) <= 0)
        {
            throw new InvalidOperationException("Agent update target must be newer than the currently installed version; downgrade and same-version replacement are disabled.");
        }

        return new AgentLifecyclePlan(
            Guid.NewGuid(),
            AgentLifecycleOperation.Update,
            currentVersion,
            artifact.Manifest.Version,
            ServiceUnit,
            CopyOwnedResources(),
            [
                Step(1, OperationRisk.Mutating, "Stage verified replacement artifact in agent-owned cache.", "Remote staged SHA-256 and length must match authenticated manifest."),
                Step(2, OperationRisk.Destructive, "Replace only the agent-owned binary after preserving the current binary for bounded rollback.", "Installed binary digest must match the authenticated target manifest."),
                Step(3, OperationRisk.Destructive, "Restart the fixed agent service.", "Service must be active and tunneled health/version negotiation must match the target release."),
                Step(4, OperationRisk.Mutating, "Remove the bounded previous-binary rollback copy after successful health verification.", "No stale rollback binary may remain after a successful update."),
            ]);
    }

    public static AgentLifecyclePlan PlanUninstall(AgentReleaseVersion? currentVersion = null) =>
        new(
            Guid.NewGuid(),
            AgentLifecycleOperation.Uninstall,
            currentVersion,
            null,
            ServiceUnit,
            CopyOwnedResources(),
            [
                Step(1, OperationRisk.Destructive, "Stop and disable only the fixed agent service if present.", "The fixed unit must be inactive/disabled or absent."),
                Step(2, OperationRisk.Destructive, "Remove only the fixed agent service unit, binary, state directory and cache directory.", "Each agent-owned resource must be absent; unrelated server resources are untouched."),
                Step(3, OperationRisk.Mutating, "Reload systemd after removing the fixed unit.", "The fixed unit must no longer be loadable."),
            ]);

    private static IReadOnlyList<AgentOwnedResource> CopyOwnedResources() =>
        FixedOwnedResources.Select(resource => resource with { }).ToArray();

    private static AgentLifecyclePlanStep Step(int sequence, OperationRisk risk, string action, string verification) =>
        new(sequence, risk, action, verification);
}
