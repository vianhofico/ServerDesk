using System.Text;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Databases;

public sealed class MongoDbAwareDatabaseRuntimeService : IDatabaseRuntimeService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LC_ALL"] = "C",
            ["LANG"] = "C",
        };

    private readonly IDatabaseRuntimeService _inner;
    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly DatabaseRuntimeOptions _options;

    public MongoDbAwareDatabaseRuntimeService(
        IDatabaseRuntimeService inner,
        IRemoteCommandExecutorFactory commandFactory,
        DatabaseRuntimeOptions options)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<DatabaseRuntimeResult> InspectAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var baseline = await _inner.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!baseline.IsSuccess || baseline.Snapshot is null)
        {
            return baseline;
        }

        var mongo = await InspectMongoDbAsync(profile, cancellationToken).ConfigureAwait(false);
        if (mongo.Error is not null || mongo.Observation is null)
        {
            return new DatabaseRuntimeResult(null, mongo.Error ?? new RemoteError(
                RemoteErrorCode.ParseFailed,
                "MongoDB runtime discovery returned no observation."));
        }

        return new DatabaseRuntimeResult(
            new DatabaseRuntimeSnapshot(
                baseline.Snapshot.Engines.Concat([mongo.Observation]).ToArray(),
                DateTimeOffset.UtcNow),
            null);
    }

    private async Task<MongoRuntimeRead> InspectMongoDbAsync(
        ServerProfile profile,
        CancellationToken cancellationToken)
    {
        await using var executor = _commandFactory.Create(profile);
        var mongod = await ReadServiceAsync(executor, "mongod.service", cancellationToken).ConfigureAwait(false);
        if (mongod.Error is not null)
        {
            return MongoRuntimeRead.Fail(mongod.Error);
        }

        var mongos = await ReadServiceAsync(executor, "mongos.service", cancellationToken).ConfigureAwait(false);
        if (mongos.Error is not null)
        {
            return MongoRuntimeRead.Fail(mongos.Error);
        }

        var package = await ReadPackageVersionAsync(executor, cancellationToken).ConfigureAwait(false);
        if (package.Error is not null)
        {
            return MongoRuntimeRead.Fail(package.Error);
        }

        var selected = SelectService(mongod, mongos);
        var installed = selected.Loaded || !string.IsNullOrWhiteSpace(package.Version);
        if (!installed)
        {
            return MongoRuntimeRead.Success(new DatabaseEngineObservation(
                DatabaseEngineKind.MongoDb,
                DatabaseEngineRuntimeStatus.CliUnavailable,
                "/usr/bin/mongod",
                null,
                null,
                null,
                null,
                null,
                "MongoDB server packages/services were not detected. mongosh and MongoDB Database Tools alone are not treated as a running server."));
        }

        var status = selected.ActiveState switch
        {
            "active" => DatabaseEngineRuntimeStatus.Active,
            "inactive" or "failed" or "deactivating" => DatabaseEngineRuntimeStatus.Inactive,
            _ => DatabaseEngineRuntimeStatus.Installed,
        };
        var processName = selected.Unit == "mongos.service" ? "mongos" : "mongod";
        var executable = processName == "mongos" ? "/usr/bin/mongos" : "/usr/bin/mongod";
        var detail = status == DatabaseEngineRuntimeStatus.Active
            ? $"systemd reports {selected.Unit} as {selected.ActiveState}/{selected.SubState}. Exact MongoDB topology and live server version are verified through authenticated tunneled diagnostics."
            : $"MongoDB server is installed; observed service state is {selected.ActiveState}/{selected.SubState}. Live topology remains Unknown until authenticated tunneled diagnostics succeed.";
        return MongoRuntimeRead.Success(new DatabaseEngineObservation(
            DatabaseEngineKind.MongoDb,
            status,
            executable,
            package.Version,
            selected.Loaded ? selected.Unit : null,
            selected.Loaded ? selected.ActiveState : null,
            selected.Loaded ? selected.SubState : null,
            selected.Loaded ? selected.Unit : null,
            detail));
    }

    private async Task<ServiceRead> ReadServiceAsync(
        IRemoteCommandExecutor executor,
        string unit,
        CancellationToken cancellationToken)
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
                return ServiceRead.Missing(unit);
            }

            if (result.Error.Code == RemoteErrorCode.PermissionDenied)
            {
                return new ServiceRead(
                    unit,
                    true,
                    "unknown",
                    "unknown",
                    new RemoteError(
                        RemoteErrorCode.PermissionDenied,
                        $"MongoDB service state for {unit} could not be read with the current account."));
            }

            return new ServiceRead(unit, false, "unknown", "unknown", result.Error);
        }

        if (result.Command is not { ExitCode: 0 } command)
        {
            return ServiceRead.Missing(unit);
        }

        var properties = ParseProperties(Bounded(command.StandardOutput));
        var loaded = properties.TryGetValue("LoadState", out var loadState) &&
            !string.Equals(loadState, "not-found", StringComparison.OrdinalIgnoreCase);
        return new ServiceRead(
            unit,
            loaded,
            properties.GetValueOrDefault("ActiveState", "unknown"),
            properties.GetValueOrDefault("SubState", "unknown"),
            null);
    }

    private async Task<PackageRead> ReadPackageVersionAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        foreach (var packageName in new[] { "mongodb-org-server", "mongodb-org-mongos" })
        {
            var debian = await executor.ExecuteAsync(
                ReadOnly("dpkg-query", ["-W", "-f=${Version}", packageName]),
                cancellationToken).ConfigureAwait(false);
            if (debian.Error is null && debian.Command is { ExitCode: 0 } debianCommand)
            {
                var version = NormalizePackageVersion(FirstLine(Bounded(debianCommand.StandardOutput)));
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return new PackageRead(version, null);
                }
            }
            else if (debian.Error is not null && debian.Error.Code is not RemoteErrorCode.CommandNotFound)
            {
                return new PackageRead(null, debian.Error);
            }
        }

        foreach (var packageName in new[] { "mongodb-org-server", "mongodb-org-mongos" })
        {
            var rpm = await executor.ExecuteAsync(
                ReadOnly("rpm", ["-q", "--qf", "%{VERSION}", packageName]),
                cancellationToken).ConfigureAwait(false);
            if (rpm.Error is null && rpm.Command is { ExitCode: 0 } rpmCommand)
            {
                var version = NormalizePackageVersion(FirstLine(Bounded(rpmCommand.StandardOutput)));
                if (!string.IsNullOrWhiteSpace(version))
                {
                    return new PackageRead(version, null);
                }
            }
            else if (rpm.Error is not null && rpm.Error.Code is not RemoteErrorCode.CommandNotFound)
            {
                return new PackageRead(null, rpm.Error);
            }
        }

        return new PackageRead(null, null);
    }

    private RemoteCommandSpec ReadOnly(string executable, IReadOnlyList<string> arguments) =>
        new(executable, arguments, _options.CommandTimeout, OperationRisk.ReadOnly, StableEnvironment);

    private string Bounded(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= _options.MaximumOutputBytes)
        {
            return value;
        }

        return string.Empty;
    }

    private static ServiceRead SelectService(ServiceRead mongod, ServiceRead mongos)
    {
        if (mongos.Loaded && string.Equals(mongos.ActiveState, "active", StringComparison.Ordinal))
        {
            return mongos;
        }

        if (mongod.Loaded)
        {
            return mongod;
        }

        return mongos.Loaded ? mongos : ServiceRead.Missing("mongod.service");
    }

    private static Dictionary<string, string> ParseProperties(string value)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in value.Replace("\r", string.Empty, StringComparison.Ordinal)
                     .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf('=');
            if (separator > 0)
            {
                result[line[..separator].Trim()] = line[(separator + 1)..].Trim();
            }
        }

        return result;
    }

    private static string? FirstLine(string value) =>
        value.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

    private static string? NormalizePackageVersion(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        var epoch = normalized.IndexOf(':');
        if (epoch >= 0 && epoch + 1 < normalized.Length)
        {
            normalized = normalized[(epoch + 1)..];
        }

        var revision = normalized.IndexOf('-');
        return revision > 0 ? normalized[..revision] : normalized;
    }

    private sealed record PackageRead(string? Version, RemoteError? Error);

    private sealed record ServiceRead(
        string Unit,
        bool Loaded,
        string ActiveState,
        string SubState,
        RemoteError? Error)
    {
        public static ServiceRead Missing(string unit) => new(unit, false, "unknown", "unknown", null);
    }

    private sealed record MongoRuntimeRead(DatabaseEngineObservation? Observation, RemoteError? Error)
    {
        public static MongoRuntimeRead Success(DatabaseEngineObservation observation) => new(observation, null);

        public static MongoRuntimeRead Fail(RemoteError error) => new(null, error);
    }
}
