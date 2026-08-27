using System.Security.Cryptography;

namespace ServerDesk.Domain.Security;

public readonly record struct HostKeyFingerprint
{
    private const string Prefix = "SHA256:";

    private HostKeyFingerprint(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static HostKeyFingerprint FromHostKey(ReadOnlySpan<byte> hostKey)
    {
        if (hostKey.IsEmpty)
        {
            throw new ArgumentException("Host key cannot be empty.", nameof(hostKey));
        }

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(hostKey, hash);
        return new HostKeyFingerprint(Prefix + Convert.ToBase64String(hash).TrimEnd('='));
    }

    public static HostKeyFingerprint Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (!normalized.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new FormatException("Host-key fingerprint must use the SHA256: format.");
        }

        var encodedHash = normalized[Prefix.Length..];
        var padding = (4 - encodedHash.Length % 4) % 4;
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(encodedHash + new string('=', padding));
        }
        catch (FormatException exception)
        {
            throw new FormatException("Host-key fingerprint contains invalid base64.", exception);
        }

        if (decoded.Length != 32)
        {
            throw new FormatException("Host-key SHA256 fingerprint must contain exactly 32 hash bytes.");
        }

        return new HostKeyFingerprint(Prefix + Convert.ToBase64String(decoded).TrimEnd('='));
    }

    public override string ToString() => Value;
}

public sealed record HostKeyObservation
{
    private HostKeyObservation(
        string host,
        int port,
        string keyAlgorithm,
        HostKeyFingerprint fingerprint)
    {
        Host = NormalizeHost(host);
        if (port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(port), "Port must be between 1 and 65535.");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(keyAlgorithm);
        var normalizedAlgorithm = keyAlgorithm.Trim().ToLowerInvariant();
        if (normalizedAlgorithm.Length > 128 || normalizedAlgorithm.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Host-key algorithm is invalid.", nameof(keyAlgorithm));
        }

        Port = port;
        KeyAlgorithm = normalizedAlgorithm;
        Fingerprint = fingerprint;
    }

    public string Host { get; }

    public int Port { get; }

    public string KeyAlgorithm { get; }

    public HostKeyFingerprint Fingerprint { get; }

    public string EndpointDisplay => $"{Host}:{Port}";

    public static HostKeyObservation Create(
        string host,
        int port,
        string keyAlgorithm,
        HostKeyFingerprint fingerprint) =>
        new(host, port, keyAlgorithm, fingerprint);

    public static string NormalizeHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var normalized = host.Trim().ToLowerInvariant();
        if (normalized.Length > 255 || normalized.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Host is invalid.", nameof(host));
        }

        return normalized;
    }
}

public sealed record KnownHostRecord(
    Guid Id,
    string Host,
    int Port,
    string KeyAlgorithm,
    HostKeyFingerprint Fingerprint,
    DateTimeOffset TrustedAtUtc)
{
    public static KnownHostRecord Trust(
        HostKeyObservation observation,
        DateTimeOffset? trustedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return new KnownHostRecord(
            Guid.NewGuid(),
            observation.Host,
            observation.Port,
            observation.KeyAlgorithm,
            observation.Fingerprint,
            trustedAtUtc ?? DateTimeOffset.UtcNow);
    }

    public static KnownHostRecord Rehydrate(
        Guid id,
        string host,
        int port,
        string keyAlgorithm,
        HostKeyFingerprint fingerprint,
        DateTimeOffset trustedAtUtc)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("Known-host id cannot be empty.", nameof(id));
        }

        var observation = HostKeyObservation.Create(host, port, keyAlgorithm, fingerprint);
        return new KnownHostRecord(
            id,
            observation.Host,
            observation.Port,
            observation.KeyAlgorithm,
            observation.Fingerprint,
            trustedAtUtc);
    }
}
