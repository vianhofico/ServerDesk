using System.Text;
using ServerDesk.Application.Databases;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseBackupServiceTests
{
    private const string Secret = "db-password-'quoted'-$HOME";
    private static readonly string Sha = new('A', 64);

    [Fact]
    public async Task PostgreSqlBackupUsesSensitiveStdinAndPersistsOnlyVerifiedManifest()
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);
        var outputPath = string.Empty;
        fixture.Commands.Handler = spec =>
        {
            if (IsStat(spec, "/var/backups"))
            {
                return Success("directory\t4096");
            }

            if (spec.Executable == "pg_dump" && spec.Arguments.SequenceEqual(["--version"]))
            {
                return Success("pg_dump (PostgreSQL) 18.6\n");
            }

            if (spec.Executable == "pg_restore" && spec.Arguments.SequenceEqual(["--version"]))
            {
                return Success("pg_restore (PostgreSQL) 18.6\n");
            }

            if (spec.Risk == OperationRisk.Mutating)
            {
                Assert.Equal("sh", spec.Executable);
                Assert.Contains("pg_dump", spec.Arguments);
                Assert.NotNull(spec.StandardInput);
                Assert.DoesNotContain(Secret, spec.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain(Secret, string.Join('\n', spec.Arguments), StringComparison.Ordinal);
                Assert.DoesNotContain(Secret, string.Join('\n', spec.Environment?.Values ?? []), StringComparison.Ordinal);
                var fileIndex = spec.Arguments.IndexOf("--file");
                Assert.True(fileIndex >= 0);
                outputPath = spec.Arguments[fileIndex + 1];
                return Success();
            }

            if (!string.IsNullOrEmpty(outputPath) && IsStat(spec, outputPath))
            {
                return Success("regular file\t12345");
            }

            if (spec.Executable == "sha256sum")
            {
                return Success($"{Sha}  {outputPath}\n");
            }

            if (spec.Executable == "sh" && spec.Arguments.Contains("serverdesk-pg-verify"))
            {
                return Success();
            }

            throw new InvalidOperationException($"Unexpected command: {spec}");
        };

        var result = await fixture.Service.CreateAsync(
            fixture.Server,
            new DatabaseBackupRequest(fixture.Profile.Id, "appdb", "/var/backups"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.HistoryPersisted);
        Assert.NotNull(result.Manifest);
        Assert.Equal(DatabaseEngineKind.PostgreSql, result.Manifest.Engine);
        Assert.Equal("appdb", result.Manifest.DatabaseName);
        Assert.Equal(DatabaseBackupFormat.PostgreSqlCustom, result.Manifest.Format);
        Assert.Equal(12345, result.Manifest.Verification.SizeBytes);
        Assert.Equal(Sha, result.Manifest.Verification.Sha256);
        Assert.Equal(result.Manifest, Assert.Single(fixture.Manifests.Items));
        Assert.StartsWith("/var/backups/serverdesk-db-backup-", result.Manifest.BackupPath.Value, StringComparison.Ordinal);
        Assert.EndsWith(".dump", result.Manifest.BackupPath.Value, StringComparison.Ordinal);

        var dump = Assert.Single(fixture.Commands.Commands, command => command.Risk == OperationRisk.Mutating);
        await using var input = new MemoryStream();
        await dump.StandardInput!.WriteToAsync(input, TestContext.Current.CancellationToken);
        var encoded = Encoding.UTF8.GetString(input.ToArray()).Trim();
        Assert.Equal(Secret, Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
    }

    [Theory]
    [InlineData(DatabaseEngineKind.MySql, "mysqldump", "MySQL dump", DatabaseBackupFormat.MySqlSql)]
    [InlineData(DatabaseEngineKind.MariaDb, "mariadb-dump", "MariaDB dump", DatabaseBackupFormat.MariaDbSql)]
    public async Task MySqlFamilyBackupUsesExplicitResultFileAndBoundedStructuralVerification(
        DatabaseEngineKind engine,
        string tool,
        string header,
        DatabaseBackupFormat format)
    {
        var fixture = CreateFixture(engine);
        var outputPath = string.Empty;
        fixture.Commands.Handler = spec =>
        {
            if (IsStat(spec, "/srv/db-backups"))
            {
                return Success("directory\t4096");
            }

            if (spec.Executable == tool && spec.Arguments.SequenceEqual(["--version"]))
            {
                return Success($"{tool} Ver 8.4 fixture\n");
            }

            if (spec.Risk == OperationRisk.Mutating)
            {
                Assert.Equal("sh", spec.Executable);
                Assert.Contains(tool, spec.Arguments);
                Assert.Contains("--no-defaults", spec.Arguments);
                Assert.Contains("--databases", spec.Arguments);
                Assert.Contains("appdb", spec.Arguments);
                var resultFile = Assert.Single(spec.Arguments, argument => argument.StartsWith("--result-file=", StringComparison.Ordinal));
                outputPath = resultFile["--result-file=".Length..];
                Assert.NotNull(spec.StandardInput);
                Assert.DoesNotContain(Secret, spec.ToString(), StringComparison.Ordinal);
                return Success();
            }

            if (!string.IsNullOrEmpty(outputPath) && IsStat(spec, outputPath))
            {
                return Success("regular file\t2048");
            }

            if (spec.Executable == "sha256sum")
            {
                return Success($"{Sha}  {outputPath}\n");
            }

            if (spec.Executable == "head")
            {
                Assert.Equal("8192", spec.Arguments[1]);
                return Success($"-- {header} fixture\nCREATE DATABASE appdb;\n");
            }

            throw new InvalidOperationException($"Unexpected command: {spec}");
        };

        var result = await fixture.Service.CreateAsync(
            fixture.Server,
            new DatabaseBackupRequest(fixture.Profile.Id, "appdb", "/srv/db-backups"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(format, result.Manifest!.Format);
        Assert.EndsWith(".sql", result.Manifest.BackupPath.Value, StringComparison.Ordinal);
        Assert.Contains(header, result.Manifest.Verification.StructuralCheck, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RedisBackupFailsClosedBeforeSecretOrRemoteCommand()
    {
        var fixture = CreateFixture(DatabaseEngineKind.Redis);

        var result = await fixture.Service.CreateAsync(
            fixture.Server,
            new DatabaseBackupRequest(fixture.Profile.Id, "0", "/var/backups"),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.Unsupported);
        Assert.Empty(fixture.Commands.Commands);
        Assert.Equal(0, fixture.Secrets.ReadCount);
        Assert.Empty(fixture.Manifests.Items);
    }

    [Fact]
    public async Task NetworkInterruptionAfterDumpDispatchIsAmbiguousAndNeverRetried()
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);
        var outputPath = string.Empty;
        var dumpCount = 0;
        fixture.Commands.Handler = spec =>
        {
            if (IsStat(spec, "/var/backups"))
            {
                return Success("directory\t4096");
            }

            if (spec.Executable == "pg_dump" && spec.Arguments.SequenceEqual(["--version"]))
            {
                return Success("pg_dump (PostgreSQL) 18.6\n");
            }

            if (spec.Executable == "pg_restore" && spec.Arguments.SequenceEqual(["--version"]))
            {
                return Success("pg_restore (PostgreSQL) 18.6\n");
            }

            if (spec.Risk == OperationRisk.Mutating)
            {
                dumpCount++;
                var fileIndex = spec.Arguments.IndexOf("--file");
                outputPath = spec.Arguments[fileIndex + 1];
                return RemoteExecutionResult.Failure(
                    new RemoteError(RemoteErrorCode.NetworkInterrupted, "transport interrupted"));
            }

            if (!string.IsNullOrEmpty(outputPath) && IsStat(spec, outputPath))
            {
                return Success("regular file\t99");
            }

            throw new InvalidOperationException($"Unexpected command: {spec}");
        };

        var result = await fixture.Service.CreateAsync(
            fixture.Server,
            new DatabaseBackupRequest(fixture.Profile.Id, "appdb", "/var/backups"),
            TestContext.Current.CancellationToken);

        Assert.True(result.AmbiguousState);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error!.Code);
        Assert.Equal(1, dumpCount);
        Assert.Empty(fixture.Manifests.Items);
        Assert.Contains(fixture.Commands.Commands, command => IsStat(command, outputPath));
    }

    [Theory]
    [InlineData("../tmp")]
    [InlineData("/var/backups/../escape")]
    [InlineData("relative")]
    public async Task InvalidDestinationIsRejectedBeforeSecretAndRemoteCommands(string destination)
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);

        var result = await fixture.Service.CreateAsync(
            fixture.Server,
            new DatabaseBackupRequest(fixture.Profile.Id, "appdb", destination),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.InvalidEndpoint, result.Error!.Code);
        Assert.Empty(fixture.Commands.Commands);
        Assert.Equal(0, fixture.Secrets.ReadCount);
    }

    [Theory]
    [InlineData(" appdb")]
    [InlineData("appdb ")]
    [InlineData("app\nname")]
    public async Task AmbiguousDatabaseIdentityIsRejectedBeforeRemoteCommands(string databaseName)
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);

        var result = await fixture.Service.CreateAsync(
            fixture.Server,
            new DatabaseBackupRequest(fixture.Profile.Id, databaseName, "/var/backups"),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.Commands.Commands);
        Assert.Equal(0, fixture.Secrets.ReadCount);
    }

    private static Fixture CreateFixture(DatabaseEngineKind engine)
    {
        var server = ServerProfile.Create("Backup fixture", "server.example", 22, "operator");
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForDatabaseProfile(profileId);
        var profile = DatabaseConnectionProfile.Create(
            profileId,
            server.Id,
            $"{engine} fixture",
            engine,
            "127.0.0.1",
            DatabaseConnectionProfile.DefaultPortFor(engine),
            engine == DatabaseEngineKind.Redis ? null : "appdb",
            "dbuser",
            DatabaseAuthenticationKind.Password,
            reference);
        var profiles = new ProfileRepository(profile);
        var secrets = new SecretStore(reference, Secret);
        var commands = new CommandFactory();
        var manifests = new ManifestRepository();
        var service = new DatabaseBackupService(
            profiles,
            secrets,
            commands,
            manifests,
            DatabaseBackupOptions.Default);
        return new Fixture(server, profile, secrets, commands, manifests, service);
    }

    private static bool IsStat(RemoteCommandSpec spec, string path) =>
        spec.Executable == "stat" && spec.Arguments.LastOrDefault() == path;

    private static RemoteExecutionResult Success(string output = "") =>
        RemoteExecutionResult.Success(new RemoteCommandResult(0, output, string.Empty, TimeSpan.Zero));

    private sealed record Fixture(
        ServerProfile Server,
        DatabaseConnectionProfile Profile,
        SecretStore Secrets,
        CommandFactory Commands,
        ManifestRepository Manifests,
        DatabaseBackupService Service);

    private sealed class ProfileRepository : IDatabaseProfileRepository
    {
        private readonly DatabaseConnectionProfile _profile;

        public ProfileRepository(DatabaseConnectionProfile profile) => _profile = profile;

        public ValueTask<IReadOnlyList<DatabaseConnectionProfile>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DatabaseConnectionProfile>>([_profile]);

        public ValueTask<IReadOnlyList<DatabaseConnectionProfile>> ListForServerAsync(Guid serverProfileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DatabaseConnectionProfile>>(serverProfileId == _profile.ServerProfileId ? [_profile] : []);

        public ValueTask<DatabaseConnectionProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DatabaseConnectionProfile?>(id == _profile.Id ? _profile : null);

        public ValueTask UpsertAsync(DatabaseConnectionProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class SecretStore : ISecretStore
    {
        private readonly SecretReference _reference;
        private readonly string _secret;

        public SecretStore(SecretReference reference, string secret)
        {
            _reference = reference;
            _secret = secret;
        }

        public int ReadCount { get; private set; }

        public ValueTask SetAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ValueTask.FromResult<string?>(reference == _reference ? _secret : null);
        }

        public ValueTask DeleteAsync(SecretReference reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class CommandFactory : IRemoteCommandExecutorFactory
    {
        public Func<RemoteCommandSpec, RemoteExecutionResult> Handler { get; set; } =
            spec => throw new InvalidOperationException($"Unexpected command: {spec}");

        public List<RemoteCommandSpec> Commands { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile) => new Executor(this, profile.Id);

        private sealed class Executor : IRemoteCommandExecutor
        {
            private readonly CommandFactory _owner;

            public Executor(CommandFactory owner, Guid serverProfileId)
            {
                _owner = owner;
                ServerProfileId = serverProfileId;
            }

            public Guid ServerProfileId { get; }

            public Task<RemoteExecutionResult> ExecuteAsync(RemoteCommandSpec command, CancellationToken cancellationToken = default)
            {
                _owner.Commands.Add(command);
                return Task.FromResult(_owner.Handler(command));
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class ManifestRepository : IDatabaseBackupManifestRepository
    {
        public List<DatabaseBackupManifest> Items { get; } = [];

        public ValueTask<IReadOnlyList<DatabaseBackupManifest>> ListForServerAsync(Guid serverProfileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DatabaseBackupManifest>>(Items.Where(item => item.ServerProfileId == serverProfileId).ToArray());

        public ValueTask<DatabaseBackupManifest?> GetAsync(Guid backupId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DatabaseBackupManifest?>(Items.FirstOrDefault(item => item.BackupId == backupId));

        public ValueTask AddAsync(DatabaseBackupManifest manifest, CancellationToken cancellationToken = default)
        {
            Items.Add(manifest);
            return ValueTask.CompletedTask;
        }
    }
}
