using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;

namespace ServerDesk.Application.UserAdministration;

public sealed record AuthorizedPublicKeyInfo(
    string Fingerprint,
    string KeyType,
    string Comment,
    string Line);

public sealed record AuthorizedKeySnapshot(
    string Username,
    uint UserId,
    uint GroupId,
    string Home,
    string DirectoryPath,
    string FilePath,
    bool DirectoryExists,
    bool FileExists,
    int? DirectoryMode,
    int? FileMode,
    uint? FileUserId,
    uint? FileGroupId,
    IReadOnlyList<AuthorizedPublicKeyInfo> Keys,
    string OriginalText,
    string StateFingerprint,
    bool HasUnparsedContent);

public sealed record AuthorizedKeyLoadResult(
    AuthorizedKeySnapshot? Snapshot,
    RemoteError? Error)
{
    public bool IsSuccess => Snapshot is not null && Error is null;
}

public enum AuthorizedKeyMutationKind
{
    Add,
    Remove,
}

public sealed record AuthorizedKeyMutationRequest(
    AuthorizedKeyMutationKind Kind,
    string Username,
    string? PublicKeyLine = null,
    string? Fingerprint = null);

public sealed record AuthorizedKeyMutationPreview(
    Guid PlanId,
    string Fingerprint,
    AuthorizedKeyMutationRequest Request,
    string BeforeStateFingerprint,
    AuthorizedPublicKeyInfo? BoundKey,
    ConnectedUserImpact ConnectedUserImpact,
    OperationRisk Risk,
    string Summary);

public sealed record AuthorizedKeyMutationPreviewResult(
    AuthorizedKeyMutationPreview? Preview,
    RemoteError? Error)
{
    public bool IsSuccess => Preview is not null && Error is null;
}

public sealed record AuthorizedKeyMutationResult(
    bool IsSuccess,
    bool AmbiguousState,
    string Message,
    RemoteError? Error = null,
    AuthorizedKeySnapshot? VerifiedSnapshot = null);

public sealed record AuthorizedKeyAdministrationOptions(
    TimeSpan CommandTimeout,
    int MaximumFileBytes,
    int MaximumKeys,
    string PrivilegeExecutable)
{
    public static AuthorizedKeyAdministrationOptions Default { get; } =
        new(TimeSpan.FromSeconds(20), 1024 * 1024, 4096, "sudo");

    public void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero || CommandTimeout > TimeSpan.FromMinutes(2))
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }

        if (MaximumFileBytes is <= 0 or > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileBytes));
        }

        if (MaximumKeys is <= 0 or > 100_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumKeys));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(PrivilegeExecutable);
    }
}

public interface IAuthorizedKeyAdministrationService
{
    Task<AuthorizedKeyLoadResult> LoadAsync(
        ServerDesk.Domain.Servers.ServerProfile profile,
        LocalUserInfo user,
        CancellationToken cancellationToken = default);

    Task<AuthorizedKeyMutationPreviewResult> PreviewAsync(
        ServerDesk.Domain.Servers.ServerProfile profile,
        LocalUserInfo user,
        AuthorizedKeyMutationRequest request,
        CancellationToken cancellationToken = default);

    Task<AuthorizedKeyMutationResult> ExecuteAsync(
        ServerDesk.Domain.Servers.ServerProfile profile,
        LocalUserInfo user,
        AuthorizedKeyMutationPreview preview,
        CancellationToken cancellationToken = default);
}
