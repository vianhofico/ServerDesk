using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.UserAdministration;

public sealed partial class UserAdministrationService : IUserAdministrationService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };
    private static readonly HashSet<string> PrivilegeGroups = new(StringComparer.OrdinalIgnoreCase)
    {
        "root",
        "sudo",
        "wheel",
        "admin",
    };

    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly UserAdministrationOptions _options;
    private readonly ConcurrentDictionary<Guid, string> _capabilities = new();

    public UserAdministrationService(
        IRemoteCommandExecutorFactory commandFactory,
        UserAdministrationOptions options)
    {
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<UserAdministrationResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _commandFactory.Create(profile);

        var passwd = await ExecuteReadOnlyAsync(executor, "getent", ["passwd"], cancellationToken).ConfigureAwait(false);
        if (passwd.Error is not null)
        {
            return new UserAdministrationResult(null, passwd.Error);
        }

        var group = await ExecuteReadOnlyAsync(executor, "getent", ["group"], cancellationToken).ConfigureAwait(false);
        if (group.Error is not null)
        {
            return new UserAdministrationResult(null, group.Error);
        }

        if (passwd.Command!.ExitCode != 0)
        {
            return FailureResult(FirstUseful(passwd.Command.StandardError, passwd.Command.StandardOutput, "getent passwd failed."));
        }

        if (group.Command!.ExitCode != 0)
        {
            return FailureResult(FirstUseful(group.Command.StandardError, group.Command.StandardOutput, "getent group failed."));
        }

        if (TooLarge(passwd.Command.StandardOutput) || TooLarge(group.Command.StandardOutput))
        {
            return new UserAdministrationResult(
                null,
                new RemoteError(RemoteErrorCode.CapabilityUnavailable, "User/group inventory exceeded the configured safety bound."));
        }

        var lockStates = await ReadLockStatesAsync(executor, cancellationToken).ConfigureAwait(false);
        try
        {
            return new UserAdministrationResult(
                ParseSnapshot(passwd.Command.StandardOutput, group.Command.StandardOutput, lockStates),
                null);
        }
        catch (FormatException exception)
        {
            return new UserAdministrationResult(null, new RemoteError(RemoteErrorCode.ParseFailed, exception.Message));
        }
    }

    public async Task<UserMutationPreviewResult> PreviewAsync(
        ServerProfile profile,
        UserMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        UserMutationRequest normalized;
        try
        {
            normalized = Normalize(request);
        }
        catch (ArgumentException exception)
        {
            return PreviewFailure(RemoteErrorCode.InvalidEndpoint, exception.Message);
        }

        var inventory = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!inventory.IsSuccess || inventory.Snapshot is null)
        {
            return new UserMutationPreviewResult(null, inventory.Error ?? new RemoteError(
                RemoteErrorCode.CommandFailed,
                "User/group state could not be inspected."));
        }

        var precondition = ResolvePrecondition(inventory.Snapshot, normalized);
        if (precondition.Error is not null)
        {
            return new UserMutationPreviewResult(null, precondition.Error);
        }

        var command = BuildCommand(normalized);
        var planId = Guid.NewGuid();
        var provisional = new UserMutationPreview(
            planId,
            string.Empty,
            normalized,
            SnapshotFingerprint(inventory.Snapshot),
            precondition.User,
            precondition.Group,
            AnalyzeConnectedUserImpact(profile, normalized, precondition.User, precondition.Group),
            command.Executable,
            command.Arguments,
            command.Risk,
            Display(command.Executable, command.Arguments));
        var fingerprint = PreviewFingerprint(provisional);
        var preview = provisional with { Fingerprint = fingerprint };
        _capabilities[planId] = fingerprint;
        return new UserMutationPreviewResult(preview, null);
    }

    public async Task<UserMutationResult> ExecuteAsync(
        ServerProfile profile,
        UserMutationPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preview);

        var actualFingerprint = PreviewFingerprint(preview with { Fingerprint = string.Empty });
        if (!_capabilities.TryRemove(preview.PlanId, out var expectedFingerprint) ||
            !FixedTimeEquals(preview.Fingerprint, expectedFingerprint) ||
            !FixedTimeEquals(preview.Fingerprint, actualFingerprint))
        {
            return MutationFailure(RemoteErrorCode.PathConflict,
                "User administration Preview is missing, replayed or modified. Refresh and preview again.");
        }

        var beforeResult = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!beforeResult.IsSuccess || beforeResult.Snapshot is null)
        {
            return MutationFailure(beforeResult.Error ?? new RemoteError(
                RemoteErrorCode.CommandFailed,
                "User/group state could not be re-read before mutation."));
        }

        var before = beforeResult.Snapshot;
        if (!string.Equals(SnapshotFingerprint(before), preview.BeforeFingerprint, StringComparison.Ordinal))
        {
            return MutationFailure(RemoteErrorCode.PathConflict,
                "User/group state changed after Preview. Refresh before mutation.");
        }

        var command = BuildCommand(preview.Request);
        if (!string.Equals(command.Executable, preview.Executable, StringComparison.Ordinal) ||
            !command.Arguments.SequenceEqual(preview.Arguments, StringComparer.Ordinal) ||
            command.Risk != preview.Risk)
        {
            return MutationFailure(RemoteErrorCode.PathConflict,
                "The previewed user-administration command no longer matches the normalized request.");
        }

        await using var executor = _commandFactory.Create(profile);
        RemoteExecutionResult execution;
        try
        {
            execution = await executor.ExecuteAsync(
                    new RemoteCommandSpec(
                        command.Executable,
                        command.Arguments,
                        _options.CommandTimeout,
                        command.Risk,
                        StableEnvironment),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Ambiguous("User administration was cancelled after mutation dispatch began. Refresh state before any retry.");
        }

        if (execution.Error is not null)
        {
            if (IsAmbiguous(execution.Error.Code))
            {
                return Ambiguous(
                    "ServerDesk lost a reliable completion signal after the user-administration mutation may have started. Do not retry until state is refreshed.",
                    execution.Error.TechnicalDetails);
            }

            return await VerifyDeterministicFailureAsync(profile, before, execution.Error, cancellationToken).ConfigureAwait(false);
        }

        if (execution.Command!.ExitCode != 0)
        {
            var detail = FirstUseful(
                execution.Command.StandardError,
                execution.Command.StandardOutput,
                "User administration command failed.");
            var error = new RemoteError(ClassifyFailure(detail), detail);
            return await VerifyDeterministicFailureAsync(profile, before, error, cancellationToken).ConfigureAwait(false);
        }

        return await VerifySuccessAsync(profile, before, preview.Request, cancellationToken).ConfigureAwait(false);
    }

    internal UserAdministrationSnapshot ParseSnapshot(
        string passwdOutput,
        string groupOutput,
        IReadOnlyDictionary<string, UserLockState>? lockStates = null)
    {
        var groups = ParseGroups(groupOutput);
        var groupById = groups.GroupBy(item => item.GroupId).ToDictionary(items => items.Key, items => items.First());
        var users = new List<LocalUserInfo>();

        foreach (var line in SplitLines(passwdOutput).Where(line => line.Length > 0))
        {
            var fields = line.Split(':');
            if (fields.Length < 7 ||
                !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ||
                !uint.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out var gid))
            {
                throw new FormatException("getent passwd returned an unrecognized account row.");
            }

            if (users.Count >= _options.MaximumUsers)
            {
                throw new FormatException("User count exceeded the configured safety bound.");
            }

            var username = fields[0];
            if (string.IsNullOrWhiteSpace(username) || username.Any(char.IsControl))
            {
                throw new FormatException("getent passwd returned an invalid username.");
            }

            var primary = groupById.TryGetValue(gid, out var primaryGroup)
                ? primaryGroup.Name
                : gid.ToString(CultureInfo.InvariantCulture);
            var supplementary = groups
                .Where(item => item.GroupId != gid && item.Members.Contains(username, StringComparer.Ordinal))
                .Select(item => item.Name)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            var visibleGroups = supplementary.Append(primary).ToArray();
            var sudoVisible = groups.Any(item =>
                item.IsPrivilegeSensitive &&
                (item.GroupId == gid || item.Members.Contains(username, StringComparer.Ordinal)));
            var lockState = lockStates is not null && lockStates.TryGetValue(username, out var value)
                ? value
                : UserLockState.Unknown;
            users.Add(new LocalUserInfo(
                username,
                uid,
                gid,
                primary,
                supplementary,
                fields[5],
                fields[6],
                lockState,
                sudoVisible,
                uid < 1000 && uid != 0));
        }

        return new UserAdministrationSnapshot(
            users.OrderBy(item => item.Username, StringComparer.Ordinal).ToArray(),
            groups,
            lockStates is null || lockStates.Count == 0
                ? "User/group inventory loaded; lock state may be unavailable without non-interactive privilege."
                : "User/group inventory and lock state loaded.");
    }

    private IReadOnlyList<LocalGroupInfo> ParseGroups(string groupOutput)
    {
        var groups = new List<LocalGroupInfo>();
        foreach (var line in SplitLines(groupOutput).Where(line => line.Length > 0))
        {
            var fields = line.Split(':');
            if (fields.Length < 4 ||
                !uint.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out var gid))
            {
                throw new FormatException("getent group returned an unrecognized group row.");
            }

            if (groups.Count >= _options.MaximumGroups)
            {
                throw new FormatException("Group count exceeded the configured safety bound.");
            }

            var name = fields[0];
            var members = fields[3]
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(item => item.Length > 0)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray();
            groups.Add(new LocalGroupInfo(
                name,
                gid,
                members,
                gid == 0 || PrivilegeGroups.Contains(name)));
        }

        return groups.OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
    }

    private async Task<IReadOnlyDictionary<string, UserLockState>> ReadLockStatesAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    _options.PrivilegeExecutable,
                    ["-n", "passwd", "-S", "-a"],
                    _options.CommandTimeout,
                    OperationRisk.ReadOnly,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is not null || result.Command is null || result.Command.ExitCode != 0 || TooLarge(result.Command.StandardOutput))
        {
            return new Dictionary<string, UserLockState>(StringComparer.Ordinal);
        }

        var states = new Dictionary<string, UserLockState>(StringComparer.Ordinal);
        foreach (var line in SplitLines(result.Command.StandardOutput))
        {
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 2)
            {
                continue;
            }

            states[parts[0]] = parts[1].ToUpperInvariant() switch
            {
                "P" => UserLockState.Unlocked,
                "L" or "LK" => UserLockState.Locked,
                "NP" => UserLockState.NoPassword,
                _ => UserLockState.Unknown,
            };
        }

        return states;
    }

    private static UserMutationRequest Normalize(UserMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Kind == UserMutationKind.Create)
        {
            var create = request.Create ?? throw new ArgumentException("Create requires an explicit user specification.");
            var username = NormalizeUsername(create.Username);
            if (IsRoot(username))
            {
                throw new ArgumentException("ServerDesk does not create or mutate the root account.");
            }

            var home = NormalizeAbsolutePath(create.Home, "home");
            var shell = NormalizeAbsolutePath(create.Shell, "shell");
            return request with
            {
                Username = username,
                Value = null,
                Create = create with { Username = username, Home = home, Shell = shell },
            };
        }

        var normalizedUsername = NormalizeUsername(request.Username);
        if (IsRoot(normalizedUsername))
        {
            throw new ArgumentException("ServerDesk intentionally exposes no mutation path for the root account.");
        }

        return request.Kind switch
        {
            UserMutationKind.ChangeShell => request with
            {
                Username = normalizedUsername,
                Value = NormalizeAbsolutePath(request.Value, "shell"),
                Create = null,
            },
            UserMutationKind.ChangeHome => request with
            {
                Username = normalizedUsername,
                Value = NormalizeAbsolutePath(request.Value, "home"),
                Create = null,
            },
            UserMutationKind.AddGroup or UserMutationKind.RemoveGroup => request with
            {
                Username = normalizedUsername,
                Value = NormalizeGroup(request.Value),
                Create = null,
            },
            UserMutationKind.Lock or UserMutationKind.Unlock => request with
            {
                Username = normalizedUsername,
                Value = null,
                Create = null,
            },
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    private static string NormalizeUsername(string? value)
    {
        var username = value?.Trim() ?? string.Empty;
        if (!UsernameRegex().IsMatch(username))
        {
            throw new ArgumentException("Username must match the conservative local-account naming policy.", nameof(value));
        }

        return username;
    }

    private static string NormalizeGroup(string? value)
    {
        var group = value?.Trim() ?? string.Empty;
        if (!GroupRegex().IsMatch(group))
        {
            throw new ArgumentException("Group name must match the conservative local-group naming policy.", nameof(value));
        }

        return group;
    }

    private static string NormalizeAbsolutePath(string? value, string field)
    {
        var path = value?.Trim() ?? string.Empty;
        if (!path.StartsWith('/', StringComparison.Ordinal) ||
            path.Length > 4096 ||
            path.Any(char.IsControl) ||
            path.Split('/', StringSplitOptions.None).Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException($"{field} must be a normalized absolute path.", field);
        }

        return path;
    }

    private static PreconditionResult ResolvePrecondition(
        UserAdministrationSnapshot snapshot,
        UserMutationRequest request)
    {
        if (request.Kind == UserMutationKind.Create)
        {
            return snapshot.Users.Any(item => string.Equals(item.Username, request.Username, StringComparison.Ordinal))
                ? PreconditionResult.Fail(RemoteErrorCode.PathConflict, "A local user with that username already exists.")
                : PreconditionResult.Success(null, null);
        }

        var user = snapshot.Users.FirstOrDefault(item => string.Equals(item.Username, request.Username, StringComparison.Ordinal));
        if (user is null)
        {
            return PreconditionResult.Fail(RemoteErrorCode.PathNotFound, "The selected local user no longer exists.");
        }

        if (request.Kind is UserMutationKind.AddGroup or UserMutationKind.RemoveGroup)
        {
            var group = snapshot.Groups.FirstOrDefault(item => string.Equals(item.Name, request.Value, StringComparison.Ordinal));
            if (group is null)
            {
                return PreconditionResult.Fail(RemoteErrorCode.PathNotFound, "The selected local group no longer exists.");
            }

            var member = string.Equals(user.PrimaryGroup, group.Name, StringComparison.Ordinal) ||
                user.SupplementaryGroups.Contains(group.Name, StringComparer.Ordinal);
            if (request.Kind == UserMutationKind.AddGroup && group.IsPrivilegeSensitive)
            {
                return PreconditionResult.Fail(
                    RemoteErrorCode.CapabilityUnavailable,
                    "ServerDesk intentionally provides no shortcut for granting root/sudo/wheel/admin group membership.");
            }

            if (request.Kind == UserMutationKind.AddGroup && member)
            {
                return PreconditionResult.Fail(RemoteErrorCode.PathConflict, "The user is already a member of that group.");
            }

            if (request.Kind == UserMutationKind.RemoveGroup && !member)
            {
                return PreconditionResult.Fail(RemoteErrorCode.PathConflict, "The user is not a member of that group.");
            }

            if (request.Kind == UserMutationKind.RemoveGroup && string.Equals(user.PrimaryGroup, group.Name, StringComparison.Ordinal))
            {
                return PreconditionResult.Fail(RemoteErrorCode.CapabilityUnavailable,
                    "ServerDesk does not remove a user's primary group through the supplementary-membership workflow.");
            }

            return PreconditionResult.Success(user, group);
        }

        if (request.Kind == UserMutationKind.ChangeShell && string.Equals(user.Shell, request.Value, StringComparison.Ordinal))
        {
            return PreconditionResult.Fail(RemoteErrorCode.PathConflict, "The user already has that shell.");
        }

        if (request.Kind == UserMutationKind.ChangeHome && string.Equals(user.Home, request.Value, StringComparison.Ordinal))
        {
            return PreconditionResult.Fail(RemoteErrorCode.PathConflict, "The user already has that home path.");
        }

        if (request.Kind == UserMutationKind.Lock && user.LockState == UserLockState.Locked)
        {
            return PreconditionResult.Fail(RemoteErrorCode.PathConflict, "The user is already locked.");
        }

        if (request.Kind == UserMutationKind.Unlock && user.LockState == UserLockState.Unlocked)
        {
            return PreconditionResult.Fail(RemoteErrorCode.PathConflict, "The user is already unlocked.");
        }

        return PreconditionResult.Success(user, null);
    }

    private CommandPlan BuildCommand(UserMutationRequest request)
    {
        var prefix = new List<string> { "-n" };
        switch (request.Kind)
        {
            case UserMutationKind.Create:
                var create = request.Create!;
                prefix.Add("useradd");
                prefix.Add(create.CreateHome ? "--create-home" : "--no-create-home");
                prefix.Add("--home-dir");
                prefix.Add(create.Home);
                prefix.Add("--shell");
                prefix.Add(create.Shell);
                prefix.Add("--");
                prefix.Add(create.Username);
                return new CommandPlan(_options.PrivilegeExecutable, prefix, OperationRisk.Mutating);
            case UserMutationKind.ChangeShell:
                return Privileged(["usermod", "--shell", request.Value!, "--", request.Username], OperationRisk.Mutating);
            case UserMutationKind.ChangeHome:
                return Privileged(["usermod", "--home", request.Value!, "--", request.Username], OperationRisk.Mutating);
            case UserMutationKind.Lock:
                return Privileged(["usermod", "--lock", "--", request.Username], OperationRisk.Destructive);
            case UserMutationKind.Unlock:
                return Privileged(["usermod", "--unlock", "--", request.Username], OperationRisk.Mutating);
            case UserMutationKind.AddGroup:
                return Privileged(["usermod", "--append", "--groups", request.Value!, "--", request.Username], OperationRisk.Mutating);
            case UserMutationKind.RemoveGroup:
                return Privileged(["gpasswd", "--delete", request.Username, request.Value!], OperationRisk.Destructive);
            default:
                throw new ArgumentOutOfRangeException(nameof(request));
        }
    }

    private CommandPlan Privileged(IReadOnlyList<string> arguments, OperationRisk risk) =>
        new(_options.PrivilegeExecutable, ["-n", .. arguments], risk);

    private static ConnectedUserImpact AnalyzeConnectedUserImpact(
        ServerProfile profile,
        UserMutationRequest request,
        LocalUserInfo? user,
        LocalGroupInfo? group)
    {
        const string noGuarantee = " This analysis cannot guarantee that the current SSH session or a future reconnect will remain available.";
        if (user is null || !string.Equals(profile.Username, user.Username, StringComparison.Ordinal))
        {
            return new ConnectedUserImpact(
                ConnectedUserImpactKind.NoKnownRestriction,
                "The mutation does not target the connected account." + noGuarantee);
        }

        if (request.Kind is UserMutationKind.Lock or UserMutationKind.ChangeShell or UserMutationKind.ChangeHome or UserMutationKind.RemoveGroup)
        {
            var detail = request.Kind == UserMutationKind.RemoveGroup && group is not null
                ? $"Removing connected user '{user.Username}' from group '{group.Name}' may affect SSH/session authorization."
                : $"Changing {request.Kind} for connected user '{user.Username}' may affect SSH access or reconnect behavior.";
            return new ConnectedUserImpact(ConnectedUserImpactKind.PossibleRestriction, detail + noGuarantee);
        }

        return new ConnectedUserImpact(
            ConnectedUserImpactKind.NoKnownRestriction,
            "No direct restriction of the connected account was identified from this mutation." + noGuarantee);
    }

    private async Task<UserMutationResult> VerifySuccessAsync(
        ServerProfile profile,
        UserAdministrationSnapshot before,
        UserMutationRequest request,
        CancellationToken cancellationToken)
    {
        UserAdministrationResult verification;
        try
        {
            verification = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Ambiguous("The command returned success, but user/group verification was cancelled. Refresh before retrying.");
        }

        if (!verification.IsSuccess || verification.Snapshot is null)
        {
            return Ambiguous(
                "The command returned success, but user/group state could not be re-read for verification.",
                verification.Error?.TechnicalDetails);
        }

        if (!MatchesExpected(before, verification.Snapshot, request))
        {
            return new UserMutationResult(
                false,
                true,
                "The user-administration command returned success, but normalized poststate did not match the previewed mutation. Refresh before retrying.",
                new RemoteError(RemoteErrorCode.AmbiguousState, "Post-mutation user/group verification did not match."),
                verification.Snapshot);
        }

        return new UserMutationResult(
            true,
            false,
            "User administration completed and normalized state was verified.",
            null,
            verification.Snapshot);
    }

    private async Task<UserMutationResult> VerifyDeterministicFailureAsync(
        ServerProfile profile,
        UserAdministrationSnapshot before,
        RemoteError error,
        CancellationToken cancellationToken)
    {
        UserAdministrationResult verification;
        try
        {
            verification = await InspectAsync(profile, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return Ambiguous(
                "The command reported failure, but ServerDesk could not verify whether user/group state changed. Refresh before retrying.",
                error.TechnicalDetails);
        }

        if (!verification.IsSuccess || verification.Snapshot is null)
        {
            return Ambiguous(
                "The command reported failure, but ServerDesk could not re-read user/group state. Refresh before retrying.",
                verification.Error?.TechnicalDetails ?? error.TechnicalDetails);
        }

        if (!string.Equals(SnapshotFingerprint(before), SnapshotFingerprint(verification.Snapshot), StringComparison.Ordinal))
        {
            return new UserMutationResult(
                false,
                true,
                "The command reported failure, but normalized user/group state changed. Completion is ambiguous; refresh before retrying.",
                new RemoteError(RemoteErrorCode.AmbiguousState, "Deterministic command failure was followed by changed user/group state."),
                verification.Snapshot);
        }

        return new UserMutationResult(false, false, error.Message, error, verification.Snapshot);
    }

    private static bool MatchesExpected(
        UserAdministrationSnapshot before,
        UserAdministrationSnapshot after,
        UserMutationRequest request)
    {
        var beforeUser = before.Users.FirstOrDefault(item => string.Equals(item.Username, request.Username, StringComparison.Ordinal));
        var afterUser = after.Users.FirstOrDefault(item => string.Equals(item.Username, request.Username, StringComparison.Ordinal));
        return request.Kind switch
        {
            UserMutationKind.Create => beforeUser is null && afterUser is not null &&
                string.Equals(afterUser.Home, request.Create!.Home, StringComparison.Ordinal) &&
                string.Equals(afterUser.Shell, request.Create.Shell, StringComparison.Ordinal),
            UserMutationKind.ChangeShell => afterUser is not null && string.Equals(afterUser.Shell, request.Value, StringComparison.Ordinal),
            UserMutationKind.ChangeHome => afterUser is not null && string.Equals(afterUser.Home, request.Value, StringComparison.Ordinal),
            UserMutationKind.Lock => afterUser?.LockState == UserLockState.Locked,
            UserMutationKind.Unlock => afterUser?.LockState == UserLockState.Unlocked,
            UserMutationKind.AddGroup => afterUser is not null && afterUser.SupplementaryGroups.Contains(request.Value!, StringComparer.Ordinal),
            UserMutationKind.RemoveGroup => afterUser is not null && !afterUser.SupplementaryGroups.Contains(request.Value!, StringComparer.Ordinal),
            _ => false,
        };
    }

    private static string SnapshotFingerprint(UserAdministrationSnapshot snapshot)
    {
        var builder = new StringBuilder();
        foreach (var user in snapshot.Users.OrderBy(item => item.Username, StringComparer.Ordinal))
        {
            builder.Append("u|")
                .Append(user.Username).Append('|')
                .Append(user.UserId).Append('|')
                .Append(user.PrimaryGroupId).Append('|')
                .Append(user.PrimaryGroup).Append('|')
                .Append(string.Join(',', user.SupplementaryGroups)).Append('|')
                .Append(user.Home).Append('|')
                .Append(user.Shell).Append('|')
                .Append(user.LockState).Append('|')
                .Append(user.HasSudoVisibility).Append('\n');
        }

        foreach (var group in snapshot.Groups.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            builder.Append("g|")
                .Append(group.Name).Append('|')
                .Append(group.GroupId).Append('|')
                .Append(group.IsPrivilegeSensitive).Append('|')
                .Append(string.Join(',', group.Members)).Append('\n');
        }

        return Sha256(builder.ToString());
    }

    private static string PreviewFingerprint(UserMutationPreview preview)
    {
        var create = preview.Request.Create;
        var builder = new StringBuilder();
        builder.Append(preview.PlanId).Append('|')
            .Append(preview.Request.Kind).Append('|')
            .Append(preview.Request.Username).Append('|')
            .Append(preview.Request.Value).Append('|')
            .Append(create?.Username).Append('|')
            .Append(create?.Home).Append('|')
            .Append(create?.Shell).Append('|')
            .Append(create?.CreateHome).Append('|')
            .Append(preview.BeforeFingerprint).Append('|')
            .Append(preview.BoundUser?.Username).Append('|')
            .Append(preview.BoundUser?.UserId).Append('|')
            .Append(preview.BoundGroup?.Name).Append('|')
            .Append(preview.BoundGroup?.GroupId).Append('|')
            .Append(preview.ConnectedUserImpact.Kind).Append('|')
            .Append(preview.ConnectedUserImpact.Message).Append('|')
            .Append(preview.Executable).Append('|')
            .Append(string.Join("\u001f", preview.Arguments)).Append('|')
            .Append(preview.Risk).Append('|')
            .Append(preview.DisplayCommand);
        return Sha256(builder.ToString());
    }

    private async Task<RemoteExecutionResult> ExecuteReadOnlyAsync(
        IRemoteCommandExecutor executor,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken) =>
        await executor.ExecuteAsync(
                new RemoteCommandSpec(executable, arguments, _options.CommandTimeout, OperationRisk.ReadOnly, StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);

    private bool TooLarge(string value) => Encoding.UTF8.GetByteCount(value ?? string.Empty) > _options.MaximumOutputBytes;

    private static string[] SplitLines(string value) =>
        (value ?? string.Empty)
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n')
        .Split('\n', StringSplitOptions.TrimEntries);

    private static string Display(string executable, IReadOnlyList<string> arguments) =>
        executable + " " + string.Join(" ", arguments.Select(TokenDisplay));

    private static string TokenDisplay(string value) =>
        value.All(character => char.IsLetterOrDigit(character) || "-._/:=@".Contains(character))
            ? value
            : "[token]";

    private static bool IsRoot(string username) => string.Equals(username, "root", StringComparison.OrdinalIgnoreCase);

    private static bool IsAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or
            RemoteErrorCode.CommandTimeout or
            RemoteErrorCode.OperationCancelled or
            RemoteErrorCode.ConnectionFailed;

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

        if (detail.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathNotFound;
        }

        if (detail.Contains("already exists", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("already a member", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not a member", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathConflict;
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

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string left, string right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));

    private static UserAdministrationResult FailureResult(string detail) =>
        new(null, new RemoteError(ClassifyFailure(detail), detail));

    private static UserMutationPreviewResult PreviewFailure(RemoteErrorCode code, string message) =>
        new(null, new RemoteError(code, message));

    private static UserMutationResult MutationFailure(RemoteError error) =>
        new(false, error.Code == RemoteErrorCode.AmbiguousState, error.Message, error);

    private static UserMutationResult MutationFailure(RemoteErrorCode code, string message) =>
        MutationFailure(new RemoteError(code, message));

    private static UserMutationResult Ambiguous(string message, string? technicalDetails = null) =>
        new(false, true, message, new RemoteError(RemoteErrorCode.AmbiguousState, message, technicalDetails));

    [GeneratedRegex("^[a-z_][a-z0-9_-]{0,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex UsernameRegex();

    [GeneratedRegex("^[a-z_][a-z0-9_.-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex GroupRegex();

    private sealed record CommandPlan(string Executable, IReadOnlyList<string> Arguments, OperationRisk Risk);

    private sealed record PreconditionResult(LocalUserInfo? User, LocalGroupInfo? Group, RemoteError? Error)
    {
        public static PreconditionResult Success(LocalUserInfo? user, LocalGroupInfo? group) => new(user, group, null);

        public static PreconditionResult Fail(RemoteErrorCode code, string message) =>
            new(null, null, new RemoteError(code, message));
    }
}

public sealed class AuditedUserAdministrationService : IUserAdministrationService
{
    private readonly IUserAdministrationService _inner;
    private readonly IOperationAudit _audit;

    public AuditedUserAdministrationService(IUserAdministrationService inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public Task<UserAdministrationResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default) =>
        _inner.InspectAsync(profile, cancellationToken);

    public Task<UserMutationPreviewResult> PreviewAsync(
        ServerProfile profile,
        UserMutationRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.PreviewAsync(profile, request, cancellationToken);

    public async Task<UserMutationResult> ExecuteAsync(
        ServerProfile profile,
        UserMutationPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preview);
        try
        {
            var result = await _inner.ExecuteAsync(profile, preview, cancellationToken).ConfigureAwait(false);
            var outcome = result.IsSuccess
                ? OperationOutcome.Succeeded
                : result.AmbiguousState
                    ? OperationOutcome.Unknown
                    : OperationOutcome.Failed;
            var persisted = await TryAuditAsync(profile, preview, outcome, cancellationToken).ConfigureAwait(false);
            return persisted
                ? result
                : result with
                {
                    Message = result.Message + " Audit persistence failed; do not repeat the user mutation solely for audit.",
                };
        }
        catch (OperationCanceledException)
        {
            _ = await TryAuditAsync(profile, preview, OperationOutcome.Cancelled, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<bool> TryAuditAsync(
        ServerProfile profile,
        UserMutationPreview preview,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var target = $"{profile.Username}@{profile.Host}:{profile.Port} local-user:{preview.Request.Username}";
            var entry = OperationAuditEntry.Create(
                "user-administration",
                $"Local user {preview.Request.Kind} requested for {preview.Request.Username}",
                preview.Risk,
                outcome,
                target);
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
