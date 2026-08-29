using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Firewall;

public interface IFirewallManager
{
    Task<FirewallInventoryResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);
}

public sealed class FirewallInventoryService : IFirewallManager
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly FirewallInventoryOptions _options;

    public FirewallInventoryService(
        IRemoteCommandExecutorFactory commandFactory,
        FirewallInventoryOptions options)
    {
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<FirewallInventoryResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _commandFactory.Create(profile);

        var ufw = await InspectUfwAsync(executor, cancellationToken).ConfigureAwait(false);
        if (ufw.Error is not null)
        {
            return new FirewallInventoryResult(null, ufw.Error);
        }

        var firewalld = await InspectFirewalldAsync(executor, cancellationToken).ConfigureAwait(false);
        if (firewalld.Error is not null)
        {
            return new FirewallInventoryResult(null, firewalld.Error);
        }

        var observations = new[] { ufw.Observation!, firewalld.Observation! };
        var active = observations
            .Where(item => item.CliAvailable && item.IsActive && !item.PermissionDenied)
            .ToArray();

        if (active.Length > 1)
        {
            return Snapshot(
                FirewallRuntimeStatus.AdapterConflict,
                FirewallAdapterKind.None,
                [],
                observations,
                "Multiple firewall adapters report an active runtime. ServerDesk will not guess which tool owns policy.");
        }

        if (observations.Any(item => item.PermissionDenied))
        {
            var adapter = observations.First(item => item.PermissionDenied).Adapter;
            return Snapshot(
                FirewallRuntimeStatus.PermissionDenied,
                adapter,
                [],
                observations,
                $"{adapter} is present but firewall state/rules could not be read with the current account.");
        }

        if (active.Length == 1)
        {
            return Snapshot(
                FirewallRuntimeStatus.Available,
                active[0].Adapter,
                active[0].Rules,
                observations,
                active[0].Detail);
        }

        if (observations.Any(IsProbeFailure))
        {
            return Snapshot(
                FirewallRuntimeStatus.ProbeFailed,
                FirewallAdapterKind.None,
                [],
                observations,
                "A firewall CLI was found, but its runtime state could not be normalized safely.");
        }

        var available = observations.Where(item => item.CliAvailable).ToArray();
        if (available.Length > 0)
        {
            return Snapshot(
                FirewallRuntimeStatus.Disabled,
                available[0].Adapter,
                [],
                observations,
                "No supported firewall adapter reports an active runtime.");
        }

        return Snapshot(
            FirewallRuntimeStatus.CliUnavailable,
            FirewallAdapterKind.None,
            [],
            observations,
            "Neither UFW nor firewalld CLI is available.");
    }

    private async Task<AdapterResult> InspectUfwAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var version = await executor.ExecuteAsync(ReadOnly("ufw", ["--version"]), cancellationToken).ConfigureAwait(false);
        if (version.Error is not null)
        {
            return version.Error.Code == RemoteErrorCode.CommandNotFound
                ? AdapterResult.Success(Unavailable(FirewallAdapterKind.Ufw, "ufw-cli-unavailable"))
                : AdapterResult.Failure(version.Error);
        }

        if (version.Command!.ExitCode != 0)
        {
            return AdapterResult.Success(ProbeFailure(
                FirewallAdapterKind.Ufw,
                FirstUseful(version.Command.StandardError, version.Command.StandardOutput, "ufw-version-probe-failed")));
        }

        var status = await executor.ExecuteAsync(ReadOnly("ufw", ["status", "numbered"]), cancellationToken).ConfigureAwait(false);
        if (status.Error is not null)
        {
            if (status.Error.Code == RemoteErrorCode.PermissionDenied)
            {
                return AdapterResult.Success(PermissionDenied(FirewallAdapterKind.Ufw, VersionText(version.Command), status.Error.Message));
            }

            return AdapterResult.Failure(status.Error);
        }

        if (status.Command!.ExitCode != 0)
        {
            var detail = FirstUseful(status.Command.StandardError, status.Command.StandardOutput, "ufw-status-probe-failed");
            return LooksLikePermissionDenied(detail)
                ? AdapterResult.Success(PermissionDenied(FirewallAdapterKind.Ufw, VersionText(version.Command), detail))
                : AdapterResult.Success(ProbeFailure(FirewallAdapterKind.Ufw, detail, VersionText(version.Command)));
        }

        try
        {
            return AdapterResult.Success(FirewallParser.ParseUfw(
                VersionText(version.Command),
                status.Command.StandardOutput,
                _options));
        }
        catch (FormatException exception)
        {
            return AdapterResult.Success(ProbeFailure(FirewallAdapterKind.Ufw, exception.Message, VersionText(version.Command)));
        }
    }

    private async Task<AdapterResult> InspectFirewalldAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var version = await executor.ExecuteAsync(ReadOnly("firewall-cmd", ["--version"]), cancellationToken).ConfigureAwait(false);
        if (version.Error is not null)
        {
            return version.Error.Code == RemoteErrorCode.CommandNotFound
                ? AdapterResult.Success(Unavailable(FirewallAdapterKind.Firewalld, "firewalld-cli-unavailable"))
                : AdapterResult.Failure(version.Error);
        }

        if (version.Command!.ExitCode != 0)
        {
            return AdapterResult.Success(ProbeFailure(
                FirewallAdapterKind.Firewalld,
                FirstUseful(version.Command.StandardError, version.Command.StandardOutput, "firewalld-version-probe-failed")));
        }

        var state = await executor.ExecuteAsync(ReadOnly("firewall-cmd", ["--state"]), cancellationToken).ConfigureAwait(false);
        if (state.Error is not null)
        {
            if (state.Error.Code == RemoteErrorCode.PermissionDenied)
            {
                return AdapterResult.Success(PermissionDenied(FirewallAdapterKind.Firewalld, VersionText(version.Command), state.Error.Message));
            }

            return AdapterResult.Failure(state.Error);
        }

        var stateText = FirewallParser.Sanitize(state.Command!.StandardOutput).Trim();
        var notRunning = stateText.Equals("not running", StringComparison.OrdinalIgnoreCase);
        if (state.Command.ExitCode != 0 && !notRunning)
        {
            var detail = FirstUseful(state.Command.StandardError, state.Command.StandardOutput, "firewalld-state-probe-failed");
            return LooksLikePermissionDenied(detail)
                ? AdapterResult.Success(PermissionDenied(FirewallAdapterKind.Firewalld, VersionText(version.Command), detail))
                : AdapterResult.Success(ProbeFailure(FirewallAdapterKind.Firewalld, detail, VersionText(version.Command)));
        }

        if (notRunning)
        {
            try
            {
                return AdapterResult.Success(FirewallParser.ParseFirewalld(
                    VersionText(version.Command),
                    "not running",
                    string.Empty,
                    _options));
            }
            catch (FormatException exception)
            {
                return AdapterResult.Success(ProbeFailure(FirewallAdapterKind.Firewalld, exception.Message, VersionText(version.Command)));
            }
        }

        var zones = await executor.ExecuteAsync(ReadOnly("firewall-cmd", ["--list-all-zones"]), cancellationToken).ConfigureAwait(false);
        if (zones.Error is not null)
        {
            if (zones.Error.Code == RemoteErrorCode.PermissionDenied)
            {
                return AdapterResult.Success(PermissionDenied(FirewallAdapterKind.Firewalld, VersionText(version.Command), zones.Error.Message));
            }

            return AdapterResult.Failure(zones.Error);
        }

        if (zones.Command!.ExitCode != 0)
        {
            var detail = FirstUseful(zones.Command.StandardError, zones.Command.StandardOutput, "firewalld-zones-probe-failed");
            return LooksLikePermissionDenied(detail)
                ? AdapterResult.Success(PermissionDenied(FirewallAdapterKind.Firewalld, VersionText(version.Command), detail))
                : AdapterResult.Success(ProbeFailure(FirewallAdapterKind.Firewalld, detail, VersionText(version.Command)));
        }

        try
        {
            return AdapterResult.Success(FirewallParser.ParseFirewalld(
                VersionText(version.Command),
                state.Command.StandardOutput,
                zones.Command.StandardOutput,
                _options));
        }
        catch (FormatException exception)
        {
            return AdapterResult.Success(ProbeFailure(FirewallAdapterKind.Firewalld, exception.Message, VersionText(version.Command)));
        }
    }

    private FirewallInventoryResult Snapshot(
        FirewallRuntimeStatus status,
        FirewallAdapterKind adapter,
        IReadOnlyList<FirewallRuleInfo> rules,
        IReadOnlyList<FirewallAdapterObservation> observations,
        string detail) =>
        new(new FirewallInventorySnapshot(status, adapter, rules, observations, detail), null);

    private RemoteCommandSpec ReadOnly(string executable, IReadOnlyList<string> arguments) =>
        new(executable, arguments, _options.CommandTimeout, OperationRisk.ReadOnly, StableEnvironment);

    private static FirewallAdapterObservation Unavailable(FirewallAdapterKind adapter, string detail) =>
        new(adapter, false, false, false, null, detail, [], string.Empty);

    private static FirewallAdapterObservation PermissionDenied(
        FirewallAdapterKind adapter,
        string? version,
        string detail) =>
        new(adapter, true, false, true, version, detail, [], string.Empty);

    private static FirewallAdapterObservation ProbeFailure(
        FirewallAdapterKind adapter,
        string detail,
        string? version = null) =>
        new(adapter, true, false, false, version, "probe-failed: " + FirewallParser.Sanitize(detail), [], string.Empty);

    private static bool IsProbeFailure(FirewallAdapterObservation observation) =>
        observation.Detail.StartsWith("probe-failed:", StringComparison.Ordinal);

    private static string VersionText(RemoteCommandResult command) =>
        FirstUseful(command.StandardOutput, command.StandardError, string.Empty);

    private static bool LooksLikePermissionDenied(string value) =>
        value.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("not permitted", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("must be root", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("you need to be root", StringComparison.OrdinalIgnoreCase);

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return FirewallParser.Sanitize(first.Trim());
        }

        return !string.IsNullOrWhiteSpace(second)
            ? FirewallParser.Sanitize(second.Trim())
            : fallback;
    }

    private sealed record AdapterResult(FirewallAdapterObservation? Observation, RemoteError? Error)
    {
        public static AdapterResult Success(FirewallAdapterObservation observation) => new(observation, null);

        public static AdapterResult Failure(RemoteError error) => new(null, error);
    }
}
