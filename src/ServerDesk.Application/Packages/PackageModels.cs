using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Packages;

public enum PackageManagerKind
{
    Apt,
    Dnf,
}

public enum PackageManagerRuntimeStatus
{
    Available,
    Unavailable,
    PermissionDenied,
    AdapterConflict,
    Error,
}

public enum PackageUpdateClassification
{
    None,
    Regular,
    Security,
    Unknown,
}

public sealed record PackageInfo(
    string Name,
    string? InstalledVersion,
    string? CandidateVersion,
    string? Architecture,
    string? Repository,
    PackageUpdateClassification UpdateClassification)
{
    public bool IsInstalled => !string.IsNullOrWhiteSpace(InstalledVersion);

    public bool UpdateAvailable =>
        !string.IsNullOrWhiteSpace(CandidateVersion) &&
        !string.Equals(InstalledVersion, CandidateVersion, StringComparison.Ordinal);
}

public sealed record PackageManagerObservation(
    PackageManagerKind Manager,
    bool ManagerExecutableAvailable,
    bool DatabaseExecutableAvailable,
    bool PermissionDenied,
    string? Version,
    string Detail);

public sealed record PackageImpactHint(
    bool RebootMayBeRequired,
    bool ServiceRestartMayBeRequired,
    string Message);

public sealed record PackageInventorySnapshot(
    PackageManagerRuntimeStatus Status,
    PackageManagerKind? ActiveManager,
    IReadOnlyList<PackageInfo> Packages,
    IReadOnlyList<PackageManagerObservation> Observations,
    string Detail,
    DateTimeOffset CapturedAtUtc);

public sealed record PackageInventoryResult(
    PackageInventorySnapshot? Snapshot,
    RemoteError? Error)
{
    public bool IsSuccess => Snapshot is not null && Error is null;
}

public enum PackageMutationKind
{
    RefreshMetadata,
    Install,
    Upgrade,
    Remove,
}

public sealed record PackageMutationRequest(
    PackageMutationKind Kind,
    PackageManagerKind Manager,
    IReadOnlyList<string> PackageNames);

public sealed record PackageMutationPreview(
    Guid PlanId,
    string Fingerprint,
    PackageMutationRequest Request,
    string BeforeStateFingerprint,
    IReadOnlyList<PackageInfo> BoundPackages,
    string Executable,
    IReadOnlyList<string> Arguments,
    OperationRisk Risk,
    PackageImpactHint ImpactHint,
    string DisplayCommand);

public sealed record PackageMutationPreviewResult(
    PackageMutationPreview? Preview,
    RemoteError? Error)
{
    public bool IsSuccess => Preview is not null && Error is null;
}

public sealed record PackageMutationResult(
    bool IsSuccess,
    bool AmbiguousState,
    string Message,
    RemoteError? Error = null,
    PackageInventorySnapshot? VerifiedSnapshot = null);

public sealed record PackageAdministrationOptions(
    TimeSpan CommandTimeout,
    int MaximumOutputCharacters,
    int MaximumPackagesPerMutation,
    string PrivilegeExecutable)
{
    public static PackageAdministrationOptions Default { get; } =
        new(TimeSpan.FromMinutes(2), 4 * 1024 * 1024, 64, "sudo");

    public void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(10))
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }

        if (MaximumOutputCharacters is < 64 * 1024 or > 32 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOutputCharacters));
        }

        if (MaximumPackagesPerMutation is <= 0 or > 256)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumPackagesPerMutation));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(PrivilegeExecutable);
    }
}

public interface IPackageManager
{
    Task<PackageInventoryResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);

    Task<PackageMutationPreviewResult> PreviewAsync(
        ServerProfile profile,
        PackageMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<PackageMutationResult> ExecuteAsync(
        ServerProfile profile,
        PackageMutationPreview preview,
        CancellationToken cancellationToken = default);
}
