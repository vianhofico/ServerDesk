using System.Globalization;
using Microsoft.Data.SqlClient;
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

public sealed class SqlServerBackupRestoreIntegrationTests
{
    private const string DatabaseName = "serverdesk_restore";
    private const string ProbeTable = "serverdesk_restore_probe";
    private const string BackupMarker = "backup-state";
    private const string MutatedMarker = "mutated-state";

    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int SshPort = ReadPort("SERVERDESK_SSH_PORT", 2222);
    private static readonly int SqlServerPort = ReadPort("SERVERDESK_SQLSERVER_PORT", 11433);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string SshPassword = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string SqlServerPassword = Environment.GetEnvironmentVariable("SERVERDESK_SQLSERVER_PASSWORD")
        ?? throw new InvalidOperationException("SERVERDESK_SQLSERVER_PASSWORD is required for the SQL Server backup/restore fixture.");
    private static readonly string BackupDirectory = Environment.GetEnvironmentVariable("SERVERDESK_DB_BACKUP_DIR") ?? "/tmp/serverdesk-db-backups";

    [Fact]
    public async Task VerifiedNativeBackupRestoresExactSqlServerDatabaseAndKeepsSecretOutOfPreview()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await ResetProbeAsync(cancellationToken);
        var fixture = CreateFixture();

        var backup = await fixture.Backup.CreateAsync(
            fixture.Server,
            new DatabaseBackupRequest(fixture.Profile.Id, DatabaseName, BackupDirectory),
            cancellationToken);

        Assert.True(backup.IsSuccess, backup.Message);
        Assert.True(backup.HistoryPersisted);
        var manifest = Assert.IsType<DatabaseBackupManifest>(backup.Manifest);
        Assert.Equal(DatabaseEngineKind.SqlServer, manifest.Engine);
        Assert.Equal(DatabaseBackupFormat.SqlServerNative, manifest.Format);
        Assert.Equal(DatabaseName, manifest.DatabaseName);
        Assert.True(manifest.Verification.SizeBytes > 0);
        Assert.Equal(64, manifest.Verification.Sha256.Length);
        Assert.Contains("RESTORE VERIFYONLY", manifest.Verification.StructuralCheck, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SqlServerPassword, manifest.ToString(), StringComparison.Ordinal);

        await MutateProbeAsync(cancellationToken);
        var mutated = await ReadProbeAsync(cancellationToken);
        Assert.Equal(MutatedMarker, mutated.Marker);
        Assert.Equal(2, mutated.RowCount);

        var previewResult = await fixture.Restore.PreviewAsync(
            fixture.Server,
            new DatabaseRestoreRequest(fixture.Profile.Id, manifest.BackupId, DatabaseName),
            cancellationToken);

        Assert.True(previewResult.IsSuccess, previewResult.Error?.Message);
        var preview = Assert.IsType<DatabaseRestorePreview>(previewResult.Preview);
        Assert.Equal(DatabaseEngineKind.SqlServer, preview.Engine);
        Assert.Equal(DatabaseBackupFormat.SqlServerNative, preview.BackupFormat);
        Assert.Equal(OperationRisk.Destructive, preview.Risk);
        Assert.Equal(DatabaseName, preview.Request.TargetDatabase);
        Assert.False(preview.RollbackAvailable);
        Assert.Contains("replace", preview.DataLossWarning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(SqlServerPassword, preview.DisplayCommand, StringComparison.Ordinal);
        Assert.DoesNotContain(SqlServerPassword, string.Join(" ", preview.Arguments), StringComparison.Ordinal);

        var restore = await fixture.Restore.ExecuteAsync(fixture.Server, preview, cancellationToken);

        Assert.True(restore.IsSuccess, restore.Message);
        Assert.False(restore.AmbiguousState);
        Assert.False(restore.RollbackAvailable);
        Assert.NotNull(restore.VerifiedTarget);
        Assert.Equal(DatabaseName, restore.VerifiedTarget.DatabaseName);
        Assert.Equal("17.0.4075.5", restore.VerifiedTarget.ServerVersion);

        var restored = await ReadProbeAsync(cancellationToken);
        Assert.Equal(BackupMarker, restored.Marker);
        Assert.Equal(1, restored.RowCount);
    }

    private static Fixture CreateFixture()
    {
        var serverId = Guid.NewGuid();
        var sshReference = SecretReference.ForServerProfile(serverId);
        var server = ServerProfile.Create(
            serverId,
            "SQL Server backup/restore fixture",
            Host,
            SshPort,
            Username,
            credentialReference: sshReference,
            authenticationKind: ServerAuthenticationKind.Password);
        var profileId = Guid.NewGuid();
        var databaseReference = SecretReference.ForDatabaseProfile(profileId);
        var profile = DatabaseConnectionProfile.Create(
            profileId,
            server.Id,
            "SQL Server 2025 CU8 fixture",
            DatabaseEngineKind.SqlServer,
            "127.0.0.1",
            SqlServerPort,
            DatabaseName,
            "sa",
            DatabaseAuthenticationKind.Password,
            databaseReference);
        var secrets = new MemorySecretStore(
            new Dictionary<SecretReference, string>
            {
                [sshReference] = SshPassword,
                [databaseReference] = SqlServerPassword,
            });
        var commands = new SshRemoteCommandExecutorFactory(
            secrets,
            new TrustOnceHostTrustService(),
            new RejectInteractivePrompt(),
            new SshSessionOptions(
                TimeSpan.FromSeconds(15),
                TimeSpan.FromSeconds(3),
                TimeSpan.FromSeconds(2),
                TimeSpan.FromMilliseconds(250)));
        var profiles = new MemoryDatabaseProfileRepository(profile);
        var manifests = new MemoryManifestRepository();
        var backup = new SqlServerDatabaseBackupService(
            profiles,
            secrets,
            commands,
            manifests,
            DatabaseBackupOptions.Default);
        var restore = new SqlServerDatabaseRestoreService(
            profiles,
            manifests,
            secrets,
            commands,
            DatabaseRestoreOptions.Default);
        return new Fixture(server, profile, backup, restore);
    }

    private static async Task ResetProbeAsync(CancellationToken cancellationToken)
    {
        await ExecuteSqlAsync($"IF OBJECT_ID(N'dbo.{ProbeTable}', N'U') IS NOT NULL DROP TABLE dbo.{ProbeTable};", cancellationToken);
        await ExecuteSqlAsync($"CREATE TABLE dbo.{ProbeTable} (id int NOT NULL PRIMARY KEY, marker varchar(64) NOT NULL);", cancellationToken);
        await ExecuteSqlAsync($"INSERT INTO dbo.{ProbeTable} (id, marker) VALUES (1, '{BackupMarker}');", cancellationToken);
    }

    private static async Task MutateProbeAsync(CancellationToken cancellationToken)
    {
        await ExecuteSqlAsync($"UPDATE dbo.{ProbeTable} SET marker = '{MutatedMarker}' WHERE id = 1;", cancellationToken);
        await ExecuteSqlAsync($"INSERT INTO dbo.{ProbeTable} (id, marker) VALUES (2, '{MutatedMarker}');", cancellationToken);
    }

    private static async Task<ProbeState> ReadProbeAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var marker = new SqlCommand($"SELECT marker FROM dbo.{ProbeTable} WHERE id = 1;", connection);
        var value = Convert.ToString(await marker.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) ?? string.Empty;
        await using var count = new SqlCommand($"SELECT COUNT_BIG(*) FROM dbo.{ProbeTable};", connection);
        var rows = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        return new ProbeState(value, rows);
    }

    private static async Task ExecuteSqlAsync(string sql, CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(ConnectionString());
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string ConnectionString()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"tcp:127.0.0.1,{SqlServerPort.ToString(CultureInfo.InvariantCulture)}",
            InitialCatalog = DatabaseName,
            UserID = "sa",
            Password = SqlServerPassword,
            Encrypt = SqlConnectionEncryptOption.Mandatory,
            TrustServerCertificate = true,
            Pooling = false,
            ConnectTimeout = 10,
            CommandTimeout = 20,
        };
        return builder.ConnectionString;
    }

    private static int ReadPort(string name, int fallback) => int.Parse(
        Environment.GetEnvironmentVariable(name) ?? fallback.ToString(CultureInfo.InvariantCulture),
        CultureInfo.InvariantCulture);

    private sealed record Fixture(
        ServerProfile Server,
        DatabaseConnectionProfile Profile,
        SqlServerDatabaseBackupService Backup,
        SqlServerDatabaseRestoreService Restore);

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
            ValueTask.FromResult<IReadOnlyList<DatabaseBackupManifest>>(Items.Where(item => item.ServerProfileId == serverProfileId).ToArray());

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
            return ValueTask.FromResult<string?>(_secrets.TryGetValue(reference, out var value) ? value : null);
        }

        public ValueTask DeleteAsync(SecretReference reference, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TrustOnceHostTrustService : IHostTrustService
    {
        public ValueTask<HostTrustVerification> VerifyAsync(HostKeyObservation observation, CancellationToken cancellationToken = default)
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
