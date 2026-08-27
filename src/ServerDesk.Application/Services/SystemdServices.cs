using System.Globalization;
using System.Text.RegularExpressions;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Services;

public sealed record SystemdServiceOptions(string Executable, bool UseSudoForMutations)
{
    public static SystemdServiceOptions Default { get; } = new("systemctl", true);
}

public sealed record ServerServiceInfo(
    string Unit,
    string Description,
    string LoadState,
    string ActiveState,
    string SubState,
    string EnabledState,
    int? MainProcessId,
    string StatusText)
{
    public bool IsActive => string.Equals(ActiveState, "active", StringComparison.OrdinalIgnoreCase);
}

public sealed record ServerServiceQueryResult(
    IReadOnlyList<ServerServiceInfo> Services,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null;
}

public enum ServerServiceAction
{
    Start,
    Stop,
    Restart,
    Reload,
    Enable,
    Disable,
}

public sealed record ServerServiceActionResult(
    bool IsSuccess,
    RemoteError? Error,
    string Message,
    ServerServiceInfo? VerifiedService = null);

public interface IServerServiceManager
{
    Task<ServerServiceQueryResult> ListAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);

    Task<ServerServiceQueryResult> GetAsync(
        ServerProfile profile,
        string unit,
        CancellationToken cancellationToken = default);

    Task<ServerServiceActionResult> ExecuteAsync(
        ServerProfile profile,
        string unit,
        ServerServiceAction action,
        CancellationToken cancellationToken = default);
}

public sealed class SystemdServiceManager : IServerServiceManager
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _commandExecutorFactory;
    private readonly SystemdServiceOptions _options;

    public SystemdServiceManager(
        IRemoteCommandExecutorFactory commandExecutorFactory,
        SystemdServiceOptions options)
    {
        _commandExecutorFactory = commandExecutorFactory ?? throw new ArgumentNullException(nameof(commandExecutorFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ArgumentException.ThrowIfNullOrWhiteSpace(_options.Executable);
    }

    public async Task<ServerServiceQueryResult> ListAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _commandExecutorFactory.Create(profile);

        var unitsExecution = await executor.ExecuteAsync(
                ReadOnlyCommand(
                    "list-units",
                    "--type=service",
                    "--all",
                    "--no-legend",
                    "--no-pager",
                    "--plain"),
                cancellationToken)
            .ConfigureAwait(false);
        var unitsFailure = ReadFailure(unitsExecution, "Unable to list systemd service units.");
        if (unitsFailure is not null)
        {
            return new ServerServiceQueryResult([], unitsFailure);
        }

        var filesExecution = await executor.ExecuteAsync(
                ReadOnlyCommand(
                    "list-unit-files",
                    "--type=service",
                    "--no-legend",
                    "--no-pager"),
                cancellationToken)
            .ConfigureAwait(false);
        var filesFailure = ReadFailure(filesExecution, "Unable to read systemd unit-file states.");
        if (filesFailure is not null)
        {
            return new ServerServiceQueryResult([], filesFailure);
        }

        try
        {
            var units = SystemdServiceParser.ParseListUnits(unitsExecution.Command!.StandardOutput);
            var enabled = SystemdServiceParser.ParseUnitFiles(filesExecution.Command!.StandardOutput);
            var merged = units
                .Select(unit => unit with
                {
                    EnabledState = enabled.TryGetValue(unit.Unit, out var state) ? state : "unknown",
                })
                .OrderBy(unit => unit.Unit, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new ServerServiceQueryResult(merged, null);
        }
        catch (FormatException exception)
        {
            return new ServerServiceQueryResult(
                [],
                new RemoteError(
                    RemoteErrorCode.ParseFailed,
                    "ServerDesk could not parse systemd service output.",
                    exception.Message));
        }
    }

    public async Task<ServerServiceQueryResult> GetAsync(
        ServerProfile profile,
        string unit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateUnitName(unit);
        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                ReadOnlyCommand(
                    "show",
                    unit,
                    "--no-pager",
                    "--property=Id,Description,LoadState,ActiveState,SubState,UnitFileState,MainPID,StatusText"),
                cancellationToken)
            .ConfigureAwait(false);
        var failure = ReadFailure(execution, $"Unable to inspect systemd unit '{unit}'.");
        if (failure is not null)
        {
            return new ServerServiceQueryResult([], failure);
        }

        try
        {
            var service = SystemdServiceParser.ParseShow(execution.Command!.StandardOutput);
            if (!string.Equals(service.Unit, unit, StringComparison.Ordinal))
            {
                return new ServerServiceQueryResult(
                    [],
                    new RemoteError(
                        RemoteErrorCode.ParseFailed,
                        $"systemctl returned details for '{service.Unit}' instead of requested unit '{unit}'."));
            }

            return new ServerServiceQueryResult([service], null);
        }
        catch (FormatException exception)
        {
            return new ServerServiceQueryResult(
                [],
                new RemoteError(
                    RemoteErrorCode.ParseFailed,
                    $"ServerDesk could not parse systemd details for '{unit}'.",
                    exception.Message));
        }
    }

    public async Task<ServerServiceActionResult> ExecuteAsync(
        ServerProfile profile,
        string unit,
        ServerServiceAction action,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateUnitName(unit);
        var verb = action switch
        {
            ServerServiceAction.Start => "start",
            ServerServiceAction.Stop => "stop",
            ServerServiceAction.Restart => "restart",
            ServerServiceAction.Reload => "reload",
            ServerServiceAction.Enable => "enable",
            ServerServiceAction.Disable => "disable",
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };
        var risk = IsDisruptive(action) ? OperationRisk.Destructive : OperationRisk.Mutating;

        await using var executor = _commandExecutorFactory.Create(profile);
        var execution = await executor.ExecuteAsync(
                MutationCommand(verb, unit, risk),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            var error = IsAmbiguous(execution.Error.Code)
                ? new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    $"ServerDesk lost a reliable completion signal while running systemctl {verb} for '{unit}'. Refresh service state before deciding whether to retry.",
                    execution.Error.TechnicalDetails)
                : execution.Error;
            return new ServerServiceActionResult(false, error, error.Message);
        }

        var command = execution.Command!;
        if (command.ExitCode != 0)
        {
            var detail = FirstUseful(command.StandardError, command.StandardOutput, $"systemctl {verb} failed for '{unit}'.");
            var error = new RemoteError(ClassifyMutationFailure(detail), detail);
            return new ServerServiceActionResult(false, error, detail);
        }

        var verification = await GetAsync(profile, unit, cancellationToken).ConfigureAwait(false);
        if (!verification.IsSuccess || verification.Services.Count != 1)
        {
            var detail = verification.Error?.Message ?? "The unit could not be re-read after mutation.";
            var error = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"systemctl {verb} returned success for '{unit}', but ServerDesk could not verify the resulting state. {detail}");
            return new ServerServiceActionResult(false, error, error.Message);
        }

        var verified = verification.Services[0];
        if (!MatchesExpectedState(verified, action))
        {
            var error = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"systemctl {verb} returned success for '{unit}', but verified state is active={verified.ActiveState}, sub={verified.SubState}, enabled={verified.EnabledState}. Refresh before retrying.");
            return new ServerServiceActionResult(false, error, error.Message, verified);
        }

        return new ServerServiceActionResult(
            true,
            null,
            $"systemctl {verb} completed and verified for '{unit}'.",
            verified);
    }

    public static void ValidateUnitName(string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        if (!SystemdServiceParser.ServiceUnitNameRegex().IsMatch(unit))
        {
            throw new ArgumentException(
                "Service unit names must be a single .service identifier without path separators, whitespace or shell syntax.",
                nameof(unit));
        }
    }

    public static bool IsDisruptive(ServerServiceAction action) =>
        action is ServerServiceAction.Stop or ServerServiceAction.Restart or ServerServiceAction.Disable;

    private RemoteCommandSpec ReadOnlyCommand(params string[] arguments) =>
        new(
            _options.Executable,
            arguments,
            TimeSpan.FromSeconds(20),
            OperationRisk.ReadOnly,
            StableEnvironment);

    private RemoteCommandSpec MutationCommand(string verb, string unit, OperationRisk risk)
    {
        if (_options.UseSudoForMutations)
        {
            return new RemoteCommandSpec(
                "sudo",
                ["-n", _options.Executable, verb, "--", unit],
                TimeSpan.FromSeconds(30),
                risk,
                StableEnvironment);
        }

        return new RemoteCommandSpec(
            _options.Executable,
            [verb, "--", unit],
            TimeSpan.FromSeconds(30),
            risk,
            StableEnvironment);
    }

    private static RemoteError? ReadFailure(RemoteExecutionResult execution, string fallback)
    {
        if (execution.Error is not null)
        {
            return execution.Error;
        }

        var command = execution.Command!;
        if (command.ExitCode == 0)
        {
            return null;
        }

        var detail = FirstUseful(command.StandardError, command.StandardOutput, fallback);
        var code = detail.Contains("not found", StringComparison.OrdinalIgnoreCase)
            ? RemoteErrorCode.CommandNotFound
            : detail.Contains("not been booted with systemd", StringComparison.OrdinalIgnoreCase) ||
              detail.Contains("Failed to connect to bus", StringComparison.OrdinalIgnoreCase)
                ? RemoteErrorCode.CapabilityUnavailable
                : detail.Contains("not-found", StringComparison.OrdinalIgnoreCase) ||
                  detail.Contains("could not be found", StringComparison.OrdinalIgnoreCase)
                    ? RemoteErrorCode.PathNotFound
                    : detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                        ? RemoteErrorCode.PermissionDenied
                        : RemoteErrorCode.CommandFailed;
        return new RemoteError(code, detail);
    }

    private static RemoteErrorCode ClassifyMutationFailure(string detail)
    {
        if (detail.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("sudoers", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not allowed", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.SudoRequired;
        }

        if (detail.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("not-found", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PathNotFound;
        }

        if (detail.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
            detail.Contains("access denied", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteErrorCode.PermissionDenied;
        }

        return RemoteErrorCode.CommandFailed;
    }

    private static bool MatchesExpectedState(ServerServiceInfo service, ServerServiceAction action) =>
        action switch
        {
            ServerServiceAction.Start or ServerServiceAction.Restart => service.IsActive,
            ServerServiceAction.Stop => !service.IsActive,
            ServerServiceAction.Enable => service.EnabledState is "enabled" or "enabled-runtime" or "linked" or "linked-runtime",
            ServerServiceAction.Disable => service.EnabledState is "disabled" or "indirect" or "static",
            ServerServiceAction.Reload => service.LoadState != "not-found",
            _ => false,
        };

    private static bool IsAmbiguous(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or RemoteErrorCode.CommandTimeout or RemoteErrorCode.OperationCancelled;

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        return !string.IsNullOrWhiteSpace(second) ? second.Trim() : fallback;
    }
}

public static partial class SystemdServiceParser
{
    [GeneratedRegex(@"^[A-Za-z0-9_.@:\\-]+\.service$", RegexOptions.CultureInvariant)]
    public static partial Regex ServiceUnitNameRegex();

    [GeneratedRegex(@"^\s*(\S+)\s+(\S+)\s+(\S+)\s+(\S+)\s+(.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex UnitRowRegex();

    [GeneratedRegex(@"^\s*(\S+)\s+(\S+)(?:\s+\S+)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex UnitFileRowRegex();

    public static IReadOnlyList<ServerServiceInfo> ParseListUnits(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return [];
        }

        var result = new List<ServerServiceInfo>();
        foreach (var line in Lines(output))
        {
            var match = UnitRowRegex().Match(line);
            if (!match.Success)
            {
                throw new FormatException($"Malformed systemctl list-units row: '{line}'.");
            }

            var unit = match.Groups[1].Value;
            if (!ServiceUnitNameRegex().IsMatch(unit))
            {
                throw new FormatException($"Unexpected service unit identifier: '{unit}'.");
            }

            result.Add(new ServerServiceInfo(
                unit,
                match.Groups[5].Value.Trim(),
                match.Groups[2].Value,
                match.Groups[3].Value,
                match.Groups[4].Value,
                "unknown",
                null,
                string.Empty));
        }

        return result;
    }

    public static IReadOnlyDictionary<string, string> ParseUnitFiles(string output)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(output))
        {
            return result;
        }

        foreach (var line in Lines(output))
        {
            var match = UnitFileRowRegex().Match(line);
            if (!match.Success)
            {
                throw new FormatException($"Malformed systemctl list-unit-files row: '{line}'.");
            }

            var unit = match.Groups[1].Value;
            if (ServiceUnitNameRegex().IsMatch(unit))
            {
                result[unit] = match.Groups[2].Value;
            }
        }

        return result;
    }

    public static ServerServiceInfo ParseShow(string output)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in Lines(output))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                throw new FormatException($"Malformed systemctl show property: '{line}'.");
            }

            properties[line[..separator]] = line[(separator + 1)..];
        }

        var unit = Required(properties, "Id");
        if (!ServiceUnitNameRegex().IsMatch(unit))
        {
            throw new FormatException($"Unexpected service unit identifier: '{unit}'.");
        }

        var mainPidRaw = Required(properties, "MainPID");
        if (!int.TryParse(mainPidRaw, NumberStyles.None, CultureInfo.InvariantCulture, out var mainPid) || mainPid < 0)
        {
            throw new FormatException($"Invalid MainPID value '{mainPidRaw}'.");
        }

        return new ServerServiceInfo(
            unit,
            Required(properties, "Description"),
            Required(properties, "LoadState"),
            Required(properties, "ActiveState"),
            Required(properties, "SubState"),
            Required(properties, "UnitFileState"),
            mainPid == 0 ? null : mainPid,
            properties.TryGetValue("StatusText", out var status) ? status : string.Empty);
    }

    private static string Required(IReadOnlyDictionary<string, string> properties, string key) =>
        properties.TryGetValue(key, out var value)
            ? value
            : throw new FormatException($"systemctl show output is missing required property '{key}'.");

    private static IEnumerable<string> Lines(string output) =>
        output.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
