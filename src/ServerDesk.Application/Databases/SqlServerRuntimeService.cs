using System.Text;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Databases;

public sealed class SqlServerAwareDatabaseRuntimeService : IDatabaseRuntimeService
{
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IDatabaseRuntimeService _inner;
    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly DatabaseRuntimeOptions _options;

    public SqlServerAwareDatabaseRuntimeService(
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

        var sqlServer = await InspectSqlServerAsync(profile, cancellationToken).ConfigureAwait(false);
        if (sqlServer.Error is not null)
        {
            return new DatabaseRuntimeResult(null, sqlServer.Error);
        }

        return new DatabaseRuntimeResult(
            new DatabaseRuntimeSnapshot(
                baseline.Snapshot.Engines.Concat([sqlServer.Observation!]).ToArray(),
                DateTimeOffset.UtcNow),
            null);
    }

    private async Task<SqlServerProbeResult> InspectSqlServerAsync(
        ServerProfile profile,
        CancellationToken cancellationToken)
    {
        await using var executor = _commandFactory.Create(profile);
        var service = await executor.ExecuteAsync(
            ReadOnly(
                "systemctl",
                [
                    "show",
                    "mssql-server.service",
                    "--property=LoadState",
                    "--property=ActiveState",
                    "--property=SubState",
                    "--no-pager",
                ]),
            cancellationToken).ConfigureAwait(false);
        if (service.Error is not null && service.Error.Code != RemoteErrorCode.CommandNotFound)
        {
            if (service.Error.Code == RemoteErrorCode.PermissionDenied)
            {
                return SqlServerProbeResult.Success(new DatabaseEngineObservation(
                    DatabaseEngineKind.SqlServer,
                    DatabaseEngineRuntimeStatus.PermissionDenied,
                    "/opt/mssql/bin/sqlservr",
                    null,
                    "mssql-server.service",
                    null,
                    null,
                    null,
                    "mssql-server.service exists or may exist, but runtime state could not be read with the current account."));
            }

            return SqlServerProbeResult.Failure(service.Error);
        }

        var serviceProperties = service.Error is null && service.Command is { ExitCode: 0 } serviceCommand
            ? ParseProperties(Bounded(serviceCommand.StandardOutput))
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var serviceLoaded = serviceProperties.TryGetValue("LoadState", out var loadState) &&
            !string.Equals(loadState, "not-found", StringComparison.OrdinalIgnoreCase);

        var package = await ReadPackageVersionAsync(executor, cancellationToken).ConfigureAwait(false);
        if (package.Error is not null)
        {
            return SqlServerProbeResult.Failure(package.Error);
        }

        var installed = serviceLoaded || !string.IsNullOrWhiteSpace(package.Version);
        if (!installed)
        {
            return SqlServerProbeResult.Success(new DatabaseEngineObservation(
                DatabaseEngineKind.SqlServer,
                DatabaseEngineRuntimeStatus.CliUnavailable,
                "/opt/mssql/bin/sqlservr",
                null,
                null,
                null,
                null,
                null,
                "Microsoft SQL Server package/service was not detected. Client tooling alone is not treated as a running SQL Server instance."));
        }

        var activeState = serviceProperties.GetValueOrDefault("ActiveState", "unknown");
        var subState = serviceProperties.GetValueOrDefault("SubState", "unknown");
        var status = activeState switch
        {
            "active" => DatabaseEngineRuntimeStatus.Active,
            "inactive" or "failed" or "deactivating" => DatabaseEngineRuntimeStatus.Inactive,
            _ => DatabaseEngineRuntimeStatus.Installed,
        };
        var detail = status == DatabaseEngineRuntimeStatus.Active
            ? $"systemd reports mssql-server.service as {activeState}/{subState}. Exact server engine version is authenticated and verified through the SQL Server diagnostic path."
            : $"Microsoft SQL Server is installed; systemd state is {activeState}/{subState}. Exact live engine version remains unknown until authenticated diagnostics succeed.";
        return SqlServerProbeResult.Success(new DatabaseEngineObservation(
            DatabaseEngineKind.SqlServer,
            status,
            "/opt/mssql/bin/sqlservr",
            package.Version,
            serviceLoaded ? "mssql-server.service" : null,
            serviceLoaded ? activeState : null,
            serviceLoaded ? subState : null,
            serviceLoaded ? "mssql-server.service" : null,
            detail));
    }

    private async Task<PackageRead> ReadPackageVersionAsync(
        IRemoteCommandExecutor executor,
        CancellationToken cancellationToken)
    {
        var debian = await executor.ExecuteAsync(
            ReadOnly("dpkg-query", ["-W", "-f=${Version}", "mssql-server"]),
            cancellationToken).ConfigureAwait(false);
        if (debian.Error is null && debian.Command is { ExitCode: 0 } debianCommand)
        {
            return new PackageRead(FirstLine(Bounded(debianCommand.StandardOutput)), null);
        }

        if (debian.Error is not null && debian.Error.Code is not RemoteErrorCode.CommandNotFound)
        {
            return new PackageRead(null, debian.Error);
        }

        var rpm = await executor.ExecuteAsync(
            ReadOnly("rpm", ["-q", "--qf", "%{VERSION}-%{RELEASE}", "mssql-server"]),
            cancellationToken).ConfigureAwait(false);
        if (rpm.Error is null && rpm.Command is { ExitCode: 0 } rpmCommand)
        {
            return new PackageRead(FirstLine(Bounded(rpmCommand.StandardOutput)), null);
        }

        if (rpm.Error is not null && rpm.Error.Code is not RemoteErrorCode.CommandNotFound)
        {
            return new PackageRead(null, rpm.Error);
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

    private sealed record PackageRead(string? Version, RemoteError? Error);

    private sealed record SqlServerProbeResult(DatabaseEngineObservation? Observation, RemoteError? Error)
    {
        public static SqlServerProbeResult Success(DatabaseEngineObservation observation) => new(observation, null);

        public static SqlServerProbeResult Failure(RemoteError error) => new(null, error);
    }
}
