using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.UserAdministration;

public sealed class GuardedAuthorizedKeyAdministrationService : IAuthorizedKeyAdministrationService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly AuthorizedKeyAdministrationService _inner;
    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly AuthorizedKeyAdministrationOptions _options;
    private readonly ConcurrentDictionary<Guid, GuardState> _previewStates = new();

    public GuardedAuthorizedKeyAdministrationService(
        AuthorizedKeyAdministrationService inner,
        IRemoteCommandExecutorFactory commandFactory,
        AuthorizedKeyAdministrationOptions options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<AuthorizedKeyLoadResult> LoadAsync(
        ServerProfile profile,
        LocalUserInfo user,
        CancellationToken cancellationToken = default)
    {
        var guard = await InspectGuardStateAsync(profile, user, cancellationToken).ConfigureAwait(false);
        if (guard.Error is not null)
        {
            return new AuthorizedKeyLoadResult(null, guard.Error);
        }

        return await _inner.LoadAsync(profile, user, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AuthorizedKeyMutationPreviewResult> PreviewAsync(
        ServerProfile profile,
        LocalUserInfo user,
        AuthorizedKeyMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(request);

        var before = await InspectGuardStateAsync(profile, user, cancellationToken).ConfigureAwait(false);
        if (before.Error is not null || before.State is null)
        {
            return PreviewFailure(before.Error ?? new RemoteError(
                RemoteErrorCode.CommandFailed,
                "Authorized-key metadata could not be inspected before Preview."));
        }

        var result = await _inner.PreviewAsync(profile, user, request, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess || result.Preview is null)
        {
            return result;
        }

        var after = await InspectGuardStateAsync(profile, user, cancellationToken).ConfigureAwait(false);
        if (after.Error is not null || after.State is null)
        {
            return PreviewFailure(after.Error ?? new RemoteError(
                RemoteErrorCode.CommandFailed,
                "Authorized-key metadata could not be re-inspected after Preview."));
        }

        if (!string.Equals(before.State.Fingerprint, after.State.Fingerprint, StringComparison.Ordinal))
        {
            return PreviewFailure(new RemoteError(
                RemoteErrorCode.PathConflict,
                "The .ssh or authorized_keys owner/group/mode changed while Preview was being prepared. Reload before mutation."));
        }

        _previewStates[result.Preview.PlanId] = after.State;
        return result;
    }

    public async Task<AuthorizedKeyMutationResult> ExecuteAsync(
        ServerProfile profile,
        LocalUserInfo user,
        AuthorizedKeyMutationPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(preview);

        if (!_previewStates.TryRemove(preview.PlanId, out var expected))
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "Authorized-key Preview metadata is missing, already consumed or no longer valid. Reload and preview again.");
        }

        var current = await InspectGuardStateAsync(profile, user, cancellationToken).ConfigureAwait(false);
        if (current.Error is not null || current.State is null)
        {
            return Failure(current.Error ?? new RemoteError(
                RemoteErrorCode.CommandFailed,
                "Authorized-key metadata could not be re-read before mutation."));
        }

        if (!string.Equals(current.State.Fingerprint, expected.Fingerprint, StringComparison.Ordinal))
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "The .ssh or authorized_keys owner/group/mode changed after Preview. Reload before mutation.");
        }

        var result = await _inner.ExecuteAsync(profile, user, preview, cancellationToken).ConfigureAwait(false);
        if (result.AmbiguousState)
        {
            return result;
        }

        GuardInspection verification;
        try
        {
            verification = await InspectGuardStateAsync(profile, user, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Ambiguous(
                "Authorized-key command completed deterministically, but owner/group/mode verification was cancelled. Reload before any retry.");
        }

        if (verification.Error is not null || verification.State is null)
        {
            return Ambiguous(
                "Authorized-key command completed deterministically, but ServerDesk could not verify .ssh/authorized_keys owner, group and mode.",
                verification.Error?.TechnicalDetails);
        }

        if (!result.IsSuccess)
        {
            return string.Equals(verification.State.Fingerprint, expected.Fingerprint, StringComparison.Ordinal)
                ? result
                : Ambiguous(
                    "The authorized-key command reported failure, but .ssh/authorized_keys metadata changed. Completion is ambiguous; reload before retrying.");
        }

        if (!ExpectedSafePostState(verification.State, user))
        {
            return new AuthorizedKeyMutationResult(
                false,
                true,
                "authorized_keys content mutation succeeded, but .ssh/authorized_keys ownership or permissions are not the guarded 700/600 policy. Reload before retrying.",
                new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    "Post-mutation authorized-key metadata verification did not match the selected user's UID/GID and required modes."),
                result.VerifiedSnapshot);
        }

        return result;
    }

    private async Task<GuardInspection> InspectGuardStateAsync(
        ServerProfile profile,
        LocalUserInfo user,
        CancellationToken cancellationToken)
    {
        if (!TryResolvePaths(user, out var directoryPath, out var filePath, out var pathError))
        {
            return new GuardInspection(null, new RemoteError(RemoteErrorCode.InvalidEndpoint, pathError!));
        }

        await using var executor = _commandFactory.Create(profile);
        var directory = await ReadStatAsync(executor, directoryPath!, cancellationToken).ConfigureAwait(false);
        if (directory.Error is not null)
        {
            return new GuardInspection(null, directory.Error);
        }

        var file = await ReadStatAsync(executor, filePath!, cancellationToken).ConfigureAwait(false);
        if (file.Error is not null)
        {
            return new GuardInspection(null, file.Error);
        }

        var canonical = string.Join(
            "\u001f",
            user.Username,
            user.UserId,
            user.PrimaryGroupId,
            user.Home,
            directoryPath,
            directory.Exists,
            directory.UserId,
            directory.GroupId,
            directory.Mode,
            filePath,
            file.Exists,
            file.UserId,
            file.GroupId,
            file.Mode);
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        return new GuardInspection(
            new GuardState(directory, file, fingerprint),
            null);
    }

    private async Task<StatIdentity> ReadStatAsync(
        IRemoteCommandExecutor executor,
        string path,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
            new RemoteCommandSpec(
                _options.PrivilegeExecutable,
                ["-n", "stat", "--printf=%u:%g:%a", "--", path],
                _options.CommandTimeout,
                OperationRisk.ReadOnly,
                StableEnvironment),
            cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new StatIdentity(false, null, null, null, result.Error);
        }

        if (result.Command!.ExitCode != 0)
        {
            var detail = FirstUseful(result.Command.StandardError, result.Command.StandardOutput, "stat failed.");
            if (detail.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
            {
                return new StatIdentity(false, null, null, null, null);
            }

            return new StatIdentity(false, null, null, null, new RemoteError(ClassifyFailure(detail), detail));
        }

        var parts = result.Command.StandardOutput.Trim().Split(':');
        if (parts.Length != 3 ||
            !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var gid) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var mode))
        {
            return new StatIdentity(
                false,
                null,
                null,
                null,
                new RemoteError(RemoteErrorCode.ParseFailed, "stat returned unrecognized ownership/mode output."));
        }

        return new StatIdentity(true, uid, gid, mode, null);
    }

    private static bool ExpectedSafePostState(GuardState state, LocalUserInfo user) =>
        state.Directory is
        {
            Exists: true,
            Mode: 700,
            UserId: not null,
            GroupId: not null,
        } &&
        state.Directory.UserId == user.UserId &&
        state.Directory.GroupId == user.PrimaryGroupId &&
        state.File is
        {
            Exists: true,
            Mode: 600,
            UserId: not null,
            GroupId: not null,
        } &&
        state.File.UserId == user.UserId &&
        state.File.GroupId == user.PrimaryGroupId;

    private static bool TryResolvePaths(
        LocalUserInfo user,
        out string? directory,
        out string? file,
        out string? error)
    {
        directory = null;
        file = null;
        error = null;
        var home = user.Home?.TrimEnd('/') ?? string.Empty;
        if (!home.StartsWith('/', StringComparison.Ordinal) ||
            home.Length < 2 ||
            home.Contains("/../", StringComparison.Ordinal) ||
            home.EndsWith("/..", StringComparison.Ordinal) ||
            home.Contains('\0'))
        {
            error = "The selected user's home path is not a safe normalized absolute path.";
            return false;
        }

        directory = home + "/.ssh";
        file = directory + "/authorized_keys";
        return true;
    }

    private static RemoteErrorCode ClassifyFailure(string detail)
    {
        if (detail.Contains("password is required", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.SudoRequired;
        }

        if (detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PermissionDenied;
        }

        if (detail.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathNotFound;
        }

        return RemoteErrorCode.CommandFailed;
    }

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        return !string.IsNullOrWhiteSpace(second) ? second.Trim() : fallback;
    }

    private static AuthorizedKeyMutationPreviewResult PreviewFailure(RemoteError error) =>
        new(null, error);

    private static AuthorizedKeyMutationResult Failure(RemoteError error) =>
        new(false, error.Code == RemoteErrorCode.AmbiguousState, error.Message, error);

    private static AuthorizedKeyMutationResult Failure(RemoteErrorCode code, string message) =>
        Failure(new RemoteError(code, message));

    private static AuthorizedKeyMutationResult Ambiguous(string message, string? details = null) =>
        new(false, true, message, new RemoteError(RemoteErrorCode.AmbiguousState, message, details));

    private sealed record GuardState(
        StatIdentity Directory,
        StatIdentity File,
        string Fingerprint);

    private sealed record GuardInspection(GuardState? State, RemoteError? Error);

    private sealed record StatIdentity(
        bool Exists,
        uint? UserId,
        uint? GroupId,
        int? Mode,
        RemoteError? Error);
}
