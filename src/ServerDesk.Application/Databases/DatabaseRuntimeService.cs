using System.Text;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Databases;

public interface IDatabaseRuntimeService
{
    Task<DatabaseRuntimeResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);
}

public sealed class DatabaseRuntimeService : IDatabaseRuntimeService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private static readonly string[] PostgreSqlUnits = ["postgresql.service"];
    private static readonly string[] MySqlUnits = ["mysql.service", "mysqld.service"];
    private static readonly string[] MariaDbUnits = ["mariadb.service"];
    private static readonly string[] RedisUnits = ["redis-server.service", "redis.service"];

    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly DatabaseRuntimeOptions _options;

    public DatabaseRuntimeService(
        IRemoteCommandExecutorFactory commandFactory,
        DatabaseRuntimeOptions options)
    {
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<DatabaseRuntimeResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        await using var executor = _commandFactory.Create(profile);
        var observations = new List<DatabaseEngineObservation>();

        var postgres = await ProbeExecutableAsync(
            executor,
            DatabaseEngineKind.PostgreSql,
            "postgres",
            ["--version"],
            PostgreSqlUnits,
            cancellationToken).ConfigureAwait(false);
        if (postgres.Error is not null)
        {
            return new DatabaseRuntimeResult(null, postgres.Error);
        }

        observations.Add(postgres.Observation!);

        var mysql = await ProbeExecutableAsync(
            executor,
            DatabaseEngineKind.MySql,
            "mysqld",
            ["--version"],
            MySqlUnits,
            cancellationToken).ConfigureAwait(false);
        if (mysql.Error is not null)
        {
            return new DatabaseRuntimeResult(null, mysql.Error);
        }

        var mariadb = await ProbeExecutableAsync(
            executor,
            DatabaseEngineKind.MariaDb,
            "mariadbd",
            ["--version"],
            MariaDbUnits,
            cancellationToken).ConfigureAwait(false);
        if (mariadb.Error is not null)
        {
            return new DatabaseRuntimeResult(null, mariadb.Error);
        }

        AddMySqlFamilyObservations(observations, mysql.Observation!, mariadb.Observation!);

        var redis = await ProbeExecutableAsync(
            executor,
            DatabaseEngineKind.Redis,
            "redis-server",
            ["--version"],
            RedisUnits,
            cancellationToken).ConfigureAwait(false);
        if (redis.Error is not null)
        {
            return new DatabaseRuntimeResult(null, redis.Error);
        }

        observations.Add(redis.Observation!);
        return new DatabaseRuntimeResult(
            new DatabaseRuntimeSnapshot(observations, DateTimeOffset.UtcNow),
            null);
    }

    private async Task<ProbeResult> ProbeExecutableAsync(
        IRemoteCommandExecutor executor,
        DatabaseEngineKind engine,
        string executable,
        IReadOnlyList<string> versionArguments,
        IReadOnlyList<string> serviceUnits,
        CancellationToken cancellationToken)
    {
        var versionResult = await executor.ExecuteAsync(
            ReadOnly(executable, versionArguments),
            cancellationToken).ConfigureAwait(false);
        if (versionResult.Error is not null)
        {
            if (versionResult.Error.Code == RemoteErrorCode.CommandNotFound)
            {
                return ProbeResult.Success(new DatabaseEngineObservation(
                    engine,
                    DatabaseEngineRuntimeStatus.CliUnavailable,
                    executable,
                    null,
                    null,
                    null,
                    null,
                    null,
                    $"{executable} is not available in the remote command path."));
            }

            if (versionResult.Error.Code == RemoteErrorCode.PermissionDenied)
            {
                return ProbeResult.Success(new DatabaseEngineObservation(
                    engine,
                    DatabaseEngineRuntimeStatus.PermissionDenied,
                    executable,
                    null,
                    null,
                    null,
                    null,
                    null,
                    versionResult.Error.Message));
            }

            return ProbeResult.Failure(versionResult.Error);
        }

        var versionCommand = versionResult.Command!;
        if (OutputTooLarge(versionCommand))
        {
            return ProbeResult.Success(ProbeFailed(
                engine,
                executable,
                null,
                "Version probe output exceeded the configured safety limit."));
        }

        if (versionCommand.ExitCode != 0)
        {
            return ProbeResult.Success(ProbeFailed(
                engine,
                executable,
                null,
                FirstUseful(versionCommand.StandardError, versionCommand.StandardOutput, "Version probe failed.")));
        }

        var version = NormalizeSingleLine(FirstUseful(
            versionCommand.StandardOutput,
            versionCommand.StandardError,
            "version-unavailable"));
        var serviceProbe = await ProbeServiceAsync(executor, serviceUnits, cancellationToken).ConfigureAwait(false);
        if (serviceProbe.Error is not null)
        {
            return ProbeResult.Failure(serviceProbe.Error);
        }

        var service = serviceProbe.Observation;
        if (service is null)
        {
            return ProbeResult.Success(new DatabaseEngineObservation(
                engine,
                DatabaseEngineRuntimeStatus.Installed,
                executable,
                version,
                null,
                null,
                null,
                null,
                "The server binary is available, but no supported systemd service unit was found. Runtime state remains unverified."));
        }

        var status = service.ActiveState switch
        {
            "active" => DatabaseEngineRuntimeStatus.Active,
            "inactive" or "failed" or "deactivating" => DatabaseEngineRuntimeStatus.Inactive,
            "permission-denied" => DatabaseEngineRuntimeStatus.PermissionDenied,
            "probe-failed" => DatabaseEngineRuntimeStatus.ProbeFailed,
            _ => DatabaseEngineRuntimeStatus.Installed,
        };
        var journalUnit = status is DatabaseEngineRuntimeStatus.Active or
            DatabaseEngineRuntimeStatus.Inactive or
            DatabaseEngineRuntimeStatus.Installed
                ? service.Unit
                : null;
        var detail = status switch
        {
            DatabaseEngineRuntimeStatus.PermissionDenied =>
                $"{service.Unit} exists, but its runtime state could not be read with the current account.",
            DatabaseEngineRuntimeStatus.ProbeFailed =>
                $"{service.Unit} runtime state could not be normalized safely ({service.SubState}).",
            _ => $"systemd reports {service.Unit} as {service.ActiveState}/{service.SubState}.",
        };
        return ProbeResult.Success(new DatabaseEngineObservation(
            engine,
            status,
            executable,
            version,
            service.Unit,
            service.ActiveState,
            service.SubState,
            journalUnit,
            detail));
    }

    private async Task<ServiceProbeResult> ProbeServiceAsync(
        IRemoteCommandExecutor executor,
        IReadOnlyList<string> units,
        CancellationToken cancellationToken)
    {
        foreach (var unit in units)
        {
            var result = await executor.ExecuteAsync(
                ReadOnly(
                    "systemctl",
                    [
                        "show",
                        unit,
                        "--property=LoadState",
                        "--property=ActiveState",
                        "--property=SubState",
                        "--no-pager",
                    ]),
                cancellationToken).ConfigureAwait(false);
            if (result.Error is not null)
            {
                if (result.Error.Code == RemoteErrorCode.CommandNotFound)
                {
                    return ServiceProbeResult.Success(null);
                }

                if (result.Error.Code == RemoteErrorCode.PermissionDenied)
                {
                    return ServiceProbeResult.Success(new ServiceObservation(
                        unit,
                        "permission-denied",
                        "unknown"));
                }

                return ServiceProbeResult.Failure(result.Error);
            }

            var command = result.Command!;
            if (OutputTooLarge(command))
            {
                return ServiceProbeResult.Success(new ServiceObservation(
                    unit,
                    "probe-failed",
                    "output-too-large"));
            }

            var text = string.Join('\n', command.StandardOutput, command.StandardError);
            if (command.ExitCode != 0 && LooksLikeUnitMissing(text))
            {
                continue;
            }

            if (command.ExitCode != 0)
            {
                return ServiceProbeResult.Success(new ServiceObservation(
                    unit,
                    "probe-failed",
                    NormalizeSingleLine(FirstUseful(command.StandardError, command.StandardOutput, "unknown"))));
            }

            var properties = ParseSystemdProperties(command.StandardOutput);
            if (properties.TryGetValue("LoadState", out var loadState) &&
                loadState.Equals("not-found", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return ServiceProbeResult.Success(new ServiceObservation(
                unit,
                properties.GetValueOrDefault("ActiveState", "unknown"),
                properties.GetValueOrDefault("SubState", "unknown")));
        }

        return ServiceProbeResult.Success(null);
    }

    private static void AddMySqlFamilyObservations(
        ICollection<DatabaseEngineObservation> observations,
        DatabaseEngineObservation mysql,
        DatabaseEngineObservation mariadb)
    {
        var mysqlLooksLikeMariaDb = mysql.IsInstalled &&
            mysql.Version?.Contains("MariaDB", StringComparison.OrdinalIgnoreCase) == true;
        if (mysqlLooksLikeMariaDb)
        {
            if (!mariadb.IsInstalled)
            {
                observations.Add(mysql with
                {
                    Engine = DatabaseEngineKind.MariaDb,
                    Detail = mysql.Detail + " The mysqld version identifies this runtime as MariaDB.",
                });
            }
            else
            {
                observations.Add(mariadb);
            }

            observations.Add(new DatabaseEngineObservation(
                DatabaseEngineKind.MySql,
                DatabaseEngineRuntimeStatus.CliUnavailable,
                "mysqld",
                null,
                null,
                null,
                null,
                null,
                "mysqld is present but identifies itself as MariaDB, so ServerDesk does not classify a separate MySQL runtime."));
            return;
        }

        observations.Add(mysql);
        observations.Add(mariadb);
    }

    private RemoteCommandSpec ReadOnly(string executable, IReadOnlyList<string> arguments) =>
        new(
            executable,
            arguments,
            _options.CommandTimeout,
            OperationRisk.ReadOnly,
            StableEnvironment);

    private bool OutputTooLarge(RemoteCommandResult command) =>
        Encoding.UTF8.GetByteCount(command.StandardOutput ?? string.Empty) > _options.MaximumOutputBytes ||
        Encoding.UTF8.GetByteCount(command.StandardError ?? string.Empty) > _options.MaximumOutputBytes;

    private static DatabaseEngineObservation ProbeFailed(
        DatabaseEngineKind engine,
        string executable,
        string? version,
        string detail) =>
        new(
            engine,
            DatabaseEngineRuntimeStatus.ProbeFailed,
            executable,
            version,
            null,
            null,
            null,
            null,
            detail);

    private static Dictionary<string, string> ParseSystemdProperties(string output)
    {
        var properties = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in NormalizeLines(output))
        {
            var separator = raw.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            properties[raw[..separator].Trim()] = raw[(separator + 1)..].Trim();
        }

        return properties;
    }

    private static bool LooksLikeUnitMissing(string value) =>
        value.Contains("could not be found", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("not-found", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("not found", StringComparison.OrdinalIgnoreCase);

    private static string[] NormalizeLines(string value) =>
        (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string NormalizeSingleLine(string value)
    {
        var line = NormalizeLines(value).FirstOrDefault() ?? string.Empty;
        return line.Length <= 512 ? line : line[..512];
    }

    private static string FirstUseful(string first, string second, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first.Trim();
        }

        return !string.IsNullOrWhiteSpace(second) ? second.Trim() : fallback;
    }

    private sealed record ProbeResult(DatabaseEngineObservation? Observation, RemoteError? Error)
    {
        public static ProbeResult Success(DatabaseEngineObservation observation) => new(observation, null);

        public static ProbeResult Failure(RemoteError error) => new(null, error);
    }

    private sealed record ServiceObservation(string Unit, string ActiveState, string SubState);

    private sealed record ServiceProbeResult(ServiceObservation? Observation, RemoteError? Error)
    {
        public static ServiceProbeResult Success(ServiceObservation? observation) => new(observation, null);

        public static ServiceProbeResult Failure(RemoteError error) => new(null, error);
    }
}
