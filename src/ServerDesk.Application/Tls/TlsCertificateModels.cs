using ServerDesk.Domain.Errors;

namespace ServerDesk.Application.Tls;

public enum TlsCertificateHealth
{
    Valid,
    ExpiringSoon,
    Expired,
    NotYetValid,
    Unreadable,
}

public enum CertbotRuntimeState
{
    Available,
    CliMissing,
    SudoRequired,
    PermissionDenied,
    ProbeFailed,
    OutputUnrecognized,
}

public sealed record CertbotManagedCertificate(
    string Name,
    IReadOnlyList<string> Domains,
    string CertificatePath,
    string PrivateKeyPath);

public sealed record CertbotCapability(
    CertbotRuntimeState State,
    string? Version,
    bool NginxPluginAvailable,
    IReadOnlyList<CertbotManagedCertificate> ManagedCertificates,
    string? Detail = null)
{
    public bool CanMutate => State == CertbotRuntimeState.Available;
}

public sealed record TlsCertificateInfo(
    string CertificatePath,
    IReadOnlyList<string> PrivateKeyPaths,
    string? Subject,
    IReadOnlyList<string> SubjectAlternativeNames,
    string? Issuer,
    DateTimeOffset? NotBeforeUtc,
    DateTimeOffset? NotAfterUtc,
    int? DaysRemaining,
    string? FingerprintSha256,
    TlsCertificateHealth Health,
    IReadOnlyList<string> ReferencedSites,
    string? CertbotCertificateName,
    string? ReadError = null)
{
    public bool IsCertbotManaged => !string.IsNullOrWhiteSpace(CertbotCertificateName);
}

public sealed record TlsCertificateInventorySnapshot(
    IReadOnlyList<TlsCertificateInfo> Certificates,
    CertbotCapability Certbot,
    DateTimeOffset EvaluatedAtUtc);

public sealed record TlsCertificateInventoryResult(
    TlsCertificateInventorySnapshot? Snapshot,
    RemoteError? Error)
{
    public bool IsSuccess => Snapshot is not null && Error is null;
}

public sealed record CertbotObtainRequest(
    string NginxSiteId,
    string CertificateName,
    IReadOnlyList<string> Domains,
    string Email,
    bool TermsAccepted);

public sealed record CertbotMutationResult(
    bool IsSuccess,
    bool AmbiguousState,
    bool CertificateChanged,
    string Message,
    RemoteError? Error = null,
    TlsCertificateInfo? VerifiedCertificate = null);

public sealed record TlsCertificateOptions(
    TimeSpan CommandTimeout,
    int ExpiringSoonDays,
    int MaximumCertificates,
    int MaximumCertbotOutputBytes)
{
    public static TlsCertificateOptions Default { get; } = new(
        TimeSpan.FromSeconds(45),
        30,
        128,
        1024 * 1024);

    public void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(5))
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }

        if (ExpiringSoonDays is < 1 or > 365)
        {
            throw new ArgumentOutOfRangeException(nameof(ExpiringSoonDays));
        }

        if (MaximumCertificates is < 1 or > 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCertificates));
        }

        if (MaximumCertbotOutputBytes is < 4096 or > 8 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumCertbotOutputBytes));
        }
    }
}
