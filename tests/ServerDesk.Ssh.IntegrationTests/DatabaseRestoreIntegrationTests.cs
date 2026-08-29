using System.Globalization;
using MySqlConnector;
using Npgsql;
using ServerDesk.Application.Databases;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class DatabaseRestoreIntegrationTests
{
    private const string RestoreDatabase = "serverdesk_restore";
    private const string ProbeTable = "serverdesk_restore_probe";
    private const string BackupMarker = "backup-state";
    private const string MutatedMarker = "mutated-state";

    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = ReadPort("SERVERDESK_SSH_PORT", 2222);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string SshPassword = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string DatabasePassword = Environment.GetEnvironmentVariable("SERVERDESK_DB_PASSWORD") ?? "serverdesk-db-password";
    private static readonly string BackupDirectory = Environment.GetEnvironmentVariable("SERVERDESK_DB_BACKUP_DIR") ?? "/tmp/serverdesk-db-backups";

    public static TheoryData<DatabaseEngineKind, string, int> CertifiedRestoreEngines => new()
    {
        { DatabaseEngineKind.PostgreSql, "SERVERDESK_POSTGRES_PORT", 15432 },
        { DatabaseEngineKind.MySql, "SERVERDESK_MYSQL_PORT", 13306 },
        { DatabaseEngineKind.MariaDb, "SERVERDESK_MARIADB_PORT", 13307 },
    };

    [Theory]
    [MemberData(nameof(CertifiedRestoreEngines))]
    public async Task VerifiedBackupRestoresExactRealDatabaseOverOpenSsh(
        DatabaseEngineKind engine,
        string portVariable,
        int fallbackPort)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var databasePort = ReadPort(portVariable, fallbackPort);
        await ResetProbeAsync(engine, databasePort, cancellationToken);
        var fixture = CreateFixture(engine, databasePort);

        var backup = await fixture.BackupService.CreateAsync(
            fixture.Server,
            new DatabaseBackupRequest(fixture.DatabaseProfile.Id, RestoreDatabase, BackupDirectory),
            cancellationToken);

        Assert.True(backup.IsSuccess, backup.Message);
        var manifest = Assert.IsType<DatabaseBackupManifest>(backup.Manifest);
        Assert.True(manifest.IsVerified);
        Assert.Equal(RestoreDatabase, manifest.DatabaseName);
        Assert.Equal(64, manifest.Verification.Sha256.Length);

        await MutateProbeAsync(engine, databasePort, cancellationToken);
        var changed = await ReadProbeAsync(engine, databasePort, cancellationToken);
        Assert.Equal(MutatedMarker, changed.Marker);
        Assert.Equal(2, changed.RowCount);

        var previewResult = await fixture.RestoreService.PreviewAsync(
            fixture.Server,
            new DatabaseRestoreRequest(fixture.DatabaseProfile.Id, manifest.BackupId, RestoreDatabase),
            cancellationToken);

        Assert.True(previewResult.IsSuccess, previewResult.Error?.Message);
        var preview = Assert.IsType<DatabaseRestorePreview>(previewResult.Preview);
        Assert.Equal(OperationRisk.Destructive, preview.Risk);
        Assert.Equal(fixture.Server.Id, preview.ServerProfileId);
        Assert.Equal(fixture.DatabaseProfile.Id, preview.Request.DatabaseProfileId);
        Assert.Equal(manifest.BackupId, preview.Request.BackupId);
        Assert.Equal(RestoreDatabase, preview.Request.TargetDatabase);
        Assert.Equal(manifest.BackupPath.Value, preview.BackupPath);
        Assert.Equal(manifest.Verification.Sha256, preview.BackupSha256, ignoreCase: true);
        Assert.False(preview.RollbackAvailable);
        Assert.Contains("data", preview.DataLossWarning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DatabasePassword, preview.DisplayCommand, StringComparison.Ordinal);

        var restore = await fixture.RestoreService.ExecuteAsync(
            fixture.Server,
            preview,
            cancellationToken);

        Assert.True(restore.IsSuccess, restore.Message);
        Assert.False(restore.AmbiguousState);
        Assert.False(restore.RollbackAvailable);
        Assert.NotNull(restore.VerifiedTarget);
        Assert.Equal(RestoreDatabase, restore.VerifiedTarget.DatabaseName);

        var restored = await ReadProbeAsync(engine, databasePort, cancellationToken);
        Assert.Equal(BackupMarker, restored.Marker);
        Assert.Equal(1, restored.RowCount);
    }

    private static RestoreFixture CreateFixture(DatabaseEngineKind engine, int databasePort)
    {
        var serverId = Guid.NewGuid();
        var sshReference = SecretReference.ForServerProfile(serverId);
        var server = ServerProfile.Create(
            serverId,
            $"{engine} restore fixture",
            Host,
            Port,
            Username,
            credentialReference: sshReference,
            authenticationKind: ServerAuthenticationKind.Password);
        var databaseId = Guid.NewGuid();
        var databaseReference = SecretReference.ForDatabaseProfile(databaseId);
        var databaseProfile = DatabaseConnectionProfile.Create(
            databaseId,
            server.Id,
            $"{engine} restore fixture",
            engine,
            "127.0.0.1",
            databasePort,
            RestoreDatabase,
            "serverdesk",
            DatabaseAuthenticationKind.Password,
            databaseReference);
        var secretStore = new MemorySecretStore(
            new Dictionary<SecretReference, string>
            {
                [sshReference] = SshPassword,
                [databaseReference] = DatabasePassword,
            });
        var commands = new SshRemoteCommandExecutorFactory(
            secretStore,
            new TrustOnceHostTrustService(),
            new RejectInteractivePrompt(),
            new SshSessionOptions(
                TimeSpan.FromSeconds(12),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(250)));
        var profiles = new MemoryDatabaseProfileRepository(databaseProfile);
        var manifests = new MemoryManifestRepository();
        var backupService = new DatabaseBackupService(
            profiles,
            secretStore,
            commands,
            manifests,
            DatabaseBackupOptions.Default);
        var restoreService = new DatabaseRestoreService(
            profiles,
            manifests,
            secretStore,
            commands,
            DatabaseRestoreOptions.Default);
        return new RestoreFixture(server, databaseProfile, backupService, restoreService);
    }

    private static async Task ResetProbeAsync(
        DatabaseEngineKind engine,
        int databasePort,
        CancellationToken cancellationToken)
    {
        await ExecuteDatabaseCommandAsync(
            engine,
            databasePort,
            $"DROP TABLE IF EXISTS {ProbeTable}",
            cancellationToken);
        await ExecuteDatabaseCommandAsync(
            engine,
            databasePort,
            $"CREATE TABLE {ProbeTable} (id INT PRIMARY KEY, marker VARCHAR(64) NOT NULL)",
            cancellationToken);
        await ExecuteDatabaseCommandAsync(
            engine,
            databasePort,
            $"INSERT INTO {ProbeTable} (id, marker) VALUES (1, '{BackupMarker}')",
            cancellationToken);
    }

    private static async Task MutateProbeAsync(
        DatabaseEngineKind engine,
        int databasePort,
        CancellationToken cancellationToken)
    {
        await ExecuteDatabaseCommandAsync(
            engine,
            databasePort,
            $"UPDATE {ProbeTable} SET marker = '{MutatedMarker}' WHERE id = 1",
            cancellationToken);
        await ExecuteDatabaseCommandAsync(
            engine,
            databasePort,
            $"INSERT INTO {ProbeTable} (id, marker) VALUES (2, '{MutatedMarker}')",
            cancellationToken);
    }

    private static async Task<ProbeState> ReadProbeAsync(
        DatabaseEngineKind engine,
        int databasePort,
        CancellationToken cancellationToken)
    {
        if (engine == DatabaseEngineKind.PostgreSql)
        {
            await using var connection = new NpgsqlConnection(PostgreSqlConnectionString(databasePort));
            await connection.OpenAsync(cancellationToken);
            await using var marker = new NpgsqlCommand($"SELECT marker FROM {ProbeTable} WHERE id = 1", connection);
            var value = (string?)await marker.ExecuteScalarAsync(cancellationToken);
            await using var count = new NpgsqlCommand($"SELECT COUNT(*) FROM {ProbeTable}", connection);
            var rows = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            return new ProbeState(value ?? string.Empty, rows);
        }

        await using var mysql = new MySqlConnection(MySqlConnectionString(databasePort));
        await mysql.OpenAsync(cancellationToken);
        await using var mysqlMarker = new MySqlCommand($"SELECT marker FROM {ProbeTable} WHERE id = 1", mysql);
        var mysqlValue = (string?)await mysqlMarker.ExecuteScalarAsync(cancellationToken);
        await using var mysqlCount = new MySqlCommand($"SELECT COUNT(*) FROM {ProbeTable}", mysql);
        var mysqlRows = Convert.ToInt32(await mysqlCount.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return new ProbeState(mysqlValue ?? string.Empty, mysqlRows);
    }

    private static async Task ExecuteDatabaseCommandAsync(
        DatabaseEngineKind engine,
        int databasePort,
        string sql,
        CancellationToken cancellationToken)
    {
        if (engine == DatabaseEngineKind.PostgreSql)
        {
            await using var connection = new NpgsqlConnection(PostgreSqlConnectionString(databasePort));
            await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var mysql = new MySqlConnection(MySqlConnectionString(databasePort));
        await mysql.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(sql, mysql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string PostgreSqlConnectionString(int databasePort) =>
        $"Host=127.0.0.1;Port={databasePort.ToString(CultureInfo.InvariantCulture)};Username=serverdesk;Password={DatabasePassword};Database={RestoreDatabase};Timeout=10;Command Timeout=15;Pooling=false";

    private static string MySqlConnectionString(int databasePort) =>
        $"Server=127.0.0.1;Port={databasePort.ToString(CultureInfo.InvariantCulture)};User ID=serverdesk;Password={DatabasePassword};Database={RestoreDatabase};Connection Timeout=10;Default Command Timeout=15;Pooling=false";

    private static int ReadPort(string name, int fallback) => int.Parse(
        Environment.GetEnvironmentVariable(name) ?? fallback.ToString(CultureInfo.InvariantCulture),
        CultureInfo.InvariantCulture);

    private sealed record RestoreFixture(
        ServerProfile Server,
        DatabaseConnectionProfile DatabaseProfile,
        DatabaseBackupService BackupService,
        DatabaseRestoreService RestoreService);

    private sealed record ProbeState(string Marker, int RowCount);

    private sealed class MemoryDatabaseProfileRepository : IDatabaseProfileRepository
    {
        private readonly DatabaseConnectionProfile _profile;

        public MemoryDatabaseProfileRepository(DatabaseConnectionProfile profile) => _profile = profile;

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

    private sealed class MemoryManifestRepository : IDatabaseBackupManifestRepository
    {
        public List<DatabaseBackupManifest> Items { get; } = [];

        public ValueTask<IReadOnlyList<DatabaseBackupManifest>> ListForServerAsync(Guid serverProfileId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<DatabaseBackupManifest>>(
                Items.Where(item => item.ServerProfileId == serverProfileId).ToArray());

        public ValueTask<DatabaseBackupManifest?> GetAsync(Guid backupId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<DatabaseBackupManifest?>(Items.FirstOrDefault(item => item.BackupId == backupId));

        public ValueTask AddAsync(DatabaseBackupManifest manifest, CancellationToken cancellationToken = default)
        {
            Items.Add(manifest);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MemorySecretStore : ISecretStore
    {
        private readonly IReadOnlyDictionary<SecretReference, string> _secrets;

        public MemorySecretStore(IReadOnlyDictionary<SecretReference, string> secrets) => _secrets = secrets;

        public ValueTask SetAsync(SecretReference reference, string secret, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<string?> GetAsync(SecretReference reference, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<string?>(_secrets.TryGetValue(reference, out var secret) ? secret : null);
        }

        public ValueTask DeleteAsync(SecretReference reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TrustOnceHostTrustService : IHostTrustService
    {
        public ValueTask<HostTrustVerification> VerifyAsync(
            HostKeyObservation observation,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new HostTrustVerification(HostTrustOutcome.TrustedOnce, observation, []));
        }
    }

    private sealed class RejectInteractivePrompt : IInteractiveAuthenticationPrompt
    {
        public ValueTask<IReadOnlyList<string>?> PromptAsync(
            InteractiveAuthenticationChallenge challenge,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Password fixture must not request keyboard-interactive authentication.");
    }
}
