using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.UserAdministration;

public sealed class AuthorizedKeyAdministrationService : IAuthorizedKeyAdministrationService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private static readonly string[] PublicKeyTypePrefixes =
    [
        "ssh-ed25519",
        "ssh-rsa",
        "ecdsa-sha2-",
        "sk-ssh-ed25519@",
        "sk-ecdsa-sha2-",
    ];

    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly IRemoteFileSystemFactory _fileSystemFactory;
    private readonly AuthorizedKeyAdministrationOptions _options;
    private readonly ConcurrentDictionary<Guid, string> _capabilities = new();

    public AuthorizedKeyAdministrationService(
        IRemoteCommandExecutorFactory commandFactory,
        IRemoteFileSystemFactory fileSystemFactory,
        AuthorizedKeyAdministrationOptions options)
    {
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _fileSystemFactory = fileSystemFactory ?? throw new ArgumentNullException(nameof(fileSystemFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<AuthorizedKeyLoadResult> LoadAsync(
        ServerProfile profile,
        LocalUserInfo user,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(user);
        if (!TryResolvePaths(user, out var directory, out var file, out var error))
        {
            return LoadFailure(RemoteErrorCode.InvalidEndpoint, error!);
        }

        await using var executor = _commandFactory.Create(profile);
        var directoryStat = await ReadStatAsync(executor, directory, cancellationToken).ConfigureAwait(false);
        if (directoryStat.Error is not null)
        {
            return new AuthorizedKeyLoadResult(null, directoryStat.Error);
        }

        var fileStat = await ReadStatAsync(executor, file, cancellationToken).ConfigureAwait(false);
        if (fileStat.Error is not null)
        {
            return new AuthorizedKeyLoadResult(null, fileStat.Error);
        }

        var text = string.Empty;
        if (fileStat.Exists)
        {
            var read = await executor.ExecuteAsync(
                    ReadOnly(_options.PrivilegeExecutable, ["-n", "cat", "--", file.Value]),
                    cancellationToken)
                .ConfigureAwait(false);
            if (read.Error is not null)
            {
                return new AuthorizedKeyLoadResult(null, read.Error);
            }

            if (read.Command!.ExitCode != 0)
            {
                var detail = FirstUseful(read.Command.StandardError, read.Command.StandardOutput, "authorized_keys read failed.");
                return LoadFailure(ClassifyFailure(detail), detail);
            }

            if (Encoding.UTF8.GetByteCount(read.Command.StandardOutput) > _options.MaximumFileBytes)
            {
                return LoadFailure(RemoteErrorCode.CapabilityUnavailable, "authorized_keys exceeds the configured safety bound.");
            }

            text = NormalizeNewlines(read.Command.StandardOutput);
        }

        var parsed = ParseKeys(text);
        if (parsed.Keys.Count > _options.MaximumKeys)
        {
            return LoadFailure(RemoteErrorCode.CapabilityUnavailable, "authorized_keys contains too many entries for guarded administration.");
        }

        var snapshot = new AuthorizedKeySnapshot(
            user.Username,
            user.UserId,
            user.PrimaryGroupId,
            user.Home,
            directory.Value,
            file.Value,
            directoryStat.Exists,
            fileStat.Exists,
            directoryStat.Mode,
            fileStat.Mode,
            fileStat.UserId,
            fileStat.GroupId,
            parsed.Keys,
            text,
            string.Empty,
            parsed.HasUnparsedContent);
        return new AuthorizedKeyLoadResult(
            snapshot with { StateFingerprint = StateFingerprint(snapshot) },
            null);
    }

    public async Task<AuthorizedKeyMutationPreviewResult> PreviewAsync(
        ServerProfile profile,
        LocalUserInfo user,
        AuthorizedKeyMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(user);
        AuthorizedKeyMutationRequest normalized;
        try
        {
            normalized = NormalizeRequest(user, request);
        }
        catch (ArgumentException exception)
        {
            return PreviewFailure(RemoteErrorCode.InvalidEndpoint, exception.Message);
        }

        var loaded = await LoadAsync(profile, user, cancellationToken).ConfigureAwait(false);
        if (!loaded.IsSuccess || loaded.Snapshot is null)
        {
            return new AuthorizedKeyMutationPreviewResult(
                null,
                loaded.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, "authorized_keys state could not be loaded."));
        }

        var before = loaded.Snapshot;
        if (before.HasUnparsedContent)
        {
            return PreviewFailure(
                RemoteErrorCode.ParseFailed,
                "authorized_keys contains an unrecognized non-comment line. ServerDesk will not rewrite a file it cannot normalize safely.");
        }

        AuthorizedPublicKeyInfo? boundKey = null;
        if (normalized.Kind == AuthorizedKeyMutationKind.Add)
        {
            var key = ParseSinglePublicKey(normalized.PublicKeyLine!);
            if (before.Keys.Any(item => string.Equals(item.Fingerprint, key.Fingerprint, StringComparison.Ordinal)))
            {
                return PreviewFailure(RemoteErrorCode.PathConflict, "That public key is already authorized for the selected user.");
            }
        }
        else
        {
            var matches = before.Keys
                .Where(item => string.Equals(item.Fingerprint, normalized.Fingerprint, StringComparison.Ordinal))
                .ToArray();
            if (matches.Length == 0)
            {
                return PreviewFailure(RemoteErrorCode.PathNotFound, "The selected authorized-key fingerprint no longer exists.");
            }

            if (matches.Length != 1)
            {
                return PreviewFailure(
                    RemoteErrorCode.PathConflict,
                    "The same public-key fingerprint appears multiple times. Resolve duplicates before guarded removal.");
            }

            boundKey = matches[0];
        }

        var planId = Guid.NewGuid();
        var provisional = new AuthorizedKeyMutationPreview(
            planId,
            string.Empty,
            normalized,
            before.StateFingerprint,
            boundKey,
            AnalyzeImpact(profile, user, normalized),
            normalized.Kind == AuthorizedKeyMutationKind.Remove ? OperationRisk.Destructive : OperationRisk.Mutating,
            normalized.Kind == AuthorizedKeyMutationKind.Add
                ? $"Add public key {ParseSinglePublicKey(normalized.PublicKeyLine!).Fingerprint} to {before.FilePath}"
                : $"Remove public key {boundKey!.Fingerprint} from {before.FilePath}");
        var fingerprint = PreviewFingerprint(provisional);
        var preview = provisional with { Fingerprint = fingerprint };
        _capabilities[planId] = fingerprint;
        return new AuthorizedKeyMutationPreviewResult(preview, null);
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

        var actual = PreviewFingerprint(preview with { Fingerprint = string.Empty });
        if (!_capabilities.TryRemove(preview.PlanId, out var expected) ||
            !FixedTimeEquals(preview.Fingerprint, expected) ||
            !FixedTimeEquals(preview.Fingerprint, actual))
        {
            return MutationFailure(
                RemoteErrorCode.PathConflict,
                "Authorized-key Preview is missing, replayed or modified. Reload keys and preview again.");
        }

        var current = await LoadAsync(profile, user, cancellationToken).ConfigureAwait(false);
        if (!current.IsSuccess || current.Snapshot is null)
        {
            return MutationFailure(current.Error ?? new RemoteError(
                RemoteErrorCode.CommandFailed,
                "authorized_keys could not be reloaded before mutation."));
        }

        var before = current.Snapshot;
        if (!string.Equals(before.StateFingerprint, preview.BeforeStateFingerprint, StringComparison.Ordinal))
        {
            return MutationFailure(
                RemoteErrorCode.PathConflict,
                "authorized_keys content or metadata changed after Preview. Reload before mutation.");
        }

        string editedText;
        try
        {
            editedText = BuildEditedText(before, preview);
        }
        catch (InvalidOperationException exception)
        {
            return MutationFailure(RemoteErrorCode.PathConflict, exception.Message);
        }

        return await WriteAndVerifyAsync(profile, user, before, preview, editedText, cancellationToken).ConfigureAwait(false);
    }

    internal ParsedAuthorizedKeys ParseKeys(string text)
    {
        var keys = new List<AuthorizedPublicKeyInfo>();
        var unparsed = false;
        foreach (var raw in NormalizeNewlines(text).Split('\n', StringSplitOptions.None))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            try
            {
                keys.Add(ParseSinglePublicKey(line));
            }
            catch (ArgumentException)
            {
                unparsed = true;
            }
        }

        return new ParsedAuthorizedKeys(keys, unparsed);
    }

    internal static AuthorizedPublicKeyInfo ParseSinglePublicKey(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        var normalized = line.Trim();
        if (normalized.Contains('\n') || normalized.Contains('\r') || normalized.Any(char.IsControl))
        {
            throw new ArgumentException("Exactly one printable authorized public-key line is required.", nameof(line));
        }

        if (normalized.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("BEGIN OPENSSH", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("BEGIN RSA", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("BEGIN EC", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Private-key material is never accepted by authorized-key administration.", nameof(line));
        }

        var parts = normalized.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var typeIndex = Array.FindIndex(parts, IsPublicKeyType);
        if (typeIndex < 0 || typeIndex + 1 >= parts.Length)
        {
            throw new ArgumentException("The authorized-key line does not contain a supported public key type and blob.", nameof(line));
        }

        byte[] blob;
        try
        {
            blob = Convert.FromBase64String(parts[typeIndex + 1]);
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("The authorized public-key blob is not valid base64.", nameof(line), exception);
        }

        if (blob.Length < 16 || blob.Length > 64 * 1024)
        {
            throw new ArgumentException("The authorized public-key blob size is outside the safe range.", nameof(line));
        }

        var fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(blob)).TrimEnd('=');
        var comment = typeIndex + 2 < parts.Length
            ? string.Join(' ', parts.Skip(typeIndex + 2))
            : string.Empty;
        return new AuthorizedPublicKeyInfo(fingerprint, parts[typeIndex], comment, normalized);
    }

    private async Task<AuthorizedKeyMutationResult> WriteAndVerifyAsync(
        ServerProfile profile,
        LocalUserInfo user,
        AuthorizedKeySnapshot before,
        AuthorizedKeyMutationPreview preview,
        string editedText,
        CancellationToken cancellationToken)
    {
        var content = Encoding.UTF8.GetBytes(editedText);
        if (content.Length > _options.MaximumFileBytes)
        {
            return MutationFailure(RemoteErrorCode.CapabilityUnavailable, "Resulting authorized_keys exceeds the configured safety bound.");
        }

        var token = Guid.NewGuid().ToString("N");
        var userStage = RemotePath.Parse($"/tmp/serverdesk-authorized-{token}.tmp");
        var directory = RemotePath.Parse(before.DirectoryPath);
        var target = RemotePath.Parse(before.FilePath);
        var privilegedStage = directory.Combine($".serverdesk-authorized-{token}.new");
        var mutationStarted = false;
        var privilegedStageCreated = false;
        var committed = false;

        await using var fileSystem = _fileSystemFactory.Create(profile);
        await using var executor = _commandFactory.Create(profile);
        try
        {
            await fileSystem.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await using (var source = new MemoryStream(content, writable: false))
            {
                await fileSystem.UploadAsync(
                        source,
                        userStage,
                        content.Length,
                        overwrite: false,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }

            await fileSystem.SetPermissionsAsync(
                    userStage,
                    RemoteUnixPermissions.FromMode(600),
                    cancellationToken)
                .ConfigureAwait(false);

            mutationStarted = true;
            var ensureDirectory = await ExecuteMutationAsync(
                executor,
                [
                    "-n", "install", "-d", "-m", "700",
                    "-o", user.UserId.ToString(CultureInfo.InvariantCulture),
                    "-g", user.PrimaryGroupId.ToString(CultureInfo.InvariantCulture),
                    "--", directory.Value,
                ],
                OperationRisk.Mutating,
                cancellationToken).ConfigureAwait(false);
            if (ensureDirectory is not null)
            {
                return await HandleFailureAsync(profile, user, before, ensureDirectory, cancellationToken).ConfigureAwait(false);
            }

            var installFile = await ExecuteMutationAsync(
                executor,
                [
                    "-n", "install", "-m", "600",
                    "-o", user.UserId.ToString(CultureInfo.InvariantCulture),
                    "-g", user.PrimaryGroupId.ToString(CultureInfo.InvariantCulture),
                    "--", userStage.Value, privilegedStage.Value,
                ],
                OperationRisk.Mutating,
                cancellationToken).ConfigureAwait(false);
            if (installFile is not null)
            {
                return await HandleFailureAsync(profile, user, before, installFile, cancellationToken).ConfigureAwait(false);
            }

            privilegedStageCreated = true;
            var replace = await ExecuteMutationAsync(
                executor,
                ["-n", "mv", "-f", "--", privilegedStage.Value, target.Value],
                preview.Risk,
                cancellationToken).ConfigureAwait(false);
            if (replace is not null)
            {
                return await HandleFailureAsync(profile, user, before, replace, cancellationToken).ConfigureAwait(false);
            }

            committed = true;
        }
        catch (OperationCanceledException) when (mutationStarted)
        {
            return Ambiguous("Authorized-key mutation was cancelled after privileged mutation began. Reload keys before any retry.");
        }
        catch (RemoteFileSystemException exception)
        {
            return mutationStarted
                ? Ambiguous(
                    "Authorized-key mutation lost reliable staging state after privileged mutation began. Reload before retrying.",
                    exception.Error.TechnicalDetails)
                : MutationFailure(exception.Error);
        }
        finally
        {
            try
            {
                if (fileSystem.IsConnected)
                {
                    await fileSystem.DeleteFileAsync(userStage, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch
            {
            }

            if (privilegedStageCreated && !committed)
            {
                try
                {
                    _ = await executor.ExecuteAsync(
                        new RemoteCommandSpec(
                            _options.PrivilegeExecutable,
                            ["-n", "rm", "-f", "--", privilegedStage.Value],
                            TimeSpan.FromSeconds(10),
                            OperationRisk.Mutating,
                            StableEnvironment),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }
        }

        AuthorizedKeyLoadResult verification;
        try
        {
            verification = await LoadAsync(profile, user, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Ambiguous("authorized_keys was replaced, but post-verification was cancelled. Reload before retrying.");
        }

        if (!verification.IsSuccess || verification.Snapshot is null)
        {
            return Ambiguous(
                "authorized_keys was replaced, but ServerDesk could not re-read it for verification.",
                verification.Error?.TechnicalDetails);
        }

        var after = verification.Snapshot;
        if (!VerifyExpected(before, after, preview) ||
            !after.DirectoryExists || after.DirectoryMode != 700 ||
            !after.FileExists || after.FileMode != 600 ||
            after.FileUserId != user.UserId || after.FileGroupId != user.PrimaryGroupId)
        {
            return new AuthorizedKeyMutationResult(
                false,
                true,
                "authorized_keys replace completed, but content/owner/group/mode verification did not match the guarded mutation. Reload before retrying.",
                new RemoteError(RemoteErrorCode.AmbiguousState, "Post-mutation authorized_keys verification did not match."),
                after);
        }

        return new AuthorizedKeyMutationResult(
            true,
            false,
            "Authorized public keys were replaced atomically and verified with .ssh=700 and authorized_keys=600 ownership policy.",
            null,
            after);
    }

    private async Task<AuthorizedKeyMutationResult> HandleFailureAsync(
        ServerProfile profile,
        LocalUserInfo user,
        AuthorizedKeySnapshot before,
        RemoteError error,
        CancellationToken cancellationToken)
    {
        if (IsAmbiguous(error.Code))
        {
            return Ambiguous(
                "ServerDesk lost a reliable completion signal after authorized-key mutation began. Do not retry until keys and permissions are reloaded.",
                error.TechnicalDetails);
        }

        AuthorizedKeyLoadResult verification;
        try
        {
            verification = await LoadAsync(profile, user, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return Ambiguous(
                "The authorized-key command reported failure, but live key content/metadata could not be verified. Reload before retrying.",
                error.TechnicalDetails);
        }

        if (!verification.IsSuccess || verification.Snapshot is null ||
            !string.Equals(verification.Snapshot.StateFingerprint, before.StateFingerprint, StringComparison.Ordinal))
        {
            return Ambiguous(
                "The authorized-key command reported failure, but live key content/metadata could not be proven unchanged. Reload before retrying.",
                verification.Error?.TechnicalDetails ?? error.TechnicalDetails);
        }

        return new AuthorizedKeyMutationResult(false, false, error.Message, error, verification.Snapshot);
    }

    private async Task<RemoteError?> ExecuteMutationAsync(
        IRemoteCommandExecutor executor,
        IReadOnlyList<string> arguments,
        OperationRisk risk,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    _options.PrivilegeExecutable,
                    arguments,
                    _options.CommandTimeout,
                    risk,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is not null)
        {
            return result.Error;
        }

        if (result.Command!.ExitCode == 0)
        {
            return null;
        }

        var detail = FirstUseful(
            result.Command.StandardError,
            result.Command.StandardOutput,
            "Authorized-key mutation command failed.");
        return new RemoteError(ClassifyFailure(detail), detail);
    }

    private async Task<StatResult> ReadStatAsync(
        IRemoteCommandExecutor executor,
        RemotePath path,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
                ReadOnly(
                    _options.PrivilegeExecutable,
                    ["-n", "stat", "--printf=%u:%g:%a", "--", path.Value]),
                cancellationToken)
            .ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new StatResult(false, null, null, null, result.Error);
        }

        if (result.Command!.ExitCode != 0)
        {
            var detail = FirstUseful(result.Command.StandardError, result.Command.StandardOutput, "stat failed.");
            if (detail.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase))
            {
                return new StatResult(false, null, null, null, null);
            }

            return new StatResult(
                false,
                null,
                null,
                null,
                new RemoteError(ClassifyFailure(detail), detail));
        }

        var parts = result.Command.StandardOutput.Trim().Split(':');
        if (parts.Length != 3 ||
            !uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var uid) ||
            !uint.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var gid) ||
            !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var mode))
        {
            return new StatResult(
                false,
                null,
                null,
                null,
                new RemoteError(RemoteErrorCode.ParseFailed, "stat returned unrecognized ownership/mode output."));
        }

        return new StatResult(true, uid, gid, mode, null);
    }

    private RemoteCommandSpec ReadOnly(string executable, IReadOnlyList<string> arguments) =>
        new(executable, arguments, _options.CommandTimeout, OperationRisk.ReadOnly, StableEnvironment);

    private static AuthorizedKeyMutationRequest NormalizeRequest(
        LocalUserInfo user,
        AuthorizedKeyMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var username = request.Username?.Trim() ?? string.Empty;
        if (!string.Equals(username, user.Username, StringComparison.Ordinal))
        {
            throw new ArgumentException("Authorized-key request must target the explicitly selected normalized user.", nameof(request));
        }

        if (string.Equals(username, "root", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("ServerDesk intentionally exposes no root authorized-key mutation workflow.", nameof(request));
        }

        return request.Kind switch
        {
            AuthorizedKeyMutationKind.Add => request with
            {
                Username = username,
                PublicKeyLine = ParseSinglePublicKey(request.PublicKeyLine ?? string.Empty).Line,
                Fingerprint = null,
            },
            AuthorizedKeyMutationKind.Remove => request with
            {
                Username = username,
                PublicKeyLine = null,
                Fingerprint = NormalizeFingerprint(request.Fingerprint),
            },
            _ => throw new ArgumentOutOfRangeException(nameof(request)),
        };
    }

    private static string NormalizeFingerprint(string? value)
    {
        var fingerprint = value?.Trim() ?? string.Empty;
        if (!fingerprint.StartsWith("SHA256:", StringComparison.Ordinal) ||
            fingerprint.Length is < 20 or > 128 ||
            fingerprint.Any(char.IsControl))
        {
            throw new ArgumentException("A normalized SHA256 public-key fingerprint is required.", nameof(value));
        }

        return fingerprint;
    }

    private static ConnectedUserImpact AnalyzeImpact(
        ServerProfile profile,
        LocalUserInfo user,
        AuthorizedKeyMutationRequest request)
    {
        const string noGuarantee = " This analysis cannot guarantee that the current SSH session or a future reconnect will remain available.";
        if (!string.Equals(profile.Username, user.Username, StringComparison.Ordinal))
        {
            return new ConnectedUserImpact(
                ConnectedUserImpactKind.NoKnownRestriction,
                "The key mutation does not target the connected account." + noGuarantee);
        }

        return request.Kind == AuthorizedKeyMutationKind.Remove
            ? new ConnectedUserImpact(
                ConnectedUserImpactKind.PossibleRestriction,
                "Removing an authorized key from the connected account may remove credentials needed for a future SSH reconnect." + noGuarantee)
            : new ConnectedUserImpact(
                ConnectedUserImpactKind.NoKnownRestriction,
                "Adding a public key does not directly remove the connected account's existing key access." + noGuarantee);
    }

    private static string BuildEditedText(
        AuthorizedKeySnapshot before,
        AuthorizedKeyMutationPreview preview)
    {
        if (preview.Request.Kind == AuthorizedKeyMutationKind.Add)
        {
            var line = ParseSinglePublicKey(preview.Request.PublicKeyLine!).Line;
            var text = NormalizeNewlines(before.OriginalText);
            if (text.Length > 0 && !text.EndsWith('\n'))
            {
                text += "\n";
            }

            return text + line + "\n";
        }

        var bound = preview.BoundKey ??
            throw new InvalidOperationException("Authorized-key removal lost its exact bound key identity.");
        var removed = false;
        var output = new List<string>();
        foreach (var line in NormalizeNewlines(before.OriginalText).Split('\n', StringSplitOptions.None))
        {
            if (!removed && line.Trim().Length > 0)
            {
                try
                {
                    var key = ParseSinglePublicKey(line.Trim());
                    if (string.Equals(key.Fingerprint, bound.Fingerprint, StringComparison.Ordinal))
                    {
                        removed = true;
                        continue;
                    }
                }
                catch (ArgumentException)
                {
                }
            }

            output.Add(line);
        }

        if (!removed)
        {
            throw new InvalidOperationException("The bound authorized key is no longer present in the loaded file.");
        }

        var result = string.Join('\n', output);
        return result.Length == 0 || result.EndsWith('\n') ? result : result + "\n";
    }

    private static bool VerifyExpected(
        AuthorizedKeySnapshot before,
        AuthorizedKeySnapshot after,
        AuthorizedKeyMutationPreview preview)
    {
        if (preview.Request.Kind == AuthorizedKeyMutationKind.Add)
        {
            var expected = ParseSinglePublicKey(preview.Request.PublicKeyLine!).Fingerprint;
            return after.Keys.Count == before.Keys.Count + 1 &&
                after.Keys.Count(item => string.Equals(item.Fingerprint, expected, StringComparison.Ordinal)) == 1;
        }

        var fingerprint = preview.BoundKey!.Fingerprint;
        return after.Keys.Count == before.Keys.Count - 1 &&
            after.Keys.All(item => !string.Equals(item.Fingerprint, fingerprint, StringComparison.Ordinal));
    }

    private static bool TryResolvePaths(
        LocalUserInfo user,
        out RemotePath directory,
        out RemotePath file,
        out string? error)
    {
        directory = default;
        file = default;
        error = null;
        try
        {
            var home = RemotePath.Parse(user.Home);
            if (!home.IsAbsolute ||
                !string.Equals(home.Value, user.Home.TrimEnd('/'), StringComparison.Ordinal))
            {
                error = "The selected user's home path is not a normalized absolute path.";
                return false;
            }

            directory = home.Combine(".ssh");
            file = directory.Combine("authorized_keys");
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static string StateFingerprint(AuthorizedKeySnapshot snapshot)
    {
        var canonical = string.Join(
            "\u001f",
            snapshot.Username,
            snapshot.UserId,
            snapshot.GroupId,
            snapshot.Home,
            snapshot.DirectoryPath,
            snapshot.FilePath,
            snapshot.DirectoryExists,
            snapshot.FileExists,
            snapshot.DirectoryMode,
            snapshot.FileMode,
            snapshot.FileUserId,
            snapshot.FileGroupId,
            snapshot.HasUnparsedContent,
            snapshot.OriginalText);
        return Sha256(canonical);
    }

    private static string PreviewFingerprint(AuthorizedKeyMutationPreview preview)
    {
        var canonical = string.Join(
            "\u001f",
            preview.PlanId,
            preview.Request.Kind,
            preview.Request.Username,
            preview.Request.PublicKeyLine,
            preview.Request.Fingerprint,
            preview.BeforeStateFingerprint,
            preview.BoundKey?.Fingerprint,
            preview.ConnectedUserImpact.Kind,
            preview.ConnectedUserImpact.Message,
            preview.Risk,
            preview.Summary);
        return Sha256(canonical);
    }

    private static bool IsPublicKeyType(string value) =>
        PublicKeyTypePrefixes.Any(prefix => value.StartsWith(prefix, StringComparison.Ordinal));

    private static string NormalizeNewlines(string value) =>
        (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string left, string right) =>
        left.Length == right.Length && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));

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

        if (detail.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not found", StringComparison.OrdinalIgnoreCase))
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

    private static AuthorizedKeyLoadResult LoadFailure(RemoteErrorCode code, string message) =>
        new(null, new RemoteError(code, message));

    private static AuthorizedKeyMutationPreviewResult PreviewFailure(RemoteErrorCode code, string message) =>
        new(null, new RemoteError(code, message));

    private static AuthorizedKeyMutationResult MutationFailure(RemoteError error) =>
        new(false, error.Code == RemoteErrorCode.AmbiguousState, error.Message, error);

    private static AuthorizedKeyMutationResult MutationFailure(RemoteErrorCode code, string message) =>
        MutationFailure(new RemoteError(code, message));

    private static AuthorizedKeyMutationResult Ambiguous(string message, string? details = null) =>
        new(false, true, message, new RemoteError(RemoteErrorCode.AmbiguousState, message, details));

    internal sealed record ParsedAuthorizedKeys(
        IReadOnlyList<AuthorizedPublicKeyInfo> Keys,
        bool HasUnparsedContent);

    private sealed record StatResult(
        bool Exists,
        uint? UserId,
        uint? GroupId,
        int? Mode,
        RemoteError? Error);
}

public sealed class AuditedAuthorizedKeyAdministrationService : IAuthorizedKeyAdministrationService
{
    private readonly IAuthorizedKeyAdministrationService _inner;
    private readonly IOperationAudit _audit;

    public AuditedAuthorizedKeyAdministrationService(
        IAuthorizedKeyAdministrationService inner,
        IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public Task<AuthorizedKeyLoadResult> LoadAsync(
        ServerProfile profile,
        LocalUserInfo user,
        CancellationToken cancellationToken = default) =>
        _inner.LoadAsync(profile, user, cancellationToken);

    public Task<AuthorizedKeyMutationPreviewResult> PreviewAsync(
        ServerProfile profile,
        LocalUserInfo user,
        AuthorizedKeyMutationRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.PreviewAsync(profile, user, request, cancellationToken);

    public async Task<AuthorizedKeyMutationResult> ExecuteAsync(
        ServerProfile profile,
        LocalUserInfo user,
        AuthorizedKeyMutationPreview preview,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await _inner.ExecuteAsync(profile, user, preview, cancellationToken).ConfigureAwait(false);
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
                    Message = result.Message + " Audit persistence failed; do not repeat the key mutation solely for audit.",
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
        AuthorizedKeyMutationPreview preview,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var identity = preview.BoundKey?.Fingerprint ??
                (preview.Request.PublicKeyLine is null
                    ? "public-key"
                    : AuthorizedKeyAdministrationService.ParseSinglePublicKey(preview.Request.PublicKeyLine).Fingerprint);
            var entry = OperationAuditEntry.Create(
                "authorized-key-administration",
                $"Authorized public key {preview.Request.Kind} requested for {preview.Request.Username} ({identity})",
                preview.Risk,
                outcome,
                $"{profile.Username}@{profile.Host}:{profile.Port} local-user:{preview.Request.Username} key:{identity}");
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
