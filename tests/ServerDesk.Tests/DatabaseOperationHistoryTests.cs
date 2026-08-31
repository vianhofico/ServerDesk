using ServerDesk.Application.Audit;
using ServerDesk.Application.Databases;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseOperationHistoryTests
{
    [Fact]
    public void MetadataRoundTripsStableDatabaseIdentityWithoutEndpointOrSecrets()
    {
        var server = ServerProfile.Create("History fixture", "private-db.example", 2222, "secret-user");
        var databaseProfileId = Guid.NewGuid();
        var backupId = Guid.NewGuid();
        const string databaseName = "sales db;$HOME";

        var target = DatabaseOperationAuditMetadata.ForOperation(
            server,
            databaseProfileId,
            DatabaseEngineKind.PostgreSql,
            databaseName,
            backupId,
            "restore",
            "restore-target-verified");

        Assert.StartsWith($"server:{server.Id:D} dbmeta:v1 ", target, StringComparison.Ordinal);
        Assert.DoesNotContain(server.Host, target, StringComparison.Ordinal);
        Assert.DoesNotContain(server.Username, target, StringComparison.Ordinal);
        Assert.DoesNotContain("$HOME", target, StringComparison.Ordinal);
        Assert.DoesNotContain("connection", target, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", target, StringComparison.OrdinalIgnoreCase);

        var parsed = Assert.IsType<DatabaseOperationAuditContext>(DatabaseOperationAuditMetadata.TryParse(target));
        Assert.Equal(databaseProfileId, parsed.DatabaseProfileId);
        Assert.Equal(DatabaseEngineKind.PostgreSql, parsed.Engine);
        Assert.Equal(databaseName, parsed.DatabaseName);
        Assert.Equal(backupId, parsed.BackupId);
        Assert.Equal("restore", parsed.Operation);
        Assert.Equal("restore-target-verified", parsed.Verification);
        Assert.Equal("restore-target-verified", OperationAuditMetadata.TryGetVerification(target));
        Assert.Equal(server.Id, OperationAuditMetadata.TryGetServerProfileId(target));
    }

    [Fact]
    public void MetadataParserFailsClosedForLegacyMalformedOrUnknownPayloads()
    {
        Assert.Null(DatabaseOperationAuditMetadata.TryParse(null));
        Assert.Null(DatabaseOperationAuditMetadata.TryParse("legacy target"));
        Assert.Null(DatabaseOperationAuditMetadata.TryParse("server:00000000-0000-0000-0000-000000000000 dbmeta:v1 engine:PostgreSql"));
        Assert.Throws<ArgumentException>(() => DatabaseOperationAuditMetadata.ForOperation(
            ServerProfile.Create("History fixture", "host", 22, "user"),
            Guid.NewGuid(),
            DatabaseEngineKind.MySql,
            "bad\ndatabase",
            null,
            "backup",
            "backup-verified"));
    }

    [Fact]
    public async Task DatabaseOnlyAndEngineFiltersRemainInsideBoundedHistoryWindow()
    {
        var server = ServerProfile.Create("History fixture", "host", 22, "user");
        var postgresProfile = Guid.NewGuid();
        var mysqlProfile = Guid.NewGuid();
        var auditEntries = new[]
        {
            OperationAuditEntry.Create(
                "database-backup",
                "PostgreSQL backup",
                OperationRisk.Mutating,
                OperationOutcome.Succeeded,
                DatabaseOperationAuditMetadata.ForOperation(
                    server,
                    postgresProfile,
                    DatabaseEngineKind.PostgreSql,
                    "appdb",
                    Guid.NewGuid(),
                    "backup",
                    "backup-verified")),
            OperationAuditEntry.Create(
                "database-restore",
                "MySQL restore",
                OperationRisk.Destructive,
                OperationOutcome.Unknown,
                DatabaseOperationAuditMetadata.ForOperation(
                    server,
                    mysqlProfile,
                    DatabaseEngineKind.MySql,
                    "appdb",
                    Guid.NewGuid(),
                    "restore",
                    "ambiguous-unknown")),
            OperationAuditEntry.Create(
                "foundation",
                "Generic operation",
                OperationRisk.ReadOnly,
                OperationOutcome.Succeeded,
                $"server:{server.Id:D} local verification:post-state-verified"),
        };
        var reader = new RecordingReader(auditEntries);
        var service = new OperationHistoryService(reader);

        var result = await service.QueryAsync(
            new OperationAuditQuery(
                ServerProfileId: server.Id,
                Limit: 1,
                DatabaseOnly: true,
                DatabaseEngine: DatabaseEngineKind.PostgreSql),
            TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal(DatabaseEngineKind.PostgreSql, item.DatabaseContext!.Engine);
        Assert.Equal(postgresProfile, item.DatabaseContext.DatabaseProfileId);
        Assert.Equal("backup-verified", item.Verification);
        Assert.Equal(1, result.AppliedLimit);
        Assert.Equal(OperationHistoryService.MaximumLimit, reader.LastQuery!.Limit);
        Assert.False(reader.LastQuery.DatabaseOnly);
        Assert.Null(reader.LastQuery.DatabaseEngine);
    }

    [Fact]
    public void QueryNormalizationRejectsInvalidDatabaseEngineFilter()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OperationHistoryService.Normalize(new OperationAuditQuery(DatabaseEngine: (DatabaseEngineKind)999)));
    }

    private sealed class RecordingReader : IOperationAuditReader
    {
        private readonly IReadOnlyList<OperationAuditEntry> _entries;

        public RecordingReader(IReadOnlyList<OperationAuditEntry> entries) => _entries = entries;

        public OperationAuditQuery? LastQuery { get; private set; }

        public ValueTask<IReadOnlyList<OperationAuditEntry>> QueryAsync(
            OperationAuditQuery query,
            CancellationToken cancellationToken = default)
        {
            LastQuery = query;
            return ValueTask.FromResult(_entries);
        }
    }
}
