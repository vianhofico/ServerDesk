using System.Globalization;
using MongoDB.Bson;
using MongoDB.Driver;
using ServerDesk.Application.Databases;
using ServerDesk.Application.HostTrust;
using ServerDesk.Application.Profiles;
using ServerDesk.Application.Remote;
using ServerDesk.Application.Secrets;
using ServerDesk.Application.Sessions;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Security;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Databases;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Ssh.IntegrationTests;

public sealed class MongoDbBackupRestoreIntegrationTests
{
    private const string DatabaseName = "serverdesk_restore";
    private const string CollectionName = "serverdesk_restore_probe";
    private const string BackupMarker = "backup-state";
    private const string MutatedMarker = "mutated-state";

    private static readonly string Host = Environment.GetEnvironmentVariable("SERVERDESK_SSH_HOST") ?? "127.0.0.1";
    private static readonly int SshPort = ReadPort("SERVERDESK_SSH_PORT", 2222);
    private static readonly int MongoDbPort = ReadPort("SERVERDESK_MONGODB_PORT", 17017);
    private static readonly string Username = Environment.GetEnvironmentVariable("SERVERDESK_SSH_USER") ?? "serverdesk_ci";
    private static readonly string SshPassword = Environment.GetEnvironmentVariable("SERVERDESK_SSH_PASSWORD") ?? "serverdesk-password";
    private static readonly string DatabasePassword = Environment.GetEnvironmentVariable("SERVERDESK_DB_PASSWORD") ?? "serverdesk-db-password";
    private static readonly string BackupDirectory = Environment.GetEnvironmentVariable("SERVERDESK_DB_BACKUP_DIR") ?? "/tmp/serverdesk-db-backups";

    [Fact]
    public async Task VerifiedArchiveRestoresExactMongoDbDatabaseAndKeepsSecretOutOfPreview()
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
        Assert.Equal(DatabaseEngineKind.MongoDb, manifest.Engine);
        Assert.Equal(DatabaseBackupFormat.MongoDbArchive, manifest.Format);
        Assert.Equal(DatabaseName, manifest.DatabaseName);
        Assert.True(manifest.Verification.SizeBytes > 0);
        Assert.Equal(64, manifest.Verification.Sha256.Length);
        Assert.Contains("--dryRun", manifest.Verification.StructuralCheck, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DatabasePassword, manifest.ToString(), StringComparison.Ordinal);

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
        Assert.Equal(DatabaseEngineKind.MongoDb, preview.Engine);
        Assert.Equal(DatabaseBackupFormat.MongoDbArchive, preview.BackupFormat);
        Assert.Equal(OperationRisk.Destructive, preview.Risk);
        Assert.Equal(DatabaseName, preview.Request.TargetDatabase);
        Assert.False(preview.RollbackAvailable);
        Assert.Contains("drop", preview.DataLossWarning, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(DatabasePassword, preview.DisplayCommand, StringComparison.Ordinal);
        Assert.DoesNotContain(DatabasePassword, string.Join(" ", preview.Arguments), StringComparison.Ordinal);

        var restore = await fixture.Restore.ExecuteAsync(fixture.Server, preview, cancellationToken);

        Assert.True(restore.IsSuccess, restore.Message);
        Assert.False(restore.AmbiguousState);
        Assert.False(restore.RollbackAvailable);
        Assert.NotNull(restore.VerifiedTarget);
        Assert.Equal(DatabaseName, restore.VerifiedTarget.DatabaseName);
        Assert.Equal("8.0.29", restore.VerifiedTarget.ServerVersion);

        var restored = await ReadProbeAsync(cancellationToken);
        Assert.Equal(BackupMarker, restored.Marker);
        Assert.Equal(1, restored.RowCount);
    }

    [Fact]
    public async Task RemoteToolBackupRefusesNonLoopbackEndpointBeforeDispatch()
    {
        var fixture = CreateFixture("203.0.113.10");

        var result = await fixture.Backup.CreateAsync(
            fixture.Server,
            new DatabaseBackupRequest(fixture.Profile.Id, DatabaseName, BackupDirectory),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.Unsupported);
        Assert.Null(result.Manifest);
        Assert.Contains("loopback", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RemoteToolRestorePreviewRefusesNonLoopbackEndpointBeforeDispatch()
    {
        var fixture = CreateFixture("203.0.113.10");
        var backupId = Guid.NewGuid();
        fixture.Manifests.Items.Add(new DatabaseBackupManifest(
            backupId,
            fixture.Server.Id,
            fixture.Profile.Id,
            DatabaseEngineKind.MongoDb,
            DatabaseName,
            "serverdesk",
            ServerDesk.Application.RemoteFiles.RemotePath.Parse($"{BackupDirectory}/serverdesk-db-backup-{backupId:N}.archive.gz"),
            DatabaseBackupFormat.MongoDbArchive,
            "mongodump",
            "100.18.0",
            DateTimeOffset.UtcNow,
            new DatabaseBackupVerificationEvidence(1, new string('A', 64), "fixture", DateTimeOffset.UtcNow),
            true));

        var result = await fixture.Restore.PreviewAsync(
            fixture.Server,
            new DatabaseRestoreRequest(fixture.Profile.Id, backupId, DatabaseName),
            TestContext.Current.CancellationToken);

        Assert.False(result.IsSuccess);
        Assert.True(result.Unsupported);
        Assert.Null(result.Preview);
        Assert.Contains("loopback", result.Error?.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static Fixture CreateFixture(string remoteHost = "127.0.0.1")
    {
        var serverId = Guid.NewGuid();
        var sshReference = SecretReference.ForServerProfile(serverId);
        var server = ServerProfile.Create(
            serverId,
            "MongoDB backup/restore fixture",
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
            "MongoDB 8.0.29 standalone fixture",
            DatabaseEngineKind.MongoDb,
            remoteHost,
            MongoDbPort,
            DatabaseName,
            "serverdesk",
            DatabaseAuthenticationKind.Password,
            databaseReference,
            "admin");
        var secrets = new MemorySecretStore(
            new Dictionary<SecretReference, string>
            {
                [sshReference] = SshPassword,
                [databaseReference] = DatabasePassword,
            });
        var sshOptions = new SshSessionOptions(
            TimeSpan.FromSeconds(15),
            TimeSpan.FromSeconds(3),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(250));
        var commands = new SshRemoteCommandExecutorFactory(
            secrets,
            new TrustOnceHostTrustService(),
            new RejectInteractivePrompt(),
            sshOptions);
        var serverProfiles = new MemoryServerProfileRepository(server);
        var databaseProfiles = new MemoryDatabaseProfileRepository(profile);
        var manifests = new MemoryManifestRepository();
        var tunnelService = new DatabaseTunnelService(
            serverProfiles,
            new SshPortForwardSessionFactory(
                secrets,
                new TrustOnceHostTrustService(),
                new RejectInteractivePrompt(),
                sshOptions));
        var diagnostics = new DatabaseDiagnosticService(
            tunnelService,
            secrets,
            [new MongoDbDiagnosticAdapter()],
            new DatabaseDiagnosticOptions(25, 20, TimeSpan.FromSeconds(10)) { MaxTextLength = 512 });
        var backup = new MongoDbDatabaseBackupService(
            databaseProfiles,
            secrets,
            commands,
            manifests,
            diagnostics,
            DatabaseBackupOptions.Default);
        var restore = new MongoDbDatabaseRestoreService(
            databaseProfiles,
            manifests,
            secrets,
            commands,
            diagnostics,
            DatabaseRestoreOptions.Default);
        return new Fixture(server, profile, backup, restore, manifests);
    }

    private static async Task ResetProbeAsync(CancellationToken cancellationToken)
    {
        var database = Client().GetDatabase(DatabaseName);
        await database.DropCollectionAsync(CollectionName, cancellationToken);
        var collection = database.GetCollection<BsonDocument>(CollectionName);
        await collection.InsertOneAsync(
            new BsonDocument { ["_id"] = 1, ["marker"] = BackupMarker },
            cancellationToken: cancellationToken);
    }

    private static async Task MutateProbeAsync(CancellationToken cancellationToken)
    {
        var collection = Client().GetDatabase(DatabaseName).GetCollection<BsonDocument>(CollectionName);
        await collection.UpdateOneAsync(
            Builders<BsonDocument>.Filter.Eq("_id", 1),
            Builders<BsonDocument>.Update.Set("marker", MutatedMarker),
            cancellationToken: cancellationToken);
        await collection.InsertOneAsync(
            new BsonDocument { ["_id"] = 2, ["marker"] = MutatedMarker },
            cancellationToken: cancellationToken);
    }

    private static async Task<ProbeState> ReadProbeAsync(CancellationToken cancellationToken)
    {
        var collection = Client().GetDatabase(DatabaseName).GetCollection<BsonDocument>(CollectionName);
        var document = await collection.Find(Builders<BsonDocument>.Filter.Eq("_id", 1))
            .FirstAsync(cancellationToken);
        var count = await collection.CountDocumentsAsync(FilterDefinition<BsonDocument>.Empty, cancellationToken: cancellationToken);
        return new ProbeState(document["marker"].AsString, count);
    }

    private static MongoClient Client()
    {
        var settings = new MongoClientSettings
        {
            Server = new MongoServerAddress("127.0.0.1", MongoDbPort),
            Credential = MongoCredential.CreateCredential("admin", "serverdesk", DatabasePassword),
            DirectConnection = true,
            UseTls = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            ServerSelectionTimeout = TimeSpan.FromSeconds(10),
        };
        return new MongoClient(settings);
    }

    private static int ReadPort(string name, int fallback) => int.Parse(
        Environment.GetEnvironmentVariable(name) ?? fallback.ToString(CultureInfo.InvariantCulture),
        CultureInfo.InvariantCulture);

    private sealed record Fixture(
        ServerProfile Server,
        DatabaseConnectionProfile Profile,
        MongoDbDatabaseBackupService Backup,
        MongoDbDatabaseRestoreService Restore,
        MemoryManifestRepository Manifests);

    private sealed record ProbeState(string Marker, long RowCount);

    private sealed class MemoryServerProfileRepository : IProfileRepository
    {
        private readonly ServerProfile _profile;

        public MemoryServerProfileRepository(ServerProfile profile) => _profile = profile;

        public ValueTask<IReadOnlyList<ServerProfile>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ServerProfile>>([_profile]);

        public ValueTask<ServerProfile?> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ServerProfile?>(id == _profile.Id ? _profile : null);

        public ValueTask UpsertAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteAsync(Guid id, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

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
