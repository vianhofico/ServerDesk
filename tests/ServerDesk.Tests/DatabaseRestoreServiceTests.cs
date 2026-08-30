using System.Text;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Databases;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Secrets;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseRestoreServiceTests
{
    private const string Secret = "restore-password-'quoted'-$HOME";
    private static readonly string Sha = new('B', 64);

    [Fact]
    public async Task PostgreSqlPreviewBindsExactVerifiedManifestTargetAndDestructiveCommand()
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);
        ConfigureSuccessfulPostgreSql(fixture);

        var result = await fixture.Service.PreviewAsync(
            fixture.Server,
            Request(fixture),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var preview = Assert.IsType<DatabaseRestorePreview>(result.Preview);
        Assert.Equal(fixture.Manifest.BackupId, preview.Request.BackupId);
        Assert.Equal(fixture.Profile.Id, preview.Request.DatabaseProfileId);
        Assert.Equal("appdb", preview.Request.TargetDatabase);
        Assert.Equal(OperationRisk.Destructive, preview.Risk);
        Assert.True(preview.UsesSensitiveInput);
        Assert.Equal("pg_restore", preview.RestoreTool);
        Assert.Contains("pg_restore", preview.Arguments);
        Assert.Contains("--clean", preview.Arguments);
        Assert.Contains("--single-transaction", preview.Arguments);
        Assert.Contains("appdb", preview.Arguments);
        Assert.Contains(fixture.Manifest.BackupPath.Value, preview.Arguments);
        Assert.Contains("data", preview.DataLossWarning, StringComparison.OrdinalIgnoreCase);
        Assert.False(preview.RollbackAvailable);
        Assert.DoesNotContain(Secret, preview.DisplayCommand, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, string.Join('\n', preview.Arguments), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("otherdb")]
    [InlineData("--help")]
    [InlineData(" appdb")]
    public async Task AlternateOrUnsafeTargetIsRejectedBeforeSecretAndRemoteExecution(string target)
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);

        var result = await fixture.Service.PreviewAsync(
            fixture.Server,
            new DatabaseRestoreRequest(fixture.Profile.Id, fixture.Manifest.BackupId, target),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Empty(fixture.Commands.Commands);
        Assert.Equal(0, fixture.Secrets.ReadCount);
    }

    [Fact]
    public async Task UnverifiedManifestIsRejectedBeforeSecretAndRemoteExecution()
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql, verified: false);

        var result = await fixture.Service.PreviewAsync(
            fixture.Server,
            Request(fixture),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error!.Code);
        Assert.Empty(fixture.Commands.Commands);
        Assert.Equal(0, fixture.Secrets.ReadCount);
    }

    [Fact]
    public async Task RedisRestoreFailsClosedBeforeSecretAndRemoteExecution()
    {
        var fixture = CreateFixture(DatabaseEngineKind.Redis);

        var result = await fixture.Service.PreviewAsync(
            fixture.Server,
            Request(fixture),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.Unsupported);
        Assert.Empty(fixture.Commands.Commands);
        Assert.Equal(0, fixture.Secrets.ReadCount);
    }

    [Fact]
    public async Task ExactPreviewExecutesOnceAndPostVerifiesTargetIdentity()
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);
        ConfigureSuccessfulPostgreSql(fixture);
        var preview = (await fixture.Service.PreviewAsync(
            fixture.Server,
            Request(fixture),
            TestContext.Current.CancellationToken)).Preview!;

        var result = await fixture.Service.ExecuteAsync(
            fixture.Server,
            preview,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.False(result.AmbiguousState);
        Assert.NotNull(result.VerifiedTarget);
        Assert.Equal("appdb", result.VerifiedTarget.DatabaseName);
        Assert.False(result.RollbackAvailable);
        var restore = Assert.Single(fixture.Commands.Commands, command => command.Risk == OperationRisk.Destructive);
        Assert.NotNull(restore.StandardInput);
        Assert.DoesNotContain(Secret, restore.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, string.Join('\n', restore.Arguments), StringComparison.Ordinal);
        await using var input = new MemoryStream();
        await restore.StandardInput!.WriteToAsync(input, TestContext.Current.CancellationToken);
        Assert.Equal(Secret, Encoding.UTF8.GetString(Convert.FromBase64String(Encoding.UTF8.GetString(input.ToArray()).Trim())));
    }

    [Fact]
    public async Task ModifiedPreviewIsRejectedBeforeDestructiveExecution()
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);
        ConfigureSuccessfulPostgreSql(fixture);
        var preview = (await fixture.Service.PreviewAsync(
            fixture.Server,
            Request(fixture),
            TestContext.Current.CancellationToken)).Preview!;
        var commandCount = fixture.Commands.Commands.Count;
        var tampered = preview with { BackupPath = "/tmp/other.dump" };

        var result = await fixture.Service.ExecuteAsync(
            fixture.Server,
            tampered,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error!.Code);
        Assert.Equal(commandCount, fixture.Commands.Commands.Count);
        Assert.DoesNotContain(fixture.Commands.Commands, command => command.Risk == OperationRisk.Destructive);
    }

    [Fact]
    public async Task PreviewCapabilityIsSingleUse()
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);
        ConfigureSuccessfulPostgreSql(fixture);
        var preview = (await fixture.Service.PreviewAsync(
            fixture.Server,
            Request(fixture),
            TestContext.Current.CancellationToken)).Preview!;

        var first = await fixture.Service.ExecuteAsync(
            fixture.Server,
            preview,
            TestContext.Current.CancellationToken);
        var commandCount = fixture.Commands.Commands.Count;
        var second = await fixture.Service.ExecuteAsync(
            fixture.Server,
            preview,
            TestContext.Current.CancellationToken);

        Assert.True(first.IsSuccess);
        Assert.False(second.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, second.Error!.Code);
        Assert.Equal(commandCount, fixture.Commands.Commands.Count);
        Assert.Single(fixture.Commands.Commands, command => command.Risk == OperationRisk.Destructive);
    }

    [Fact]
    public async Task TargetStateChangeAfterPreviewBlocksRestore()
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);
        var targetReads = 0;
        ConfigureSuccessfulPostgreSql(fixture, () => TargetJson(objects: ++targetReads == 1 ? 4 : 5));
        var preview = (await fixture.Service.PreviewAsync(
            fixture.Server,
            Request(fixture),
            TestContext.Current.CancellationToken)).Preview!;

        var result = await fixture.Service.ExecuteAsync(
            fixture.Server,
            preview,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error!.Code);
        Assert.DoesNotContain(fixture.Commands.Commands, command => command.Risk == OperationRisk.Destructive);
    }

    [Fact]
    public async Task BackupChecksumChangeAfterPreviewBlocksRestore()
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);
        var checksumReads = 0;
        ConfigureSuccessfulPostgreSql(
            fixture,
            checksumFactory: () => ++checksumReads == 1 ? Sha : new string('C', 64));
        var preview = (await fixture.Service.PreviewAsync(
            fixture.Server,
            Request(fixture),
            TestContext.Current.CancellationToken)).Preview!;

        var result = await fixture.Service.ExecuteAsync(
            fixture.Server,
            preview,
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.Equal(RemoteErrorCode.PathConflict, result.Error!.Code);
        Assert.DoesNotContain(fixture.Commands.Commands, command => command.Risk == OperationRisk.Destructive);
    }

    [Fact]
    public async Task NetworkInterruptionAfterRestoreDispatchIsAmbiguousAndNeverRetried()
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);
        ConfigureSuccessfulPostgreSql(
            fixture,
            destructiveResult: () => RemoteExecutionResult.Failure(
                new RemoteError(RemoteErrorCode.NetworkInterrupted, "fixture disconnect")));
        var preview = (await fixture.Service.PreviewAsync(
            fixture.Server,
            Request(fixture),
            TestContext.Current.CancellationToken)).Preview!;

        var result = await fixture.Service.ExecuteAsync(
            fixture.Server,
            preview,
            TestContext.Current.CancellationToken);

        Assert.True(result.AmbiguousState);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error!.Code);
        Assert.Single(fixture.Commands.Commands, command => command.Risk == OperationRisk.Destructive);
    }

    [Fact]
    public async Task DeterministicRestoreFailureIsStillAmbiguousBecausePartialChangesCannotBeRuledOut()
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);
        ConfigureSuccessfulPostgreSql(
            fixture,
            destructiveResult: () => RemoteExecutionResult.Success(
                new RemoteCommandResult(1, string.Empty, "fixture SQL failure", TimeSpan.Zero)));
        var preview = (await fixture.Service.PreviewAsync(
            fixture.Server,
            Request(fixture),
            TestContext.Current.CancellationToken)).Preview!;

        var result = await fixture.Service.ExecuteAsync(
            fixture.Server,
            preview,
            TestContext.Current.CancellationToken);

        Assert.True(result.AmbiguousState);
        Assert.False(result.RollbackAvailable);
        Assert.Single(fixture.Commands.Commands, command => command.Risk == OperationRisk.Destructive);
        Assert.True(fixture.Commands.Commands.Count(command => IsTargetInspection(command)) >= 3);
    }

    [Theory]
    [InlineData(DatabaseEngineKind.MySql, "mysql")]
    [InlineData(DatabaseEngineKind.MariaDb, "mariadb")]
    public async Task MySqlFamilyRestoreUsesVerifiedManifestAndRemoteFileRedirectionWithoutSecretArguments(
        DatabaseEngineKind engine,
        string tool)
    {
        var fixture = CreateFixture(engine);
        ConfigureSuccessfulMySqlFamily(fixture, tool);
        var preview = (await fixture.Service.PreviewAsync(
            fixture.Server,
            Request(fixture),
            TestContext.Current.CancellationToken)).Preview!;

        Assert.True(preview.UsesSensitiveInput);
        Assert.Equal("sh", preview.Executable);
        Assert.Contains(tool, preview.Arguments);
        Assert.Contains(fixture.Manifest.BackupPath.Value, preview.Arguments);
        Assert.DoesNotContain("--one-database", preview.Arguments);
        Assert.Contains("--database=appdb", preview.Arguments);
        Assert.DoesNotContain(Secret, string.Join('\n', preview.Arguments), StringComparison.Ordinal);

        var result = await fixture.Service.ExecuteAsync(
            fixture.Server,
            preview,
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        var restore = Assert.Single(fixture.Commands.Commands, command => command.Risk == OperationRisk.Destructive);
        Assert.NotNull(restore.StandardInput);
        Assert.DoesNotContain(Secret, restore.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditRecordsExactIdsWithoutCredentialValue()
    {
        var fixture = CreateFixture(DatabaseEngineKind.PostgreSql);
        var audit = new AuditSink();
        var preview = new DatabaseRestorePreview(
            Guid.NewGuid(),
            "fingerprint",
            fixture.Server.Id,
            $"{fixture.Server.Username}@{fixture.Server.Host}:{fixture.Server.Port}",
            Request(fixture),
            fixture.Profile.Engine,
            fixture.Manifest.Format,
            fixture.Manifest.BackupPath.Value,
            fixture.Manifest.Verification.Sha256,
            fixture.Manifest.Verification.SizeBytes,
            fixture.Manifest.ToolName,
            fixture.Manifest.ToolVersion,
            "pg_restore",
            "pg_restore fixture",
            "manifest",
            new DatabaseRestoreTargetSnapshot("appdb", "dbuser", "18.6", 4),
            "target",
            "pg_restore",
            [],
            true,
            OperationRisk.Destructive,
            "pg_restore ...",
            "destructive",
            false);
        var inner = new StubRestoreService(new DatabaseRestoreResult(
            false,
            true,
            false,
            "ambiguous",
            new RemoteError(RemoteErrorCode.AmbiguousState, "ambiguous")));
        var service = new AuditedDatabaseRestoreService(inner, audit);

        await service.ExecuteAsync(fixture.Server, preview, TestContext.Current.CancellationToken);

        var entry = Assert.Single(audit.Items);
        Assert.Equal("database-restore", entry.Category);
        Assert.Equal(OperationOutcome.Unknown, entry.Outcome);
        Assert.Equal(OperationRisk.Destructive, entry.Risk);
        Assert.Contains(fixture.Profile.Id.ToString(), entry.Target!, StringComparison.Ordinal);
        Assert.Contains(fixture.Manifest.BackupId.ToString(), entry.Target!, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, entry.Target!, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, entry.Summary, StringComparison.Ordinal);
    }

    private static Fixture CreateFixture(DatabaseEngineKind engine, bool verified = true)
    {
        var server = ServerProfile.Create("Restore fixture", "server.example", 22, "operator");
        var profileId = Guid.NewGuid();
        var reference = SecretReference.ForDatabaseProfile(profileId);
        var profile = DatabaseConnectionProfile.Create(
            profileId,
            server.Id,
            $"{engine} fixture",
            engine,
            "127.0.0.1",
            DatabaseConnectionProfile.DefaultPortFor(engine),
            "appdb",
            "dbuser",
            DatabaseAuthenticationKind.Password,
            reference);
        var format = engine switch
        {
            DatabaseEngineKind.PostgreSql or DatabaseEngineKind.Redis => DatabaseBackupFormat.PostgreSqlCustom,
            DatabaseEngineKind.MySql => DatabaseBackupFormat.MySqlSql,
            DatabaseEngineKind.MariaDb => DatabaseBackupFormat.MariaDbSql,
            _ => throw new ArgumentOutOfRangeException(nameof(engine)),
        };
        var manifest = new DatabaseBackupManifest(
            Guid.NewGuid(),
            server.Id,
            profile.Id,
            engine,
            "appdb",
            "dbuser",
            RemotePath.Parse(engine == DatabaseEngineKind.PostgreSql ? "/var/backups/app.dump" : "/var/backups/app.sql"),
            format,
            engine == DatabaseEngineKind.PostgreSql ? "pg_dump" : "mysqldump",
            "fixture dump version",
            DateTimeOffset.UtcNow.AddMinutes(-1),
            new DatabaseBackupVerificationEvidence(4096, Sha, "fixture structural verification", DateTimeOffset.UtcNow),
            verified);
        var profiles = new ProfileRepository(profile);
        var manifests = new ManifestRepository(manifest);
        var secrets = new SecretStore(reference, Secret);
        var commands = new CommandFactory();
        var service = new DatabaseRestoreService(
            profiles,
            manifests,
            secrets,
            commands,
            DatabaseRestoreOptions.Default);
        return new Fixture(server, profile, manifest, secrets, commands, service);
    }

    private static DatabaseRestoreRequest Request(Fixture fixture) =>
        new(fixture.Profile.Id, fixture.Manifest.BackupId, "appdb");

    private static void ConfigureSuccessfulPostgreSql(
        Fixture fixture,
        Func<string>? targetFactory = null,
        Func<string>? checksumFactory = null,
        Func<RemoteExecutionResult>? destructiveResult = null)
    {
        targetFactory ??= () => TargetJson();
        checksumFactory ??= () => Sha;
        destructiveResult ??= () => Success();
        fixture.Commands.Handler = spec =>
        {
            if (IsStat(spec, fixture.Manifest.BackupPath.Value))
            {
                return Success($"regular file\t{fixture.Manifest.Verification.SizeBytes}");
            }

            if (spec.Executable == "sha256sum")
            {
                return Success($"{checksumFactory()}  {fixture.Manifest.BackupPath.Value}\n");
            }

            if (spec.Executable == "pg_restore" && spec.Arguments.Contains("--list"))
            {
                return Success("; Archive created by pg_dump fixture\n");
            }

            if (spec.Executable == "pg_restore" && spec.Arguments.SequenceEqual(["--version"]))
            {
                return Success("pg_restore (PostgreSQL) 18.6\n");
            }

            if (IsTargetInspection(spec))
            {
                Assert.NotNull(spec.StandardInput);
                return Success(targetFactory() + "\n");
            }

            if (spec.Risk == OperationRisk.Destructive)
            {
                Assert.Contains("pg_restore", spec.Arguments);
                Assert.Contains("--single-transaction", spec.Arguments);
                return destructiveResult();
            }

            throw new InvalidOperationException($"Unexpected command: {spec}");
        };
    }

    private static void ConfigureSuccessfulMySqlFamily(Fixture fixture, string tool)
    {
        fixture.Commands.Handler = spec =>
        {
            if (IsStat(spec, fixture.Manifest.BackupPath.Value))
            {
                return Success($"regular file\t{fixture.Manifest.Verification.SizeBytes}");
            }

            if (spec.Executable == "sha256sum")
            {
                return Success($"{Sha}  {fixture.Manifest.BackupPath.Value}\n");
            }

            if (spec.Executable == "head")
            {
                var header = fixture.Profile.Engine == DatabaseEngineKind.MariaDb ? "MariaDB dump" : "MySQL dump";
                return Success($"-- {header} fixture\n");
            }

            if (spec.Executable == tool && spec.Arguments.SequenceEqual(["--version"]))
            {
                return Success($"{tool} Ver fixture\n");
            }

            if (IsTargetInspection(spec))
            {
                Assert.NotNull(spec.StandardInput);
                return Success(TargetJson() + "\n");
            }

            if (spec.Risk == OperationRisk.Destructive)
            {
                Assert.Equal("sh", spec.Executable);
                Assert.Contains(tool, spec.Arguments);
                return Success();
            }

            throw new InvalidOperationException($"Unexpected command: {spec}");
        };
    }

    private static bool IsTargetInspection(RemoteCommandSpec spec) =>
        spec.Risk == OperationRisk.ReadOnly && spec.Executable == "sh" &&
        (spec.Arguments.Contains("psql") || spec.Arguments.Contains("mysql") || spec.Arguments.Contains("mariadb"));

    private static bool IsStat(RemoteCommandSpec spec, string path) =>
        spec.Executable == "stat" && spec.Arguments.LastOrDefault() == path;

    private static string TargetJson(long objects = 4) =>
        $"{{\"database\":\"appdb\",\"identity\":\"dbuser\",\"version\":\"18.6\",\"objects\":{objects}}}";

    private static RemoteExecutionResult Success(string output = "") =>
        RemoteExecutionResult.Success(new RemoteCommandResult(0, output, string.Empty, TimeSpan.Zero));

    private sealed record Fixture(
        ServerProfile Server,
        DatabaseConnectionProfile Profile,
        DatabaseBackupManifest Manifest,
        SecretStore Secrets,
        CommandFactory Commands,
        DatabaseRestoreService Service);

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

    private sealed class ManifestRepository : IDatabaseBackupManifestRepository
    {
        private readonly DatabaseBackupManifest _manifest;

        public ManifestRepository(DatabaseBackupManifest manifest) => _manifest = manifest;

        public ValueTask<IReadOnlyList<DatabaseBackupManifest>> ListForServerAsync(Guid serverProfileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DatabaseBackupManifest>>(serverProfileId == _manifest.ServerProfileId ? [_manifest] : []);

        public ValueTask<DatabaseBackupManifest?> GetAsync(Guid backupId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DatabaseBackupManifest?>(backupId == _manifest.BackupId ? _manifest : null);

        public ValueTask AddAsync(DatabaseBackupManifest manifest, CancellationToken cancellationToken = default) =>
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

    private sealed class AuditSink : IOperationAudit
    {
        public List<OperationAuditEntry> Items { get; } = [];

        public ValueTask AppendAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Items.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OperationAuditEntry>>(Items.Take(limit).ToArray());
    }

    private sealed class StubRestoreService : IDatabaseRestoreService
    {
        private readonly DatabaseRestoreResult _result;

        public StubRestoreService(DatabaseRestoreResult result) => _result = result;

        public Task<DatabaseRestorePreviewResult> PreviewAsync(ServerProfile serverProfile, DatabaseRestoreRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DatabaseRestoreResult> ExecuteAsync(ServerProfile serverProfile, DatabaseRestorePreview preview, CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }
}
