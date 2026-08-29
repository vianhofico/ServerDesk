using ServerDesk.Application.Databases;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseRuntimeTests
{
    [Fact]
    public async Task MissingEnginesRemainDistinctAndUseReadOnlyStableLocale()
    {
        var state = new FakeState();
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Snapshot);
        Assert.Equal(4, result.Snapshot.Engines.Count);
        Assert.All(result.Snapshot.Engines, engine =>
            Assert.Equal(DatabaseEngineRuntimeStatus.CliUnavailable, engine.Status));
        Assert.False(result.Snapshot.HasSupportedEngine);
        Assert.All(state.Commands, AssertReadOnlyStableLocale);
    }

    [Fact]
    public async Task ActivePostgresAndRedisAreNormalizedWithoutDatabaseConnectionCommands()
    {
        var state = new FakeState
        {
            PostgresVersion = "postgres (PostgreSQL) 17.4",
            RedisVersion = "Redis server v=8.0.2 sha=00000000:0 malloc=jemalloc bits=64 build=fixture",
        };
        state.Services["postgresql.service"] = ("loaded", "active", "running");
        state.Services["redis-server.service"] = ("loaded", "active", "running");
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var postgres = Assert.Single(result.Snapshot!.Engines, engine => engine.Engine == DatabaseEngineKind.PostgreSql);
        Assert.Equal(DatabaseEngineRuntimeStatus.Active, postgres.Status);
        Assert.Equal("postgresql.service", postgres.ServiceUnit);
        Assert.Equal("postgresql.service", postgres.JournalUnit);
        Assert.Contains("17.4", postgres.Version, StringComparison.Ordinal);

        var redis = Assert.Single(result.Snapshot.Engines, engine => engine.Engine == DatabaseEngineKind.Redis);
        Assert.Equal(DatabaseEngineRuntimeStatus.Active, redis.Status);
        Assert.Equal("redis-server.service", redis.ServiceUnit);
        Assert.Equal(2, result.Snapshot.ActiveEngineCount);
        Assert.DoesNotContain(state.Commands, command =>
            command.Executable is "psql" or "mysql" or "redis-cli");
        Assert.All(state.Commands, AssertReadOnlyStableLocale);
    }

    [Fact]
    public async Task SystemdRuntimeIsDetectedEvenWhenServerBinaryIsNotInPath()
    {
        var state = new FakeState();
        state.Services["postgresql.service"] = ("loaded", "active", "running");
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var postgres = Assert.Single(result.Snapshot!.Engines, engine => engine.Engine == DatabaseEngineKind.PostgreSql);
        Assert.Equal(DatabaseEngineRuntimeStatus.Active, postgres.Status);
        Assert.True(postgres.IsInstalled);
        Assert.Null(postgres.Version);
        Assert.Equal("postgresql.service", postgres.ServiceUnit);
        Assert.Contains("not in the remote command path", postgres.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MariaDbReportedThroughMysqldIsNotMisclassifiedAsMySql()
    {
        var state = new FakeState
        {
            MySqlDVersion = "mysqld  Ver 11.4.5-MariaDB for debian-linux-gnu on x86_64 (Debian 13)",
        };
        state.Services["mysql.service"] = ("loaded", "active", "running");
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var maria = Assert.Single(result.Snapshot!.Engines, engine => engine.Engine == DatabaseEngineKind.MariaDb);
        Assert.True(maria.IsInstalled);
        Assert.Contains("MariaDB", maria.Version, StringComparison.OrdinalIgnoreCase);
        var mysql = Assert.Single(result.Snapshot.Engines, engine => engine.Engine == DatabaseEngineKind.MySql);
        Assert.Equal(DatabaseEngineRuntimeStatus.CliUnavailable, mysql.Status);
        Assert.Contains("does not classify", mysql.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InactiveServiceIsNotReportedAsActive()
    {
        var state = new FakeState
        {
            MySqlDVersion = "mysqld  Ver 8.4.4 for Linux on x86_64 (MySQL Community Server - GPL)",
        };
        state.Services["mysql.service"] = ("loaded", "inactive", "dead");
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var mysql = Assert.Single(result.Snapshot!.Engines, engine => engine.Engine == DatabaseEngineKind.MySql);
        Assert.Equal(DatabaseEngineRuntimeStatus.Inactive, mysql.Status);
        Assert.False(mysql.IsActive);
    }

    [Fact]
    public async Task ServicePermissionDeniedIsNotCollapsedIntoInstalledState()
    {
        var state = new FakeState
        {
            RedisVersion = "Redis server v=8.0.2",
        };
        state.ServiceErrors["redis-server.service"] =
            new RemoteError(RemoteErrorCode.PermissionDenied, "systemd access denied");
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var redis = Assert.Single(result.Snapshot!.Engines, engine => engine.Engine == DatabaseEngineKind.Redis);
        Assert.Equal(DatabaseEngineRuntimeStatus.PermissionDenied, redis.Status);
        Assert.Null(redis.JournalUnit);
        Assert.Contains("could not be read", redis.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TransportFailureStopsSnapshotInsteadOfPretendingEngineIsMissing()
    {
        var state = new FakeState
        {
            PostgresError = new RemoteError(RemoteErrorCode.ConnectionFailed, "transport unavailable"),
        };
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Snapshot);
        Assert.Equal(RemoteErrorCode.ConnectionFailed, result.Error!.Code);
    }

    [Fact]
    public async Task VersionProbeOutputIsBoundedAndNeverParsedAsInventory()
    {
        var state = new FakeState
        {
            PostgresVersion = new string('x', 4096),
        };
        var service = new DatabaseRuntimeService(
            new FakeCommandFactory(state),
            new DatabaseRuntimeOptions(TimeSpan.FromSeconds(2), 128));

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var postgres = Assert.Single(result.Snapshot!.Engines, engine => engine.Engine == DatabaseEngineKind.PostgreSql);
        Assert.Equal(DatabaseEngineRuntimeStatus.ProbeFailed, postgres.Status);
        Assert.Contains("safety limit", postgres.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertReadOnlyStableLocale(RemoteCommandSpec command)
    {
        Assert.Equal(OperationRisk.ReadOnly, command.Risk);
        Assert.NotNull(command.Environment);
        Assert.Equal("C", command.Environment["LC_ALL"]);
        Assert.DoesNotContain(command.Arguments, argument => argument.Contains(';'));
    }

    private static DatabaseRuntimeService CreateService(FakeState state) =>
        new(new FakeCommandFactory(state), DatabaseRuntimeOptions.Default);

    private static ServerProfile Profile() =>
        ServerProfile.Create("Database runtime", "example.invalid", 22, "dev");

    private sealed class FakeState
    {
        public List<RemoteCommandSpec> Commands { get; } = [];
        public string? PostgresVersion { get; set; }
        public RemoteError? PostgresError { get; set; }
        public string? MySqlDVersion { get; set; }
        public string? MariaDbDVersion { get; set; }
        public string? RedisVersion { get; set; }
        public Dictionary<string, (string Load, string Active, string Sub)> Services { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, RemoteError> ServiceErrors { get; } = new(StringComparer.Ordinal);
    }

    private sealed class FakeCommandFactory : IRemoteCommandExecutorFactory
    {
        private readonly FakeState _state;

        public FakeCommandFactory(FakeState state) => _state = state;

        public IRemoteCommandExecutor Create(ServerProfile profile) =>
            new FakeCommandExecutor(profile.Id, _state);
    }

    private sealed class FakeCommandExecutor : IRemoteCommandExecutor
    {
        private readonly FakeState _state;

        public FakeCommandExecutor(Guid serverProfileId, FakeState state)
        {
            ServerProfileId = serverProfileId;
            _state = state;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state.Commands.Add(command);
            if (command.Executable == "postgres")
            {
                return Task.FromResult(_state.PostgresError is not null
                    ? RemoteExecutionResult.Failure(_state.PostgresError)
                    : Version(_state.PostgresVersion, "postgres"));
            }

            if (command.Executable == "mysqld")
            {
                return Task.FromResult(Version(_state.MySqlDVersion, "mysqld"));
            }

            if (command.Executable == "mariadbd")
            {
                return Task.FromResult(Version(_state.MariaDbDVersion, "mariadbd"));
            }

            if (command.Executable == "redis-server")
            {
                return Task.FromResult(Version(_state.RedisVersion, "redis-server"));
            }

            if (command.Executable == "systemctl" && command.Arguments.Count >= 2 && command.Arguments[0] == "show")
            {
                var unit = command.Arguments[1];
                if (_state.ServiceErrors.TryGetValue(unit, out var serviceError))
                {
                    return Task.FromResult(RemoteExecutionResult.Failure(serviceError));
                }

                if (!_state.Services.TryGetValue(unit, out var service))
                {
                    return Task.FromResult(Success(1, string.Empty, $"Unit {unit} could not be found."));
                }

                return Task.FromResult(Success(
                    0,
                    $"LoadState={service.Load}\nActiveState={service.Active}\nSubState={service.Sub}\n",
                    string.Empty));
            }

            throw new InvalidOperationException($"Unexpected database runtime command: {command.Executable} {string.Join(' ', command.Arguments)}");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static RemoteExecutionResult Version(string? value, string executable) =>
            value is null
                ? RemoteExecutionResult.Failure(new RemoteError(RemoteErrorCode.CommandNotFound, $"{executable} missing"))
                : Success(0, value, string.Empty);

        private static RemoteExecutionResult Success(int exitCode, string stdout, string stderr) =>
            RemoteExecutionResult.Success(new RemoteCommandResult(exitCode, stdout, stderr, TimeSpan.Zero));
    }
}
