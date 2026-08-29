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

    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly PackageAdministrationOptions _options;
    private readonly ConcurrentDictionary<Guid, string> _previewCapabilities = new();

    public PackageAdministrationService(
        IRemoteCommandExecutorFactory commandFactory,
        PackageAdministrationOptions options)
    {
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<PackageInventoryResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _commandFactory.Create(profile);

        var apt = await ProbeAsync(
            executor,
            PackageManagerKind.Apt,
            "apt-get",
            ["--version"],
            "dpkg-query",
            ["--version"],
            cancellationToken).ConfigureAwait(false);
        var dnf = await ProbeAsync(
            executor,
            PackageManagerKind.Dnf,
            "dnf",
            ["--version"],
            "rpm",
            ["--version"],
            cancellationToken).ConfigureAwait(false);
        var observations = new[] { apt, dnf };
        var available = observations.Where(IsAvailable).ToArray();

        if (available.Length > 1)
        {
            return Snapshot(
                PackageManagerRuntimeStatus.AdapterConflict,
                null,
                [],
                observations,
                "Both complete APT and DNF capability pairs are available. ServerDesk will not guess which package manager owns this host.");
        }

        if (available.Length == 0)
        {
            if (observations.Any(item => item.PermissionDenied))
            {
                return Snapshot(
                    PackageManagerRuntimeStatus.PermissionDenied,
                    null,
                    [],
                    observations,
                    "Package-manager capability probing was denied by the remote account or execution policy.");
            }

            return Snapshot(
                PackageManagerRuntimeStatus.Unavailable,
                null,
                [],
                observations,
                "No complete APT (apt-get + dpkg-query) or DNF (dnf + rpm) capability pair is available.");
        }

        var manager = available[0].Manager;
        var inventory = manager == PackageManagerKind.Apt
            ? await InspectAptAsync(executor, cancellationToken).ConfigureAwait(false)
            : await InspectDnfAsync(executor, cancellationToken).ConfigureAwait(false);
        if (inventory.Error is not null)
        {
            var status = inventory.Error.Code is RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired
                ? PackageManagerRuntimeStatus.PermissionDenied
                : PackageManagerRuntimeStatus.Error;
            return new PackageInventoryResult(
                new PackageInventorySnapshot(
                    status,
                    manager,
                    [],
                    observations,
                    inventory.Error.Message,
                    DateTimeOffset.UtcNow),
                inventory.Error);
        }

        return Snapshot(
            PackageManagerRuntimeStatus.Available,
            manager,
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
            normalized = NormalizeRequestCore(request, _options.MaximumPackagesPerMutation);
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
        IReadOnlyList<PackageInfo> bound = [];
        if (normalized.Kind != PackageMutationKind.RefreshMetadata)
        {
            var resolved = normalized.Manager == PackageManagerKind.Apt
                ? await ResolveAptAsync(executor, normalized.PackageNames, cancellationToken).ConfigureAwait(false)
                : await ResolveDnfAsync(executor, before, normalized.PackageNames, cancellationToken).ConfigureAwait(false);
            if (resolved.Error is not null)
            {
                return new PackageMutationPreviewResult(null, resolved.Error);
            }

            bound = resolved.Packages;
            var precondition = ValidatePreconditions(normalized, bound);
            if (precondition is not null)
            {
                return new PackageMutationPreviewResult(null, precondition);
            }
        }

        var command = BuildCommand(normalized, _options.PrivilegeExecutable);
        var planId = Guid.NewGuid();
        var provisional = new PackageMutationPreview(
            planId,
            string.Empty,
            normalized,
            StateFingerprint(before),
            bound,
            command.Executable,
            command.Arguments,
            command.Risk,
            AnalyzeImpact(normalized),
            Display(command.Executable, command.Arguments));
        var fingerprint = PreviewFingerprint(provisional);
        var preview = provisional with { Fingerprint = fingerprint };
        _previewCapabilities[planId] = fingerprint;
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
        if (!_previewCapabilities.TryRemove(preview.PlanId, out var expectedFingerprint) ||
            !FixedTimeEquals(preview.Fingerprint, expectedFingerprint) ||
            !FixedTimeEquals(preview.Fingerprint, actualFingerprint))
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
        if (preview.Request.Kind != PackageMutationKind.RefreshMetadata)
        {
            var resolved = preview.Request.Manager == PackageManagerKind.Apt
                ? await ResolveAptAsync(executor, preview.Request.PackageNames, cancellationToken).ConfigureAwait(false)
                : await ResolveDnfAsync(executor, before, preview.Request.PackageNames, cancellationToken).ConfigureAwait(false);
            if (resolved.Error is not null)
            {
                return Failure(resolved.Error);
            }

            if (!string.Equals(
                    PackageSetFingerprint(resolved.Packages),
                    PackageSetFingerprint(preview.BoundPackages),
                    StringComparison.Ordinal))
            {
                return Failure(
                    RemoteErrorCode.PathConflict,
                    "Selected package candidate/version identity changed after Preview. Refresh and preview again.");
            }
        }

        var command = BuildCommand(preview.Request, _options.PrivilegeExecutable);
        if (!string.Equals(command.Executable, preview.Executable, StringComparison.Ordinal) ||
            !command.Arguments.SequenceEqual(preview.Arguments, StringComparer.Ordinal) ||
            command.Risk != preview.Risk)
        {
            return Failure(RemoteErrorCode.PathConflict, "The package command no longer matches the exact typed Preview.");
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
            return Ambiguous(
                "Package execution was cancelled after dispatch began. Completion is unknown; refresh package state before any retry.");
        }

        if (execution.Error is not null)
        {
            if (IsAmbiguous(execution.Error.Code))
            {
                return Ambiguous(
                    "ServerDesk lost a reliable completion signal after package mutation dispatch. Refresh package state before any retry.",
                    execution.Error.TechnicalDetails);
            }

            return await VerifyDeterministicFailureAsync(profile, before, execution.Error).ConfigureAwait(false);
        }

        if (OutputTooLarge(execution.Command!))
        {
            return Ambiguous(
                "Package command output exceeded the configured safety bound after dispatch. Completion is treated as ambiguous.");
        }

        if (execution.Command!.ExitCode != 0)
        {
            var detail = FirstUseful(
                execution.Command.StandardError,
                execution.Command.StandardOutput,
                "Package mutation command failed.");
            return await VerifyDeterministicFailureAsync(
                    profile,
                    before,
                    new RemoteError(ClassifyFailure(detail), detail))
                .ConfigureAwait(false);
        }

        return await VerifySuccessAsync(profile, preview, cancellationToken).ConfigureAwait(false);
    }

    internal static PackageMutationRequest NormalizeRequest(PackageMutationRequest request) =>
        NormalizeRequestCore(request, 256);

    internal static IReadOnlyList<PackageInfo> ParseDpkgInstalled(string output)
    {
        var packages = new List<PackageInfo>();
        foreach (var line in NormalizeLines(output).Where(item => item.Length > 0))
        {
            var parts = line.Split('\t');
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

            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens.Length < 3)
            {
                continue;
            }

            var name = tokens[1];
            var open = line.IndexOf('(');
            var close = line.LastIndexOf(')');
            if (open < 0 || close <= open)
            {
                continue;
            }

            var inside = line[(open + 1)..close].Trim();
            var fields = inside.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length == 0)
            {
                continue;
            }

            var repository = fields.Length > 1
                ? string.Join(' ', fields.Skip(1).TakeWhile(item => !item.StartsWith('[', StringComparison.Ordinal)))
                : null;
            updates.Add(new PackageUpdateRow(
                name,
                fields[0],
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
        foreach (var line in NormalizeLines(output).Where(item => item.Length > 0))
        {
            var parts = line.Split('\t');
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

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 3)
            {
                continue;
            }

            var dot = fields[0].LastIndexOf('.');
            if (dot <= 0 || dot == fields[0].Length - 1)
            {
                continue;
            }

            var repository = fields[2];
            updates.Add(new PackageUpdateRow(
                fields[0][..dot],
                fields[1],
                repository,
                IsSecurityRepository(repository)
                    ? PackageUpdateClassification.Security
                    : PackageUpdateClassification.Regular));
        }

        return updates;
    }

    internal static PackageCommandPlan BuildCommandForTest(
        PackageMutationRequest request,
        string privilegeExecutable = "sudo") =>
        BuildCommand(NormalizeRequest(request), privilegeExecutable);

    private async Task<InventoryRead> InspectAptAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var installed = await ReadAsync(
            executor,
            "dpkg-query",
            ["-W", "-f=${binary:Package}\\t${Version}\\t${Architecture}\\n"],
            cancellationToken).ConfigureAwait(false);
        if (installed.Error is not null)
        {
            return new InventoryRead([], installed.Error.Message, installed.Error);
        }

        IReadOnlyList<PackageInfo> packages;
        try
        {
            packages = ParseDpkgInstalled(installed.Output!);
        }
        catch (FormatException exception)
        {
            var error = new RemoteError(RemoteErrorCode.ParseFailed, exception.Message);
            return new InventoryRead([], error.Message, error);
        }

        var updates = await ReadAsync(
            executor,
            "apt-get",
            ["-s", "-o", "Debug::NoLocking=1", "upgrade"],
            cancellationToken,
            acceptedExitCodes: [0]).ConfigureAwait(false);
        if (updates.Error is null)
        {
            packages = MergeInstalledAndUpdates(packages, ParseAptUpgradeSimulation(updates.Output!));
            return new InventoryRead(packages, "APT installed and cached update inventory loaded.", null);
        }

        return new InventoryRead(
            packages,
            "APT installed inventory loaded; cached update metadata is unavailable or stale. Explicit Refresh metadata is required before relying on update candidates.",
            null);
    }

    private async Task<InventoryRead> InspectDnfAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var installed = await ReadAsync(
            executor,
            "rpm",
            ["-qa", "--qf", "%{NAME}\\t%{VERSION}-%{RELEASE}\\t%{ARCH}\\n"],
            cancellationToken).ConfigureAwait(false);
        if (installed.Error is not null)
        {
            return new InventoryRead([], installed.Error.Message, installed.Error);
        }

        IReadOnlyList<PackageInfo> packages;
        try
        {
            packages = ParseRpmInstalled(installed.Output!);
        }
        catch (FormatException exception)
        {
            var error = new RemoteError(RemoteErrorCode.ParseFailed, exception.Message);
            return new InventoryRead([], error.Message, error);
        }

        var updates = await ReadAsync(
            executor,
            "dnf",
            ["-q", "--cacheonly", "check-update"],
            cancellationToken,
            acceptedExitCodes: [0, 100]).ConfigureAwait(false);
        if (updates.Error is null)
        {
            packages = MergeInstalledAndUpdates(packages, ParseDnfCheckUpdate(updates.Output!));
            return new InventoryRead(packages, "DNF installed and cached update inventory loaded.", null);
        }

        return new InventoryRead(
            packages,
            "DNF installed inventory loaded; cached update metadata is unavailable. Explicit Refresh metadata is required before relying on update candidates.",
            null);
    }

    private async Task<PackageResolution> ResolveAptAsync(
        IRemoteCommandExecutor executor,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        var result = await ReadAsync(
            executor,
            "apt-cache",
            ["policy", .. names],
            cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new PackageResolution([], result.Error);
        }

        var expected = names.ToHashSet(StringComparer.Ordinal);
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

        foreach (var raw in NormalizeLines(result.Output!))
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
        return packages.Count == names.Count
            ? new PackageResolution(packages, null)
            : new PackageResolution(
                packages,
                new RemoteError(RemoteErrorCode.PathNotFound, "One or more selected APT package identities could not be resolved exactly."));
    }

    private async Task<PackageResolution> ResolveDnfAsync(
        IRemoteCommandExecutor executor,
        PackageInventorySnapshot inventory,
        IReadOnlyList<string> names,
        CancellationToken cancellationToken)
    {
        var resolved = inventory.Packages
            .Where(item => names.Contains(item.Name, StringComparer.Ordinal))
            .ToDictionary(item => item.Name, StringComparer.Ordinal);
        var missing = names.Where(name => !resolved.ContainsKey(name)).ToArray();
        if (missing.Length > 0)
        {
            var available = await ReadAsync(
                executor,
                "dnf",
                ["-q", "--cacheonly", "list", "--available", .. missing],
                cancellationToken).ConfigureAwait(false);
            if (available.Error is null)
            {
                foreach (var package in ParseDnfAvailable(available.Output!, missing))
                {
                    resolved[package.Name] = package;
                }
            }
        }

        var packages = names.Where(resolved.ContainsKey).Select(name => resolved[name]).ToArray();
        return packages.Length == names.Count
            ? new PackageResolution(packages, null)
            : new PackageResolution(
                packages,
                new RemoteError(RemoteErrorCode.PathNotFound, "One or more selected DNF package identities are unavailable in installed or cached repository metadata."));
    }

    private async Task<PackageManagerObservation> ProbeAsync(
        IRemoteCommandExecutor executor,
        PackageManagerKind manager,
        string managerExecutable,
        IReadOnlyList<string> managerArguments,
        string databaseExecutable,
        IReadOnlyList<string> databaseArguments,
        CancellationToken cancellationToken)
    {
        var first = await ProbeExecutableAsync(executor, managerExecutable, managerArguments, cancellationToken).ConfigureAwait(false);
        var second = await ProbeExecutableAsync(executor, databaseExecutable, databaseArguments, cancellationToken).ConfigureAwait(false);
        return new PackageManagerObservation(
            manager,
            first.Available,
            second.Available,
            first.PermissionDenied || second.PermissionDenied,
            first.Version,
            $"manager={first.Detail}; database={second.Detail}");
    }

    private async Task<ProbeState> ProbeExecutableAsync(
        IRemoteCommandExecutor executor,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var result = await executor.ExecuteAsync(
            new RemoteCommandSpec(executable, arguments, _options.CommandTimeout, OperationRisk.ReadOnly, StableEnvironment),
            cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            var denied = result.Error.Code is RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired;
            return new ProbeState(false, denied, null, result.Error.Message);
        }

        if (OutputTooLarge(result.Command!))
        {
            return new ProbeState(false, false, null, "probe output exceeded safety bound");
        }

        if (result.Command!.ExitCode == 0)
        {
            var version = FirstUseful(result.Command.StandardOutput, result.Command.StandardError, string.Empty)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            return new ProbeState(true, false, NullIfBlank(version), "available");
        }

        var detail = FirstUseful(result.Command.StandardError, result.Command.StandardOutput, "probe failed");
        var code = ClassifyFailure(detail);
        return new ProbeState(
            false,
            code is RemoteErrorCode.PermissionDenied or RemoteErrorCode.SudoRequired,
            null,
            detail);
    }

    private async Task<ReadResult> ReadAsync(
        IRemoteCommandExecutor executor,
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        IReadOnlySet<int>? acceptedExitCodes = null)
    {
        var result = await executor.ExecuteAsync(
            new RemoteCommandSpec(executable, arguments, _options.CommandTimeout, OperationRisk.ReadOnly, StableEnvironment),
            cancellationToken).ConfigureAwait(false);
        if (result.Error is not null)
        {
            return new ReadResult(null, result.Error);
        }

        if (OutputTooLarge(result.Command!))
        {
            return new ReadResult(null, new RemoteError(
                RemoteErrorCode.CapabilityUnavailable,
                "Package-manager output exceeded the configured safety bound."));
        }

        var accepted = acceptedExitCodes ?? new HashSet<int> { 0 };
        if (!accepted.Contains(result.Command!.ExitCode))
        {
            var detail = FirstUseful(result.Command.StandardError, result.Command.StandardOutput, "Package-manager read command failed.");
            return new ReadResult(null, new RemoteError(ClassifyFailure(detail), detail));
        }

        return new ReadResult(result.Command.StandardOutput, null);
    }

    private async Task<PackageMutationResult> VerifySuccessAsync(
        ServerProfile profile,
        PackageMutationPreview preview,
        CancellationToken cancellationToken)
    {
        PackageInventoryResult verification;
        try
        {
            verification = await InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Ambiguous("Package command returned success, but post-verification was cancelled. Refresh before retrying.");
        }

        if (verification.Snapshot is not { Status: PackageManagerRuntimeStatus.Available } after ||
            after.ActiveManager != preview.Request.Manager)
        {
            return Ambiguous(
                "Package command returned success, but package-manager state could not be re-read reliably.",
                verification.Error?.TechnicalDetails);
        }

        var matches = preview.Request.Kind switch
        {
            PackageMutationKind.RefreshMetadata => true,
            PackageMutationKind.Install => preview.Request.PackageNames.All(name => IsInstalled(after, name)),
            PackageMutationKind.Upgrade => preview.BoundPackages.All(bound =>
                after.Packages.Any(item =>
                    string.Equals(item.Name, bound.Name, StringComparison.Ordinal) &&
                    string.Equals(item.InstalledVersion, bound.CandidateVersion, StringComparison.Ordinal))),
            PackageMutationKind.Remove => preview.Request.PackageNames.All(name => !IsInstalled(after, name)),
            _ => false,
        };
        if (!matches)
        {
            return new PackageMutationResult(
                false,
                true,
                "Package command returned success, but normalized post-state did not match the exact previewed mutation.",
                new RemoteError(RemoteErrorCode.AmbiguousState, "Package post-verification did not match."),
                after);
        }

        return new PackageMutationResult(
            true,
            false,
            "Package operation completed and normalized package state was verified.",
            null,
            after);
    }

    private async Task<PackageMutationResult> VerifyDeterministicFailureAsync(
        ServerProfile profile,
        PackageInventorySnapshot before,
        RemoteError error)
    {
        PackageInventoryResult verification;
        try
        {
            verification = await InspectAsync(profile, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            return Ambiguous(
                "The package command reported failure, but live package state could not be verified. Refresh before retrying.",
                error.TechnicalDetails);
        }

        if (verification.Snapshot is not { Status: PackageManagerRuntimeStatus.Available } after ||
            after.ActiveManager != before.ActiveManager)
        {
            return Ambiguous(
                "The package command reported failure, but package-manager state could not be verified.",
                verification.Error?.TechnicalDetails ?? error.TechnicalDetails);
        }

        if (!string.Equals(StateFingerprint(before), StateFingerprint(after), StringComparison.Ordinal))
        {
            return new PackageMutationResult(
                false,
                true,
                "The package command reported failure, but normalized package/update state changed. Completion is ambiguous; refresh before retrying.",
                new RemoteError(RemoteErrorCode.AmbiguousState, "Deterministic package failure was followed by state drift."),
                after);
        }

        return new PackageMutationResult(false, false, error.Message, error, after);
    }

    private static PackageMutationRequest NormalizeRequestCore(PackageMutationRequest request, int maximumPackages)
    {
        ArgumentNullException.ThrowIfNull(request);
        var names = (request.PackageNames ?? [])
            .Select(item => item?.Trim() ?? string.Empty)
            .ToArray();

        if (request.Kind == PackageMutationKind.RefreshMetadata)
        {
            if (names.Length != 0)
            {
                throw new ArgumentException("Metadata refresh must not carry package identities.", nameof(request));
            }

            return request with { PackageNames = [] };
        }

        if (names.Length == 0)
        {
            throw new ArgumentException("Install, upgrade and remove require explicit selected package identities.", nameof(request));
        }

        if (names.Length > maximumPackages)
        {
            throw new ArgumentException($"At most {maximumPackages} packages may be changed in one guarded operation.", nameof(request));
        }

        foreach (var name in names)
        {
            if (!SafePackageNameRegex().IsMatch(name) || name.StartsWith('-', StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unsafe or unsupported package identity '{name}'.", nameof(request));
            }
        }

        return request with
        {
            PackageNames = names
                .Distinct(StringComparer.Ordinal)
                .OrderBy(item => item, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    private static RemoteError? ValidatePreconditions(
        PackageMutationRequest request,
        IReadOnlyList<PackageInfo> packages)
    {
        if (packages.Count != request.PackageNames.Count)
        {
            return new RemoteError(RemoteErrorCode.PathNotFound, "One or more selected package identities could not be resolved exactly.");
        }

        foreach (var name in request.PackageNames)
        {
            var package = packages.Single(item => string.Equals(item.Name, name, StringComparison.Ordinal));
            switch (request.Kind)
            {
                case PackageMutationKind.Install when package.IsInstalled:
                    return new RemoteError(RemoteErrorCode.PathConflict, $"Package '{name}' is already installed.");
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

    private static PackageCommandPlan BuildCommand(PackageMutationRequest request, string privilegeExecutable)
    {
        var arguments = new List<string> { "-n" };
        if (request.Manager == PackageManagerKind.Apt)
        {
            arguments.Add("apt-get");
            switch (request.Kind)
            {
                case PackageMutationKind.RefreshMetadata:
                    arguments.Add("update");
                    return new PackageCommandPlan(privilegeExecutable, arguments, OperationRisk.Mutating);
                case PackageMutationKind.Install:
                    arguments.AddRange(["-y", "--no-install-recommends", "install"]);
                    arguments.AddRange(request.PackageNames);
                    return new PackageCommandPlan(privilegeExecutable, arguments, OperationRisk.Mutating);
                case PackageMutationKind.Upgrade:
                    arguments.AddRange(["-y", "--only-upgrade", "install"]);
                    arguments.AddRange(request.PackageNames);
                    return new PackageCommandPlan(privilegeExecutable, arguments, OperationRisk.Mutating);
                case PackageMutationKind.Remove:
                    arguments.AddRange(["-y", "remove"]);
                    arguments.AddRange(request.PackageNames);
                    return new PackageCommandPlan(privilegeExecutable, arguments, OperationRisk.Destructive);
            }
        }
        else
        {
            arguments.AddRange(["dnf", "-q"]);
            switch (request.Kind)
            {
                case PackageMutationKind.RefreshMetadata:
                    arguments.Add("makecache");
                    return new PackageCommandPlan(privilegeExecutable, arguments, OperationRisk.Mutating);
                case PackageMutationKind.Install:
                    arguments.AddRange(["-y", "install"]);
                    arguments.AddRange(request.PackageNames);
                    return new PackageCommandPlan(privilegeExecutable, arguments, OperationRisk.Mutating);
                case PackageMutationKind.Upgrade:
                    arguments.AddRange(["-y", "upgrade"]);
                    arguments.AddRange(request.PackageNames);
                    return new PackageCommandPlan(privilegeExecutable, arguments, OperationRisk.Mutating);
                case PackageMutationKind.Remove:
                    arguments.AddRange(["-y", "remove"]);
                    arguments.AddRange(request.PackageNames);
                    return new PackageCommandPlan(privilegeExecutable, arguments, OperationRisk.Destructive);
            }
        }

        throw new ArgumentOutOfRangeException(nameof(request));
    }

    private static IReadOnlyList<PackageInfo> ParseDnfAvailable(string output, IReadOnlyList<string> expectedNames)
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

            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length < 3)
            {
                continue;
            }

            var dot = fields[0].LastIndexOf('.');
            if (dot <= 0 || dot == fields[0].Length - 1)
            {
                continue;
            }

            var name = fields[0][..dot];
            if (!expected.Contains(name))
            {
                continue;
            }

            packages.Add(new PackageInfo(
                name,
                null,
                fields[1],
                fields[0][(dot + 1)..],
                fields[2],
                IsSecurityRepository(fields[2])
                    ? PackageUpdateClassification.Security
                    : PackageUpdateClassification.Unknown));
        }

        return packages
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
    }

    private static IReadOnlyList<PackageInfo> MergeInstalledAndUpdates(
        IReadOnlyList<PackageInfo> installed,
        IReadOnlyList<PackageUpdateRow> updates)
    {
        var byName = updates
            .GroupBy(item => item.Name, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        return installed
            .Select(package => byName.TryGetValue(package.Name, out var update)
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

    private static PackageImpactHint AnalyzeImpact(PackageMutationRequest request)
    {
        if (request.Kind == PackageMutationKind.RefreshMetadata)
        {
            return new PackageImpactHint(false, false, "Metadata refresh updates package indexes only; it never applies package upgrades.");
        }

        var reboot = request.PackageNames.Any(IsKernelPackage);
        var restart = request.PackageNames.Any(IsServiceSensitivePackage);
        var selected = string.Join(", ", request.PackageNames);
        var message = reboot
            ? $"Selected packages ({selected}) include a kernel package; a reboot may be required after the operation."
            : restart
                ? $"Selected packages ({selected}) include service/runtime packages; service restarts may be required."
                : $"No deterministic reboot/service-restart hint is available for the selected packages ({selected}).";
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

    private bool OutputTooLarge(RemoteCommandResult command) =>
        command.StandardOutput.Length > _options.MaximumOutputCharacters ||
        command.StandardError.Length > _options.MaximumOutputCharacters;

    private static bool IsAvailable(PackageManagerObservation observation) =>
        observation.ManagerExecutableAvailable &&
        observation.DatabaseExecutableAvailable &&
        !observation.PermissionDenied;

    private static bool IsInstalled(PackageInventorySnapshot snapshot, string name) =>
        snapshot.Packages.Any(item => string.Equals(item.Name, name, StringComparison.Ordinal) && item.IsInstalled);

    private static PackageInventoryResult Snapshot(
        PackageManagerRuntimeStatus status,
        PackageManagerKind? manager,
        IReadOnlyList<PackageInfo> packages,
        IReadOnlyList<PackageManagerObservation> observations,
        string detail) =>
        new(
            new PackageInventorySnapshot(status, manager, packages, observations, detail, DateTimeOffset.UtcNow),
            null);

    private static string StateFingerprint(PackageInventorySnapshot snapshot)
    {
        var builder = new StringBuilder();
        builder.Append(snapshot.Status).Append('|').Append(snapshot.ActiveManager).Append('\n');
        foreach (var observation in snapshot.Observations.OrderBy(item => item.Manager))
        {
            builder.Append(observation.Manager).Append('|')
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

    private static string PreviewFingerprint(PackageMutationPreview preview) =>
        Sha256(string.Join(
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
            preview.DisplayCommand));

    private static string Display(string executable, IReadOnlyList<string> arguments) =>
        executable + " " + string.Join(' ', arguments.Select(TokenDisplay));

    private static string TokenDisplay(string value) =>
        value.All(character => char.IsLetterOrDigit(character) || "-._/:=@+".Contains(character))
            ? value
            : "[token]";

    private static RemoteErrorCode ClassifyFailure(string detail)
    {
        if (detail.Contains("password is required", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not in the sudoers", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("a terminal is required", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.SudoRequired;
        }

        if (detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("could not open lock file", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PermissionDenied;
        }

        if (detail.Contains("unable to locate package", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("no match for argument", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathNotFound;
        }

        return RemoteErrorCode.CommandFailed;
    }

    private static bool IsAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or
            RemoteErrorCode.CommandTimeout or
            RemoteErrorCode.OperationCancelled or
            RemoteErrorCode.ConnectionFailed;

    private static bool IsSecurityRepository(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        (value.Contains("security", StringComparison.OrdinalIgnoreCase) ||
         value.Contains("-sec", StringComparison.OrdinalIgnoreCase));

    private static string[] NormalizeLines(string value) =>
        (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries);

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

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

    internal sealed record PackageCommandPlan(
        string Executable,
        IReadOnlyList<string> Arguments,
        OperationRisk Risk);

    private sealed record ProbeState(
        bool Available,
        bool PermissionDenied,
        string? Version,
        string Detail);

    private sealed record ReadResult(string? Output, RemoteError? Error);

    private sealed record InventoryRead(
        IReadOnlyList<PackageInfo> Packages,
        string Detail,
        RemoteError? Error);

    private sealed record PackageResolution(
        IReadOnlyList<PackageInfo> Packages,
        RemoteError? Error);
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
                : result with { Message = result.Message + " Audit persistence failed; do not repeat the package operation solely for audit." };
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
            var identity = preview.Request.Kind == PackageMutationKind.RefreshMetadata
                ? preview.Request.Manager.ToString()
                : string.Join(',', preview.Request.PackageNames);
            var entry = OperationAuditEntry.Create(
                "package-administration",
                $"Package {preview.Request.Kind} requested via {preview.Request.Manager}: {identity}",
                preview.Risk,
                outcome,
                $"{profile.Username}@{profile.Host}:{profile.Port} package-manager:{preview.Request.Manager} packages:{identity}");
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
