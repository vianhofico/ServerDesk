using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;

namespace ServerDesk.Application.UserAdministration;

public enum UserLockState
{
    Unknown,
    Unlocked,
    Locked,
    NoPassword,
}

public sealed record LocalGroupInfo(
    string Name,
    uint GroupId,
    IReadOnlyList<string> Members,
    bool IsPrivilegeSensitive);

public sealed record LocalUserInfo(
    string Username,
    uint UserId,
    uint PrimaryGroupId,
    string PrimaryGroup,
    IReadOnlyList<string> SupplementaryGroups,
    string Home,
    string Shell,
    UserLockState LockState,
    bool HasSudoVisibility,
    bool IsSystemAccount);

public sealed record UserAdministrationSnapshot(
    IReadOnlyList<LocalUserInfo> Users,
    IReadOnlyList<LocalGroupInfo> Groups,
    string Detail);

public sealed record UserAdministrationResult(
    UserAdministrationSnapshot? Snapshot,
    RemoteError? Error)
{
    public bool IsSuccess => Snapshot is not null && Error is null;
}

public sealed record UserAdministrationOptions(
    TimeSpan CommandTimeout,
    int MaximumUsers,
    int MaximumGroups,
    int MaximumOutputBytes,
    string PrivilegeExecutable)
{
    public static UserAdministrationOptions Default { get; } = new(
        TimeSpan.FromSeconds(20),
        4096,
        4096,
        4 * 1024 * 1024,
        "sudo");

    public void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }

        if (MaximumUsers is <= 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumUsers));
        }

        if (MaximumGroups is <= 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumGroups));
        }

        if (MaximumOutputBytes is <= 0 or > 64 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOutputBytes));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(PrivilegeExecutable);
    }
}

public enum UserMutationKind
{
    Create,
    ChangeShell,
    ChangeHome,
    Lock,
    Unlock,
    AddGroup,
    RemoveGroup,
}

public enum ConnectedUserImpactKind
{
    NoKnownRestriction,
    PossibleRestriction,
    Unknown,
}

public sealed record CreateLocalUserSpec(
    string Username,
    string Home,
    string Shell,
    bool CreateHome = true);

public sealed record UserMutationRequest(
    UserMutationKind Kind,
    string Username,
    string? Value = null,
    CreateLocalUserSpec? Create = null);

public sealed record ConnectedUserImpact(
    ConnectedUserImpactKind Kind,
    string Message);

public sealed record UserMutationPreview(
    Guid PlanId,
    string Fingerprint,
    UserMutationRequest Request,
    string BeforeFingerprint,
    LocalUserInfo? BoundUser,
    LocalGroupInfo? BoundGroup,
    ConnectedUserImpact ConnectedUserImpact,
    string Executable,
    IReadOnlyList<string> Arguments,
    OperationRisk Risk,
    string DisplayCommand);

public sealed record UserMutationPreviewResult(
    UserMutationPreview? Preview,
    RemoteError? Error)
{
    public bool IsSuccess => Preview is not null && Error is null;
}

public sealed record UserMutationResult(
    bool IsSuccess,
    bool AmbiguousState,
    string Message,
    RemoteError? Error = null,
    UserAdministrationSnapshot? VerifiedSnapshot = null);

public interface IUserAdministrationService
{
    Task<UserAdministrationResult> InspectAsync(
        ServerDesk.Domain.Servers.ServerProfile profile,
        CancellationToken cancellationToken = default);

    Task<UserMutationPreviewResult> PreviewAsync(
        ServerDesk.Domain.Servers.ServerProfile profile,
        UserMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<UserMutationResult> ExecuteAsync(
        ServerDesk.Domain.Servers.ServerProfile profile,
        UserMutationPreview preview,
        CancellationToken cancellationToken = default);
}
