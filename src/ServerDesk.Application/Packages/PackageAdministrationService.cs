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

namespace ServerDesk.Application.Packages;

public sealed partial class PackageAdministrationService : IPackageManager
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C",
            ["LANG"] = "C",
        };

    private static readonly Regex SafePackageName = SafePackageNameRegex();
    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly PackageAdministrationOptions _options;
    private readonly IReadOnlyDictionary<PackageManagerKind, IPackageAdapter> _adapters;
    private readonly ConcurrentDictionary<Guid, string> _capabilities = new();

    public PackageAdministrationService(
        IRemoteCommandExecutorFactory commandFactory,
        PackageAdministrationOptions options)
    {
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
        _adapters = new Dictionary<PackageManagerKind, IPackageAdapter>
        {
            [PackageManagerKind.Apt] = new AptPackageAdapter(_options),
            [PackageManagerKind.Dnf] = new DnfPackageAdapter(_options),
        };
    }

    public async Task<PackageInventoryResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _commandFactory.Create(profile);
        var observations = new List<PackageManagerObservation>();
        foreach (var adapter in _adapters.Values)
        {
            observations.Add(await adapter.ProbeAsync(executor, cancellationToken).ConfigureAwait(false));
        }

        var available = observations.Where(IsAvailable).ToArray();
        if (available.Length > 1)
        {
            return SnapshotResult(
                PackageManagerRuntimeStatus.AdapterConflict,
                null,
                [],
                observations,
                "APT and DNF capability pairs are both available. ServerDesk will not guess package-manager ownership.");
        }

        if (available.Length == 0)
        {
            if (observations.Any(item => item.PermissionDenied))
            {
                return SnapshotResult(
                    PackageManagerRuntimeStatus.PermissionDenied,
                    null,
                    [],
                    observations,
                    "Package-manager capability probing was denied by the remote account or execution policy.");
            }

            return SnapshotResult(
                PackageManagerRuntimeStatus.Unavailable,
                null,
                [],
                observations,
                "No complete APT (apt-get + dpkg-query) or DNF (dnf + rpm) capability pair is available.");
        }

        var selected = available[0].Manager;
        var inventory = await _adapters[selected]
            .InspectAsync(executor, cancellationToken)
            .ConfigureAwait(false);
        if (inventory.Error is not null)
        {
            var status = inventory.Error.Code == RemoteErrorCode.PermissionDenied ||
                inventory.Error.Code == RemoteErrorCode.SudoRequired
                ? PackageManagerRuntimeStatus.PermissionDenied
                : PackageManagerRuntimeStatus.Error;
            return new PackageInventoryResult(
                new PackageInventorySnapshot(
                    status,
                    selected,
                    [],
                    observations,
                    inventory.Error.Message,
                    DateTimeOffset.UtcNow),
                inventory.Error);
        }

        return SnapshotResult(
            PackageManagerRuntimeStatus.Available,
            selected,
            inventory.Packages,
            observations,
            inventory.Detail);
    }

    public async Task<PackageMutationPreviewResult> PreviewAsync(
        ServerProfile profile,
        PackageMutationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        PackageMutationRequest normalized;
        try
        {
            normalized = NormalizeRequest(request);
        }
        catch (ArgumentException exception)
        {
            return PreviewFailure(RemoteErrorCode.InvalidEndpoint, exception.Message);
        }

        var inspected = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (inspected.Snapshot is not { Status: PackageManagerRuntimeStatus.Available } before ||
            before.ActiveManager is null)
        {
            return new PackageMutationPreviewResult(
                null,
                inspected.Error ?? new RemoteError(
                    RemoteErrorCode.CapabilityUnavailable,
                    inspected.Snapshot?.Detail ?? "No package manager is safely available."));
        }

        if (before.ActiveManager != normalized.Manager)
        {
            return PreviewFailure(
                RemoteErrorCode.PathConflict,
                $"The requested {normalized.Manager} adapter is not the single active runtime package-manager capability.");
        }

        await using var executor = _commandFactory.Create(profile);
        var adapter = _adapters[normalized.Manager];
        var bound = normalized.Kind == PackageMutationKind.RefreshMetadata
            ? new AdapterPackageResolution([], null)
            : await adapter.ResolveAsync(executor, normalized.PackageNames, cancellationToken).ConfigureAwait(false);
        if (bound.Error is not null)
        {
            return new PackageMutationPreviewResult(null, bound.Error);
        }

        var precondition = ValidateMutationPreconditions(normalized, bound.Packages);
        if (precondition is not null)
        {
            return new PackageMutationPreviewResult(null, precondition);
        }

        var command = adapter.BuildCommand(normalized);
        var planId = Guid.NewGuid();
        var provisional = new PackageMutationPreview(
            planId,
            string.Empty,
            normalized,
            StateFingerprint(before),
            bound.Packages,
            command.Executable,
            command.Arguments,
            command.Risk,
            AnalyzeImpact(normalized),
            Display(command.Executable, command.Arguments));
        var fingerprint = PreviewFingerprint(provisional);
        var preview = provisional with { Fingerprint = fingerprint };
        _capabilities[planId] = fingerprint;
        return new PackageMutationPreviewResult(preview, null);
    }

    public async Task<PackageMutationResult> ExecuteAsync(
        ServerProfile profile,
        PackageMutationPreview preview,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(preview);
        var actualFingerprint = PreviewFingerprint(preview with { Fingerprint = string.Empty });
        if (!_capabilities.TryRemove(preview.PlanId, out var expectedFingerprint) ||
            !FixedTimeEquals(expectedFingerprint, preview.Fingerprint) ||
            !FixedTimeEquals(actualFingerprint, preview.Fingerprint))
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "Package Preview is missing, replayed or modified. Re-read package state and preview again.");
        }

        var inspected = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (inspected.Snapshot is not { Status: PackageManagerRuntimeStatus.Available } before ||
            before.ActiveManager != preview.Request.Manager)
        {
            return Failure(
                inspected.Error ?? new RemoteError(
                    RemoteErrorCode.PathConflict,
                    "Package-manager capability changed after Preview."));
        }

        if (!string.Equals(StateFingerprint(before), preview.BeforeStateFingerprint, StringComparison.Ordinal))
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "Installed/update package state changed after Preview. Refresh and preview again before mutation.");
        }

        await using var executor = _commandFactory.Create(profile);
        var adapter = _adapters[preview.Request.Manager];
        if (preview.Request.Kind != PackageMutationKind.RefreshMetadata)
        {
            var rebound = await adapter.ResolveAsync(
                    executor,
                    preview.Request.PackageNames,
                    cancellationToken)
                .ConfigureAwait(false);
            if (rebound.Error is not null)
            {
                return Failure(rebound.Error);
            }

            if (!string.Equals(
                    PackageSetFingerprint(rebound.Packages),
                    PackageSetFingerprint(preview.BoundPackages),
                    StringComparison.Ordinal))
            {
                return Failure(
                    RemoteErrorCode.PathConflict,
                    "Selected package candidate/version identity changed after Preview. Refresh and preview again.");
            }
        }

        var command = adapter.BuildCommand(preview.Request);
        if (!string.Equals(command.Executable, preview.Executable, StringComparison.Ordinal) ||
            !command.Arguments.SequenceEqual(preview.Arguments, StringComparer.Ordinal) ||
            command.Risk != preview.Risk)
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "The package command no longer matches the exact typed Preview.");
        }

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
            return await AmbiguousAfterDispatchAsync(
                profile,
                preview,
                "Package execution was cancelled after dispatch began. Completion is unknown; do not retry until package state is refreshed.")
                .ConfigureAwait(false);
        }

        if (execution.Error is not null)
        {
            return IsAmbiguous(execution.Error.Code)
                ? await AmbiguousAfterDispatchAsync(
                        profile,
                        preview,
                        "ServerDesk lost a reliable completion signal after package mutation dispatch. Refresh package state before any retry.",
                        execution.Error.TechnicalDetails)
                    .ConfigureAwait(false)
                : await HandleDeterministicFailureAsync(profile, preview, before, execution.Error)
                    .ConfigureAwait(false);
        }

        if (OutputTooLarge(execution.Command!))
        {
            return await AmbiguousAfterDispatchAsync(
                    profile,
                    preview,
                    "Package command output exceeded the configured safety bound after dispatch. Completion is treated as ambiguous.")
                .ConfigureAwait(false);
        }

        if (execution.Command!.ExitCode != 0)
        {
            var detail = FirstUseful(
                execution.Command.StandardError,
                execution.Command.StandardOutput,
                "Package mutation command failed.");
            return await HandleDeterministicFailureAsync(
                    profile,
                    preview,
                    before,
                    new RemoteError(ClassifyFailure(detail), detail))
                .ConfigureAwait(false);
        }

        return await VerifySuccessAsync(profile, preview, before, cancellationToken).ConfigureAwait(false);
    }

    internal static PackageMutationRequest NormalizeRequest(PackageMutationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var names = (request.PackageNames ?? [])
            .Select(item => item?.Trim() ?? string.Empty)
            .ToArray();
        if (request.Kind == PackageMutationKind.RefreshMetadata)
        {
            if (names.Length != 0)
            {
                throw new ArgumentException("Metadata refresh is manager-scoped and must not carry package identities.", nameof(request));
            }

            return request with { PackageNames = [] };
        }

        if (names.Length == 0)
        {
            throw new ArgumentException("Install, upgrade and remove require explicit selected package identities.", nameof(request));
        }

        if (names.Length > 256)
        {
            throw new ArgumentException("Too many package identities were requested in one guarded mutation.", nameof(request));
        }

        foreach (var name in names)
        {
            if (!SafePackageName.IsMatch(name) || name.StartsWith('-'))
            {
                throw new ArgumentException($"Unsafe or unsupported package identity '{name}'.", nameof(request));
            }
        }

        var normalized = names
            .Distinct(StringComparer.Ordinal)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        return request with { PackageNames = normalized };
    }

    internal static IReadOnlyList<PackageInfo> ParseDpkgInstalled(string output)
    {
        var packages = new List<PackageInfo>();
        foreach (var raw in NormalizeLines(output))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var parts = raw.Split('\t');
            if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                throw new FormatException("dpkg-query returned an unrecognized installed-package row.");
            }

            packages.Add(new PackageInfo(
                parts[0].Trim(),
                parts[1].Trim(),
                null,
                NullIfBlank(parts[2]),
                null,
                PackageUpdateClassification.None));
        }

        return packages;
    }

    internal static IReadOnlyList<PackageUpdateRow> ParseAptUpgradeSimulation(string output)
    {
        var updates = new List<PackageUpdateRow>();
        foreach (var raw in NormalizeLines(output))
        {
            var line = raw.Trim();
            if (!line.StartsWith("Inst ", StringComparison.Ordinal))
            {
                continue;
            }

            var nameEnd = line.IndexOf(' ', 5);
            var open = line.IndexOf('(', StringComparison.Ordinal);
            var close = line.LastIndexOf(')');
            if (nameEnd <= 5 || open <= nameEnd || close <= open)
            {
                continue;
            }

            var name = line[5..nameEnd].Trim();
            var inside = line[(open + 1)..close].Trim();
            var insideParts = inside.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (insideParts.Length == 0)
            {
                continue;
            }

            var candidate = insideParts[0];
            var repository = insideParts.Length > 1
                ? string.Join(' ', insideParts.Skip(1).TakeWhile(item => !item.StartsWith('[', StringComparison.Ordinal)))
                : string.Empty;
            updates.Add(new PackageUpdateRow(
                name,
                candidate,
                NullIfBlank(repository),
                IsSecurityRepository(repository)
                    ? PackageUpdateClassification.Security
                    : PackageUpdateClassification.Regular));
        }

        return updates;
    }

    internal static IReadOnlyList<PackageInfo> ParseRpmInstalled(string output)
    {
        var packages = new List<PackageInfo>();
        foreach (var raw in NormalizeLines(output))
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            var parts = raw.Split('\t');
            if (parts.Length != 3 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
            {
                throw new FormatException("rpm returned an unrecognized installed-package row.");
            }

            packages.Add(new PackageInfo(
                parts[0].Trim(),
                parts[1].Trim(),
                null,
                NullIfBlank(parts[2]),
                null,
                PackageUpdateClassification.None));
        }

        return packages;
    }

    internal static IReadOnlyList<PackageUpdateRow> ParseDnfCheckUpdate(string output)
    {
        var updates = new List<PackageUpdateRow>();
        foreach (var raw in NormalizeLines(output))
        {
            var line = raw.Trim();
            if (line.Length == 0 ||
                line.StartsWith("Last metadata", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Obsoleting", StringComparison.OrdinalIgnoreCase) ||
                line.StartsWith("Security:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !parts[0].Contains('.', StringComparison.Ordinal))
            {
                continue;
            }

            var dot = parts[0].LastIndexOf('.');
            if (dot <= 0 || dot == parts[0].Length - 1)
            {
                continue;
            }

            var name = parts[0][..dot];
            var repository = parts[2];
            updates.Add(new PackageUpdateRow(
                name,
                parts[1],
                repository,
                IsSecurityRepository(repository)
                    ? PackageUpdateClassification.Security
                    : PackageUpdateClassification.Regular));
        }

        return updates;
    }

    private async Task<PackageMutationResult> VerifySuccessAsync(
        ServerProfile profile,
        PackageMutationPreview preview,
        PackageInventorySnapshot before,
        CancellationToken cancellationToken)
    {
        PackageInventoryResult verified;
        try
        {
            verified = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Ambiguous("Package command returned success, but post-verification was cancelled. Refresh package state before retrying.");
        }

        if (verified.Snapshot is not { Status: PackageManagerRuntimeStatus.Available } after ||
            after.ActiveManager != preview.Request.Manager)
        {
            return Ambiguous(
                "Package command returned success, but ServerDesk could not re-read a stable package-manager state.",
                verified.Error?.TechnicalDetails);
        }

        var success = preview.Request.Kind switch
        {
            PackageMutationKind.RefreshMetadata => true,
            PackageMutationKind.Install => preview.Request.PackageNames.All(name => IsInstalled(after, name)),
            PackageMutationKind.Upgrade => preview.BoundPackages.All(bound =>
                after.Packages.Any(item =>
                    string.Equals(item.Name, bound.Name, StringComparison.Ordinal) &&
                    item.IsInstalled &&
                    string.Equals(item.InstalledVersion, bound.CandidateVersion, StringComparison.Ordinal))),
            PackageMutationKind.Remove => preview.Request.PackageNames.All(name => !IsInstalled(after, name)),
            _ => false,
        };
        if (!success)
        {
            return new PackageMutationResult(
                false,
                true,
                "Package command returned success, but normalized post-state did not match the exact previewed package mutation.",
                new RemoteError(RemoteErrorCode.AmbiguousState, "Package post-verification did not match."),
                after);
        }

        return new PackageMutationResult(
            true,
            false,
            "Package operation completed and normalized package state was re-read successfully.",
            null,
            after);
    }

    private async Task<PackageMutationResult> HandleDeterministicFailureAsync(
        ServerProfile profile,
        PackageMutationPreview preview,
        PackageInventorySnapshot before,
        RemoteError error)
    {
        PackageInventoryResult verified;
        try
        {
            verified = await InspectAsync(profile, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return Ambiguous(
                "The package command reported failure, but live package state could not be verified. Refresh before retrying.",
                error.TechnicalDetails);
        }

        if (verified.Snapshot is not { Status: PackageManagerRuntimeStatus.Available } after ||
            after.ActiveManager != preview.Request.Manager)
        {
            return Ambiguous(
                "The package command reported failure, but package-manager state could not be verified.",
                verified.Error?.TechnicalDetails ?? error.TechnicalDetails);
        }

        if (!string.Equals(StateFingerprint(after), StateFingerprint(before), StringComparison.Ordinal))
        {
            return new PackageMutationResult(
                false,
                true,
                "The package command reported failure, but live package/update state changed. Completion is ambiguous; refresh before retrying.",
                new RemoteError(RemoteErrorCode.AmbiguousState, "Deterministic package failure was followed by state drift."),
                after);
        }

        return new PackageMutationResult(false, false, error.Message, error, after);
    }

    private async Task<PackageMutationResult> AmbiguousAfterDispatchAsync(
        ServerProfile profile,
        PackageMutationPreview preview,
        string message,
        string? details = null)
    {
        PackageInventorySnapshot? snapshot = null;
        try
        {
            snapshot = (await InspectAsync(profile, CancellationToken.None).ConfigureAwait(false)).Snapshot;
        }
        catch
        {
        }

        return new PackageMutationResult(
            false,
            true,
            message,
            new RemoteError(RemoteErrorCode.AmbiguousState, message, details),
            snapshot);
    }

    private PackageMutationRequest NormalizeRequest(PackageMutationRequest request)
    {
        var normalized = NormalizeRequest(request);
        if (normalized.PackageNames.Count > _options.MaximumPackagesPerMutation)
        {
            throw new ArgumentException(
                $"At most {_options.MaximumPackagesPerMutation} packages may be changed in one guarded operation.",
                nameof(request));
        }

        return normalized;
    }

    private static RemoteError? ValidateMutationPreconditions(
        PackageMutationRequest request,
        IReadOnlyList<PackageInfo> packages)
    {
        if (request.Kind == PackageMutationKind.RefreshMetadata)
        {
            return null;
        }

        if (packages.Count != request.PackageNames.Count)
        {
            return new RemoteError(
                RemoteErrorCode.PathNotFound,
                "One or more selected package identities could not be resolved exactly by the active package manager.");
        }

        foreach (var name in request.PackageNames)
        {
            var package = packages.Single(item => string.Equals(item.Name, name, StringComparison.Ordinal));
            switch (request.Kind)
            {
                case PackageMutationKind.Install when package.IsInstalled:
                    return new RemoteError(RemoteErrorCode.PathConflict, $"Package '{name}' is already installed; use selected-package Upgrade if an update is available.");
                case PackageMutationKind.Install when string.IsNullOrWhiteSpace(package.CandidateVersion):
                    return new RemoteError(RemoteErrorCode.PathNotFound, $"No install candidate is available for package '{name}'.");
                case PackageMutationKind.Upgrade when !package.IsInstalled:
                    return new RemoteError(RemoteErrorCode.PathConflict, $"Package '{name}' is not installed and cannot be upgraded.");
                case PackageMutationKind.Upgrade when !package.UpdateAvailable:
                    return new RemoteError(RemoteErrorCode.PathConflict, $"Package '{name}' has no distinct candidate update.");
                case PackageMutationKind.Remove when !package.IsInstalled:
                    return new RemoteError(RemoteErrorCode.PathConflict, $"Package '{name}' is not installed and cannot be removed.");
            }
        }

        return null;
    }

    private static PackageImpactHint AnalyzeImpact(PackageMutationRequest request)
    {
        if (request.Kind == PackageMutationKind.RefreshMetadata)
        {
            return new PackageImpactHint(
                false,
                false,
                "Metadata refresh updates package indexes only; it does not apply package upgrades.");
        }

        var reboot = request.PackageNames.Any(IsKernelPackage);
        var restart = request.PackageNames.Any(IsServiceSensitivePackage);
        var selected = string.Join(", ", request.PackageNames);
        var message = reboot
            ? $"Selected package set ({selected}) contains a kernel package; a reboot may be required to activate the new kernel."
            : restart
                ? $"Selected package set ({selected}) contains service/runtime packages; service restarts may be required."
                : $"No deterministic reboot/service-restart hint is available from the selected package identities ({selected}).";
        return new PackageImpactHint(reboot, restart, message);
    }

    private static bool IsKernelPackage(string name) =>
        name.Equals("kernel", StringComparison.Ordinal) ||
        name.StartsWith("kernel-", StringComparison.Ordinal) ||
        name.StartsWith("linux-image", StringComparison.Ordinal) ||
        name.StartsWith("linux-generic", StringComparison.Ordinal);

    private static bool IsServiceSensitivePackage(string name) =>
        name is "systemd" or "glibc" or "libc6" or "openssh-server" or "openssh" or
            "nginx" or "apache2" or "httpd" or "docker" or "docker-ce" or "containerd";

    private static bool IsInstalled(PackageInventorySnapshot snapshot, string name) =>
        snapshot.Packages.Any(item => string.Equals(item.Name, name, StringComparison.Ordinal) && item.IsInstalled);

    private bool OutputTooLarge(RemoteCommandResult command) =>
        command.StandardOutput.Length > _options.MaximumOutputCharacters ||
        command.StandardError.Length > _options.MaximumOutputCharacters;

    private static bool IsAvailable(PackageManagerObservation observation) =>
        observation.ManagerExecutableAvailable &&
        observation.DatabaseExecutableAvailable &&
        !observation.PermissionDenied;

    private static PackageInventoryResult SnapshotResult(
        PackageManagerRuntimeStatus status,
        PackageManagerKind? active,
        IReadOnlyList<PackageInfo> packages,
        IReadOnlyList<PackageManagerObservation> observations,
        string detail) =>
        new(
            new PackageInventorySnapshot(
                status,
                active,
                packages,
                observations,
                detail,
                DateTimeOffset.UtcNow),
            null);

    private static string StateFingerprint(PackageInventorySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.Append(snapshot.Status).Append('|').Append(snapshot.ActiveManager).Append('\n');
        foreach (var observation in snapshot.Observations.OrderBy(item => item.Manager))
        {
            builder.Append("manager|")
                .Append(observation.Manager).Append('|')
                .Append(observation.ManagerExecutableAvailable).Append('|')
                .Append(observation.DatabaseExecutableAvailable).Append('|')
                .Append(observation.PermissionDenied).Append('|')
                .Append(observation.Version).Append('\n');
        }

        foreach (var package in snapshot.Packages.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            AppendPackage(builder, package);
        }

        return Sha256(builder.ToString());
    }

    private static string PackageSetFingerprint(IReadOnlyList<PackageInfo> packages)
    {
        var builder = new StringBuilder();
        foreach (var package in packages.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            AppendPackage(builder, package);
        }

        return Sha256(builder.ToString());
    }

    private static void AppendPackage(StringBuilder builder, PackageInfo package) =>
        builder.Append(package.Name).Append('|')
            .Append(package.InstalledVersion).Append('|')
            .Append(package.CandidateVersion).Append('|')
            .Append(package.Architecture).Append('|')
            .Append(package.Repository).Append('|')
            .Append(package.UpdateClassification).Append('\n');

    private static string PreviewFingerprint(PackageMutationPreview preview)
    {
        var canonical = string.Join(
            "\u001f",
            preview.PlanId,
            preview.Request.Kind,
            preview.Request.Manager,
            string.Join("\u001e", preview.Request.PackageNames),
            preview.BeforeStateFingerprint,
            PackageSetFingerprint(preview.BoundPackages),
            preview.Executable,
            string.Join("\u001e", preview.Arguments),
            preview.Risk,
            preview.ImpactHint.RebootMayBeRequired,
            preview.ImpactHint.ServiceRestartMayBeRequired,
            preview.ImpactHint.Message,
            preview.DisplayCommand);
        return Sha256(canonical);
    }

    private static string Display(string executable, IReadOnlyList<string> arguments) =>
        executable + " " + string.Join(' ', arguments.Select(TokenDisplay));

    private static string TokenDisplay(string value) =>
        value.All(character => char.IsLetterOrDigit(character) || "-._/:@+=%".Contains(character, StringComparison.Ordinal))
            ? value
            : "[token]";

    private static bool IsAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or
            RemoteErrorCode.CommandTimeout or
            RemoteErrorCode.OperationCancelled or
            RemoteErrorCode.ConnectionFailed;

    private static RemoteErrorCode ClassifyFailure(string detail)
    {
        if (detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PermissionDenied;
        }

        if (detail.Contains("password is required", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.SudoRequired;
        }

        if (detail.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("no match", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("unable to locate package", StringComparison.OrdinalIgnoreCase))
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

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool FixedTimeEquals(string left, string right) =>
        left.Length == right.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(left), Encoding.UTF8.GetBytes(right));

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsSecurityRepository(string? repository) =>
        repository?.Contains("security", StringComparison.OrdinalIgnoreCase) == true;

    private static IReadOnlyList<string> NormalizeLines(string output) =>
        (output ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');

    private static PackageMutationPreviewResult PreviewFailure(RemoteErrorCode code, string message) =>
        new(null, new RemoteError(code, message));

    private static PackageMutationResult Failure(RemoteError error) =>
        new(false, error.Code == RemoteErrorCode.AmbiguousState, error.Message, error);

    private static PackageMutationResult Failure(RemoteErrorCode code, string message) =>
        Failure(new RemoteError(code, message));

    private static PackageMutationResult Ambiguous(string message, string? details = null) =>
        new(false, true, message, new RemoteError(RemoteErrorCode.AmbiguousState, message, details));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9+_.:@-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafePackageNameRegex();

    internal sealed record PackageUpdateRow(
        string Name,
        string CandidateVersion,
        string? Repository,
        PackageUpdateClassification Classification);

    private sealed record AdapterCommandPlan(
        string Executable,
        IReadOnlyList<string> Arguments,
        OperationRisk Risk);

    private sealed record AdapterInventoryResult(
        IReadOnlyList<PackageInfo> Packages,
        string Detail,
        RemoteError? Error);

    private sealed record AdapterPackageResolution(
        IReadOnlyList<PackageInfo> Packages,
        RemoteError? Error);

    private interface IPackageAdapter
    {
        PackageManagerKind Kind { get; }

        Task<PackageManagerObservation> ProbeAsync(
            IRemoteCommandExecutor executor,
            CancellationToken cancellationToken);

        Task<AdapterInventoryResult> InspectAsync(
            IRemoteCommandExecutor executor,
            CancellationToken cancellationToken);

        Task<AdapterPackageResolution> ResolveAsync(
            IRemoteCommandExecutor executor,
            IReadOnlyList<string> packageNames,
            CancellationToken cancellationToken);

        AdapterCommandPlan BuildCommand(PackageMutationRequest request);
    }

    private abstract class PackageAdapterBase
    {
        protected readonly PackageAdministrationOptions Options;

        protected PackageAdapterBase(PackageAdministrationOptions options) => Options = options;

        protected async Task<RemoteExecutionResult> RunAsync(
            IRemoteCommandExecutor executor,
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var result = await executor.ExecuteAsync(
                    new RemoteCommandSpec(
                        executable,
                        arguments,
                        Options.CommandTimeout,
                        OperationRisk.ReadOnly,
                        StableEnvironment),
                    cancellationToken)
                .ConfigureAwait(false);
            if (result.Command is { } command &&
                (command.StandardOutput.Length > Options.MaximumOutputCharacters ||
                 command.StandardError.Length > Options.MaximumOutputCharacters))
            {
                return RemoteExecutionResult.Failure(new RemoteError(
                    RemoteErrorCode.CapabilityUnavailable,
                    "Package-manager output exceeded the configured safety bound."));
            }

            return result;
        }

        protected static PackageManagerObservation Observation(
            PackageManagerKind manager,
            ProbeState managerProbe,
            ProbeState databaseProbe) =>
            new(
                manager,
                managerProbe.Available,
                databaseProbe.Available,
                managerProbe.PermissionDenied || databaseProbe.PermissionDenied,
                managerProbe.Version,
                $"manager={managerProbe.Detail}; database={databaseProbe.Detail}");

        protected async Task<ProbeState> ProbeExecutableAsync(
            IRemoteCommandExecutor executor,
            string executable,
            IReadOnlyList<string> arguments,
            CancellationToken cancellationToken)
        {
            var result = await RunAsync(executor, executable, arguments, cancellationToken).ConfigureAwait(false);
            if (result.Error is not null)
            {
                var permission = result.Error.Code is RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired;
                return new ProbeState(false, permission, null, result.Error.Message);
            }

            if (result.Command!.ExitCode == 0)
            {
                var version = FirstUseful(result.Command.StandardOutput, result.Command.StandardError, string.Empty)
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .FirstOrDefault();
                return new ProbeState(true, false, NullIfBlank(version), "available");
            }

            var detail = FirstUseful(result.Command.StandardError, result.Command.StandardOutput, "probe failed");
            var permissionDenied = ClassifyFailure(detail) is RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired;
            return new ProbeState(false, permissionDenied, null, detail);
        }

        protected sealed record ProbeState(
            bool Available,
            bool PermissionDenied,
            string? Version,
            string Detail);
    }

    private sealed class AptPackageAdapter : PackageAdapterBase, IPackageAdapter
    {
        public AptPackageAdapter(PackageAdministrationOptions options)
            : base(options)
        {
        }

        public PackageManagerKind Kind => PackageManagerKind.Apt;

        public async Task<PackageManagerObservation> ProbeAsync(
            IRemoteCommandExecutor executor,
            CancellationToken cancellationToken)
        {
            var manager = await ProbeExecutableAsync(executor, "apt-get", ["--version"], cancellationToken)
                .ConfigureAwait(false);
            var database = await ProbeExecutableAsync(executor, "dpkg-query", ["--version"], cancellationToken)
                .ConfigureAwait(false);
            return Observation(Kind, manager, database);
        }

        public async Task<AdapterInventoryResult> InspectAsync(
            IRemoteCommandExecutor executor,
            CancellationToken cancellationToken)
        {
            var installedResult = await RunAsync(
                    executor,
                    "dpkg-query",
                    ["-W", "-f=${Package}\\t${Version}\\t${Architecture}\\n"],
                    cancellationToken)
                .ConfigureAwait(false);
            var installedError = ReadFailure(installedResult, "dpkg-query inventory failed.");
            if (installedError is not null)
            {
                return new AdapterInventoryResult([], installedError.Message, installedError);
            }

            IReadOnlyList<PackageInfo> installed;
            try
            {
                installed = ParseDpkgInstalled(installedResult.Command!.StandardOutput);
            }
            catch (FormatException exception)
            {
                var error = new RemoteError(RemoteErrorCode.ParseFailed, exception.Message);
                return new AdapterInventoryResult([], error.Message, error);
            }

            var updatesResult = await RunAsync(
                    executor,
                    "apt-get",
                    ["-s", "-o", "Debug::NoLocking=1", "upgrade"],
                    cancellationToken)
                .ConfigureAwait(false);
            var updates = updatesResult.Error is null && updatesResult.Command?.ExitCode == 0
                ? ParseAptUpgradeSimulation(updatesResult.Command.StandardOutput)
                : [];
            var merged = MergeInstalledAndUpdates(installed, updates);
            var detail = updatesResult.Error is null && updatesResult.Command?.ExitCode == 0
                ? "APT installed and cached upgrade inventory loaded."
                : "APT installed inventory loaded; cached upgrade metadata was unavailable or stale. Use explicit Refresh metadata before relying on update candidates.";
            return new AdapterInventoryResult(merged, detail, null);
        }

        public async Task<AdapterPackageResolution> ResolveAsync(
            IRemoteCommandExecutor executor,
            IReadOnlyList<string> packageNames,
            CancellationToken cancellationToken)
        {
            var result = await RunAsync(
                    executor,
                    "apt-cache",
                    ["policy", .. packageNames],
                    cancellationToken)
                .ConfigureAwait(false);
            var failure = ReadFailure(result, "apt-cache policy failed.");
            if (failure is not null)
            {
                return new AdapterPackageResolution([], failure);
            }

            var packages = ParseAptPolicy(result.Command!.StandardOutput, packageNames);
            return packages.Count == packageNames.Count
                ? new AdapterPackageResolution(packages, null)
                : new AdapterPackageResolution(
                    packages,
                    new RemoteError(RemoteErrorCode.PathNotFound, "One or more selected APT packages have no resolvable policy entry."));
        }

        public AdapterCommandPlan BuildCommand(PackageMutationRequest request)
        {
            var args = new List<string> { "-n", "apt-get" };
            switch (request.Kind)
            {
                case PackageMutationKind.RefreshMetadata:
                    args.Add("update");
                    return new AdapterCommandPlan(Options.PrivilegeExecutable, args, OperationRisk.Mutating);
                case PackageMutationKind.Install:
                    args.AddRange(["-y", "--no-install-recommends", "install"]);
                    args.AddRange(request.PackageNames);
                    return new AdapterCommandPlan(Options.PrivilegeExecutable, args, OperationRisk.Mutating);
                case PackageMutationKind.Upgrade:
                    args.AddRange(["-y", "--only-upgrade", "install"]);
                    args.AddRange(request.PackageNames);
                    return new AdapterCommandPlan(Options.PrivilegeExecutable, args, OperationRisk.Mutating);
                case PackageMutationKind.Remove:
                    args.AddRange(["-y", "remove"]);
                    args.AddRange(request.PackageNames);
                    return new AdapterCommandPlan(Options.PrivilegeExecutable, args, OperationRisk.Destructive);
                default:
                    throw new ArgumentOutOfRangeException(nameof(request));
            }
        }

        private static IReadOnlyList<PackageInfo> ParseAptPolicy(
            string output,
            IReadOnlyList<string> expectedNames)
        {
            var expected = expectedNames.ToHashSet(StringComparer.Ordinal);
            var packages = new List<PackageInfo>();
            string? current = null;
            string? installed = null;
            string? candidate = null;

            void Commit()
            {
                if (current is null || !expected.Contains(current))
                {
                    return;
                }

                packages.Add(new PackageInfo(
                    current,
                    installed is "(none)" ? null : NullIfBlank(installed),
                    candidate is "(none)" ? null : NullIfBlank(candidate),
                    null,
                    null,
                    PackageUpdateClassification.Unknown));
            }

            foreach (var raw in NormalizeLines(output))
            {
                if (raw.Length > 0 && !char.IsWhiteSpace(raw[0]) && raw.EndsWith(':'))
                {
                    Commit();
                    current = raw[..^1].Trim();
                    installed = null;
                    candidate = null;
                    continue;
                }

                var line = raw.Trim();
                if (line.StartsWith("Installed:", StringComparison.Ordinal))
                {
                    installed = line["Installed:".Length..].Trim();
                }
                else if (line.StartsWith("Candidate:", StringComparison.Ordinal))
                {
                    candidate = line["Candidate:".Length..].Trim();
                }
            }

            Commit();
            return packages;
        }
    }

    private sealed class DnfPackageAdapter : PackageAdapterBase, IPackageAdapter
    {
        public DnfPackageAdapter(PackageAdministrationOptions options)
            : base(options)
        {
        }

        public PackageManagerKind Kind => PackageManagerKind.Dnf;

        public async Task<PackageManagerObservation> ProbeAsync(
            IRemoteCommandExecutor executor,
            CancellationToken cancellationToken)
        {
            var manager = await ProbeExecutableAsync(executor, "dnf", ["--version"], cancellationToken)
                .ConfigureAwait(false);
            var database = await ProbeExecutableAsync(executor, "rpm", ["--version"], cancellationToken)
                .ConfigureAwait(false);
            return Observation(Kind, manager, database);
        }

        public async Task<AdapterInventoryResult> InspectAsync(
            IRemoteCommandExecutor executor,
            CancellationToken cancellationToken)
        {
            var installedResult = await RunAsync(
                    executor,
                    "rpm",
                    ["-qa", "--qf", "%{NAME}\\t%{VERSION}-%{RELEASE}\\t%{ARCH}\\n"],
                    cancellationToken)
                .ConfigureAwait(false);
            var installedError = ReadFailure(installedResult, "rpm inventory failed.");
            if (installedError is not null)
            {
                return new AdapterInventoryResult([], installedError.Message, installedError);
            }

            IReadOnlyList<PackageInfo> installed;
            try
            {
                installed = ParseRpmInstalled(installedResult.Command!.StandardOutput);
            }
            catch (FormatException exception)
            {
                var error = new RemoteError(RemoteErrorCode.ParseFailed, exception.Message);
                return new AdapterInventoryResult([], error.Message, error);
            }

            var updateResult = await RunAsync(
                    executor,
                    "dnf",
                    ["-q", "--cacheonly", "check-update"],
                    cancellationToken)
                .ConfigureAwait(false);
            var updateCommandAccepted = updateResult.Error is null && updateResult.Command?.ExitCode is 0 or 100;
            var updates = updateCommandAccepted
                ? ParseDnfCheckUpdate(updateResult.Command!.StandardOutput)
                : [];
            var merged = MergeInstalledAndUpdates(installed, updates);
            var detail = updateCommandAccepted
                ? "DNF installed and cached update inventory loaded."
                : "DNF installed inventory loaded; cached update metadata is unavailable. Use explicit Refresh metadata before relying on update candidates.";
            return new AdapterInventoryResult(merged, detail, null);
        }

        public async Task<AdapterPackageResolution> ResolveAsync(
            IRemoteCommandExecutor executor,
            IReadOnlyList<string> packageNames,
            CancellationToken cancellationToken)
        {
            var inventory = await InspectAsync(executor, cancellationToken).ConfigureAwait(false);
            if (inventory.Error is not null)
            {
                return new AdapterPackageResolution([], inventory.Error);
            }

            var resolved = inventory.Packages
                .Where(item => packageNames.Contains(item.Name, StringComparer.Ordinal))
                .ToDictionary(item => item.Name, StringComparer.Ordinal);
            var missing = packageNames.Where(name => !resolved.ContainsKey(name)).ToArray();
            if (missing.Length > 0)
            {
                var availableResult = await RunAsync(
                        executor,
                        "dnf",
                        ["-q", "--cacheonly", "list", "--available", .. missing],
                        cancellationToken)
                    .ConfigureAwait(false);
                if (availableResult.Error is null && availableResult.Command?.ExitCode == 0)
                {
                    foreach (var package in ParseDnfAvailable(availableResult.Command.StandardOutput, missing))
                    {
                        resolved[package.Name] = package;
                    }
                }
            }

            var packages = packageNames
                .Where(resolved.ContainsKey)
                .Select(name => resolved[name])
                .ToArray();
            return packages.Length == packageNames.Count
                ? new AdapterPackageResolution(packages, null)
                : new AdapterPackageResolution(
                    packages,
                    new RemoteError(RemoteErrorCode.PathNotFound, "One or more selected DNF packages are unavailable in installed or cached repository metadata."));
        }

        public AdapterCommandPlan BuildCommand(PackageMutationRequest request)
        {
            var args = new List<string> { "-n", "dnf", "-q" };
            switch (request.Kind)
            {
                case PackageMutationKind.RefreshMetadata:
                    args.Add("makecache");
                    return new AdapterCommandPlan(Options.PrivilegeExecutable, args, OperationRisk.Mutating);
                case PackageMutationKind.Install:
                    args.AddRange(["-y", "install"]);
                    args.AddRange(request.PackageNames);
                    return new AdapterCommandPlan(Options.PrivilegeExecutable, args, OperationRisk.Mutating);
                case PackageMutationKind.Upgrade:
                    args.AddRange(["-y", "upgrade"]);
                    args.AddRange(request.PackageNames);
                    return new AdapterCommandPlan(Options.PrivilegeExecutable, args, OperationRisk.Mutating);
                case PackageMutationKind.Remove:
                    args.AddRange(["-y", "remove"]);
                    args.AddRange(request.PackageNames);
                    return new AdapterCommandPlan(Options.PrivilegeExecutable, args, OperationRisk.Destructive);
                default:
                    throw new ArgumentOutOfRangeException(nameof(request));
            }
        }

        private static IReadOnlyList<PackageInfo> ParseDnfAvailable(
            string output,
            IReadOnlyList<string> expectedNames)
        {
            var expected = expectedNames.ToHashSet(StringComparer.Ordinal);
            var packages = new List<PackageInfo>();
            foreach (var raw in NormalizeLines(output))
            {
                var line = raw.Trim();
                if (line.Length == 0 ||
                    line.StartsWith("Available Packages", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Last metadata", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length < 3)
                {
                    continue;
                }

                var dot = parts[0].LastIndexOf('.');
                if (dot <= 0)
                {
                    continue;
                }

                var name = parts[0][..dot];
                if (!expected.Contains(name))
                {
                    continue;
                }

                packages.Add(new PackageInfo(
                    name,
                    null,
                    parts[1],
                    parts[0][(dot + 1)..],
                    parts[2],
                    IsSecurityRepository(parts[2])
                        ? PackageUpdateClassification.Security
                        : PackageUpdateClassification.Unknown));
            }

            return packages
                .GroupBy(item => item.Name, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToArray();
        }
    }

    private static RemoteError? ReadFailure(RemoteExecutionResult result, string fallback)
    {
        if (result.Error is not null)
        {
            return result.Error;
        }

        if (result.Command!.ExitCode == 0)
        {
            return null;
        }

        var detail = FirstUseful(result.Command.StandardError, result.Command.StandardOutput, fallback);
        return new RemoteError(ClassifyFailure(detail), detail);
    }

    private static IReadOnlyList<PackageInfo> MergeInstalledAndUpdates(
        IReadOnlyList<PackageInfo> installed,
        IReadOnlyList<PackageUpdateRow> updates)
    {
        var updateMap = updates
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return installed
            .Select(package => updateMap.TryGetValue(package.Name, out var update)
                ? package with
                {
                    CandidateVersion = update.CandidateVersion,
                    Repository = update.Repository,
                    UpdateClassification = update.Classification,
                }
                : package)
            .OrderBy(item => item.Name, StringComparer.Ordinal)
            .ToArray();
    }
}

public sealed class AuditedPackageManager : IPackageManager
{
    private readonly IPackageManager _inner;
    private readonly IOperationAudit _audit;

    public AuditedPackageManager(IPackageManager inner, IOperationAudit audit)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
    }

    public Task<PackageInventoryResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default) =>
        _inner.InspectAsync(profile, cancellationToken);

    public Task<PackageMutationPreviewResult> PreviewAsync(
        ServerProfile profile,
        PackageMutationRequest request,
        CancellationToken cancellationToken = default) =>
        _inner.PreviewAsync(profile, request, cancellationToken);

    public async Task<PackageMutationResult> ExecuteAsync(
        ServerProfile profile,
        PackageMutationPreview preview,
        CancellationToken cancellationToken = default)
    {
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
                    Message = result.Message + " Audit persistence failed; do not repeat a package mutation solely for audit.",
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
        PackageMutationPreview preview,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        try
        {
            var identities = preview.Request.PackageNames.Count == 0
                ? "metadata"
                : string.Join(',', preview.Request.PackageNames);
            var entry = OperationAuditEntry.Create(
                "package-administration",
                $"Package {preview.Request.Kind} via {preview.Request.Manager}: {identities}",
                preview.Risk,
                outcome,
                $"{profile.Username}@{profile.Host}:{profile.Port} packages:{preview.Request.Manager}:{identities}");
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
