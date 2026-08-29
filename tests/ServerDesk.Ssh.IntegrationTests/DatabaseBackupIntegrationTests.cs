using System.Globalization;
using ServerDesk.Application.Databases;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class DatabaseBackupIntegrationTests
{
    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int Port = ReadPort("SERVERDESK_SSH_PORT", 2222);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string SshPassword = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string DatabasePassword = Environment.GetEnvironmentVariable("SERVERDESK_DB_PASSWORD") ?? "serverdesk-db-password";
    private static readonly string BackupDirectory = Environment.GetEnvironmentVariable("SERVERDESK_DB_BACKUP_DIR") ?? "/tmp/serverdesk-db-backups";

    public static TheoryData<DatabaseEngineKind, string, int, DatabaseBackupFormat> CertifiedBackupEngines => new()
    {
        { DatabaseEngineKind.PostgreSql, "SERVERDESK_POSTGRES_PORT", 15432, DatabaseBackupFormat.PostgreSqlCustom },
        { DatabaseEngineKind.MySql, "SERVERDESK_MYSQL_PORT", 13306, DatabaseBackupFormat.MySqlSql },
        { DatabaseEngineKind.MariaDb, "SERVERDESK_MARIADB_PORT", 13307, DatabaseBackupFormat.MariaDbSql },
    };

    [Theory]
    [MemberData(nameof(CertifiedBackupEngines))]
    public async Task CertifiedEngineBackupCreatesAndVerifiesRealArtifactOverOpenSsh(
        DatabaseEngineKind engine,
        string portVariable,
        int fallbackPort,
        DatabaseBackupFormat expectedFormat)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = CreateFixture(engine, ReadPort(portVariable, fallbackPort));

        var result = await fixture.Service.CreateAsync(
            fixture.Server,
            new DatabaseBackupRequest(fixture.DatabaseProfile.Id, "serverdesk", BackupDirectory),
            cancellationToken);

        Assert.True(result.IsSuccess, result.Message);
        Assert.True(result.HistoryPersisted);
        Assert.NotNull(result.Manifest);
        Assert.Equal(engine, result.Manifest.Engine);
        Assert.Equal(expectedFormat, result.Manifest.Format);
        Assert.Equal("serverdesk", result.Manifest.DatabaseName);
        Assert.True(result.Manifest.Verification.SizeBytes > 0);
        Assert.Equal(64, result.Manifest.Verification.Sha256.Length);
        Assert.StartsWith(BackupDirectory + "/serverdesk-db-backup-", result.Manifest.BackupPath.Value, StringComparison.Ordinal);
        Assert.DoesNotContain(DatabasePassword, result.Manifest.ToString(), StringComparison.Ordinal);
        Assert.Equal(result.Manifest, Assert.Single(fixture.Manifests.Items));
    }

    [Fact]
    public async Task RedisBackupRemainsExplicitlyUnsupportedWithoutRemoteMutation()
    {
        var fixture = CreateFixture(DatabaseEngineKind.Redis, ReadPort("SERVERDESK_REDIS_PORT", 16379));

        var result = await fixture.Service.CreateAsync(
            fixture.Server,
            new DatabaseBackupRequest(fixture.DatabaseProfile.Id, "0", BackupDirectory),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.Unsupported);
        Assert.False(result.AmbiguousState);
        Assert.Empty(fixture.Manifests.Items);
    }

    private static BackupFixture CreateFixture(DatabaseEngineKind engine, int databasePort)
    {
        var serverId = Guid.NewGuid();
        var sshReference = SecretReference.ForServerProfile(serverId);
        var server = ServerProfile.Create(
            serverId,
            "Database backup fixture",
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
            $"{engine} backup fixture",
            engine,
            "127.0.0.1",
            databasePort,
            engine == DatabaseEngineKind.Redis ? null : "serverdesk",
            engine == DatabaseEngineKind.Redis ? null : "serverdesk",
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
        var manifests = new MemoryManifestRepository();
        var service = new DatabaseBackupService(
            new MemoryDatabaseProfileRepository(databaseProfile),
            secretStore,
            commands,
            manifests,
            DatabaseBackupOptions.Default);
        return new BackupFixture(server, databaseProfile, manifests, service);
    }

    private static int ReadPort(string name, int fallback) => int.Parse(
        Environment.GetEnvironmentVariable(name) ?? fallback.ToString(CultureInfo.InvariantCulture),
        CultureInfo.InvariantCulture);

    private sealed record BackupFixture(
        ServerProfile Server,
        DatabaseConnectionProfile DatabaseProfile,
        MemoryManifestRepository Manifests,
        DatabaseBackupService Service);

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
            return ValueTask.FromResult<string?>(_secrets.TryGetValue(reference, out var secret) ? secret : null);
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
