using Microsoft.Data.Sqlite;
using ServerDesk.Application.Abstractions;
using ServerDesk.Application.Audit;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Secrets;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace ServerDesk.Tests;

public sealed class OperationHistoryTests
{
    [Fact]
    public async Task SqliteQueryFiltersM5HistoryAndEnrichesStableServerIdentityWithoutSecrets()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);

        var profileRepository = new SqliteProfileRepository(factory);
        var profileId = Guid.NewGuid();
        var secretReference = SecretReference.Create("history-secret-reference");
        var profile = ServerProfile.Create(
            profileId,
            "Production",
            "prod.example.com",
            2222,
            "deploy",
            "production",
            secretReference,
            ServerAuthenticationKind.PrivateKey,
            @"C:\secret\history-private-key-sentinel");
        await profileRepository.UpsertAsync(profile, cancellationToken);

        var rawAudit = new SqliteOperationAudit(factory);
        var audit = new M5EnrichedOperationAudit(rawAudit, profileRepository);
        var occurred = new DateTimeOffset(2026, 8, 29, 10, 0, 0, TimeSpan.Zero);
        await audit.AppendAsync(
            OperationAuditEntry.Create(
                "firewall-mutation",
                "Firewall RemoveRule requested for Ufw identity rule-1",
                OperationRisk.Destructive,
                OperationOutcome.Unknown,
                "deploy@prod.example.com:2222 firewall:Ufw:rule-1",
                occurred),
            cancellationToken);
        await audit.AppendAsync(
            OperationAuditEntry.Create(
                "package-administration",
                "Package Upgrade requested via Apt: nginx",
                OperationRisk.Mutating,
                OperationOutcome.Succeeded,
                "deploy@prod.example.com:2222 package-manager:Apt packages:nginx",
                occurred.AddMinutes(1)),
            cancellationToken);
        await rawAudit.AppendAsync(
            OperationAuditEntry.Create(
                "foundation",
                "Unrelated local history",
                OperationRisk.ReadOnly,
                OperationOutcome.Succeeded,
                "local",
                occurred.AddMinutes(2)),
            cancellationToken);
        await rawAudit.AppendAsync(
            OperationAuditEntry.Create(
                "user-administration",
                "Legacy user mutation",
                OperationRisk.Mutating,
                OperationOutcome.Succeeded,
                "deploy@prod.example.com:2222 local-user:legacy-user",
                occurred.AddMinutes(3)),
            cancellationToken);

        var service = new OperationHistoryService(rawAudit);
        var result = await service.QueryAsync(
            new OperationAuditQuery(
                FromUtc: occurred.AddMinutes(-1),
                ToUtc: occurred.AddMinutes(1),
                ServerProfileId: profileId,
                Category: "firewall-mutation",
                Risk: OperationRisk.Destructive,
                Outcome: OperationOutcome.Unknown,
                SearchText: "rule-1",
                Limit: 25),
            cancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(profileId, item.ServerProfileId);
        Assert.True(item.HasUnknownRemoteState);
        Assert.Equal("ambiguous-unknown", item.Verification);
        Assert.StartsWith($"server:{profileId:D} ", item.Entry.Target, StringComparison.Ordinal);
        Assert.DoesNotContain("prod.example.com", item.Entry.Target!, StringComparison.Ordinal);
        Assert.DoesNotContain("history-private-key-sentinel", item.Entry.Target!, StringComparison.Ordinal);
        Assert.DoesNotContain(secretReference.Value, item.Entry.Target!, StringComparison.Ordinal);

        var legacy = await service.QueryAsync(
            new OperationAuditQuery(
                ServerProfileId: profileId,
                Category: "user-administration",
                SearchText: "legacy-user",
                Limit: 25),
            cancellationToken);
        var legacyItem = Assert.Single(legacy.Items);
        Assert.Equal(profileId, legacyItem.ServerProfileId);
        Assert.Null(legacyItem.Verification);
    }

    [Fact]
    public async Task SqliteQueryAlwaysHonorsLimitAndKeepsUnknownDistinctFromFailed()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var paths = new TemporaryAppPaths();
        var factory = new SqliteConnectionFactory(paths);
        var initializer = new SqliteDatabaseInitializer(factory);
        await initializer.InitializeAsync(cancellationToken);
        var audit = new SqliteOperationAudit(factory);
        var serverId = Guid.NewGuid();
        var occurred = new DateTimeOffset(2026, 8, 29, 11, 0, 0, TimeSpan.Zero);

        for (var index = 0; index < 8; index++)
        {
            var outcome = index % 2 == 0 ? OperationOutcome.Unknown : OperationOutcome.Failed;
            await audit.AppendAsync(
                OperationAuditEntry.Create(
                    "package-administration",
                    $"operation-{index}",
                    OperationRisk.Mutating,
                    outcome,
                    $"server:{serverId:D} packages:item-{index} verification:{OperationAuditMetadata.VerificationFor(outcome)}",
                    occurred.AddMinutes(index)),
                cancellationToken);
        }

        var service = new OperationHistoryService(audit);
        var unknown = await service.QueryAsync(
            new OperationAuditQuery(ServerProfileId: serverId, Outcome: OperationOutcome.Unknown, Limit: 2),
            cancellationToken);
        var failed = await service.QueryAsync(
            new OperationAuditQuery(ServerProfileId: serverId, Outcome: OperationOutcome.Failed, Limit: 2),
            cancellationToken);

        Assert.Equal(2, unknown.Items.Count);
        Assert.All(unknown.Items, item => Assert.True(item.HasUnknownRemoteState));
        Assert.All(unknown.Items, item => Assert.Equal(OperationOutcome.Unknown, item.Entry.Outcome));
        Assert.Equal(2, failed.Items.Count);
        Assert.All(failed.Items, item => Assert.False(item.HasUnknownRemoteState));
        Assert.All(failed.Items, item => Assert.Equal(OperationOutcome.Failed, item.Entry.Outcome));
    }

    [Fact]
    public void QueryNormalizationRejectsUnboundedInvertedOrInvalidEnumFilters()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OperationHistoryService.Normalize(new OperationAuditQuery(Limit: OperationHistoryService.MaximumLimit + 1)));
        Assert.Throws<ArgumentException>(() =>
            OperationHistoryService.Normalize(new OperationAuditQuery(
                FromUtc: new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero),
                ToUtc: new DateTimeOffset(2026, 8, 29, 0, 0, 0, TimeSpan.Zero))));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OperationHistoryService.Normalize(new OperationAuditQuery(Risk: (OperationRisk)999)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OperationHistoryService.Normalize(new OperationAuditQuery(Outcome: (OperationOutcome)999)));
    }

    private sealed class TemporaryAppPaths : IAppPaths, IDisposable
    {
        public TemporaryAppPaths()
        {
            RootDirectory = Path.Combine(Path.GetTempPath(), "ServerDesk.Tests", Guid.NewGuid().ToString("N"));
            DataDirectory = Path.Combine(RootDirectory, "data");
            LogsDirectory = Path.Combine(RootDirectory, "logs");
            SettingsFilePath = Path.Combine(RootDirectory, "settings.json");
            DatabaseFilePath = Path.Combine(DataDirectory, "serverdesk.db");
            Directory.CreateDirectory(DataDirectory);
            Directory.CreateDirectory(LogsDirectory);
        }

        public string RootDirectory { get; }
        public string DataDirectory { get; }
        public string LogsDirectory { get; }
        public string SettingsFilePath { get; }
        public string DatabaseFilePath { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(RootDirectory))
            {
                Directory.Delete(RootDirectory, true);
            }
        }
    }
}
