using ServerDesk.Application.Audit;
using ServerDesk.Application.Databases;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DatabaseBackupAuditTests
{
    [Fact]
    public async Task AuditContainsTargetIdentityAndVerificationOutcomeButNoCredential()
    {
        const string credential = "db-password-never-audit";
        var server = ServerProfile.Create("Database host", "db.example.invalid", 22, "operator");
        var databaseProfileId = Guid.NewGuid();
        var manifest = new DatabaseBackupManifest(
            Guid.NewGuid(),
            server.Id,
            databaseProfileId,
            DatabaseEngineKind.PostgreSql,
            "appdb",
            "appuser",
            RemotePath.Parse("/var/backups/serverdesk/app.dump"),
            DatabaseBackupFormat.PostgreSqlCustom,
            "pg_dump",
            "pg_dump (PostgreSQL) 18.6",
            DateTimeOffset.UtcNow,
            new DatabaseBackupVerificationEvidence(
                4096,
                new string('A', 64),
                "pg_restore --list verified",
                DateTimeOffset.UtcNow),
            true);
        var inner = new StubBackupService(new DatabaseBackupCreateResult(
            manifest,
            false,
            false,
            true,
            "verified"));
        var audit = new CapturingAudit();
        var service = new AuditedDatabaseBackupService(inner, audit);

        var result = await service.CreateAsync(
            server,
            new DatabaseBackupRequest(databaseProfileId, "appdb", "/var/backups/serverdesk"),
            TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("database-backup", entry.Category);
        Assert.Contains(databaseProfileId.ToString(), entry.Target!, StringComparison.Ordinal);
        Assert.Contains("database:appdb", entry.Target!, StringComparison.Ordinal);
        Assert.Contains(manifest.BackupId.ToString(), entry.Target!, StringComparison.Ordinal);
        Assert.DoesNotContain(credential, entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(credential, entry.Target!, StringComparison.Ordinal);
        Assert.DoesNotContain("password", entry.Summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password", entry.Target!, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubBackupService : IDatabaseBackupService
    {
        private readonly DatabaseBackupCreateResult _result;

        public StubBackupService(DatabaseBackupCreateResult result) => _result = result;

        public Task<DatabaseBackupCreateResult> CreateAsync(
            ServerProfile serverProfile,
            DatabaseBackupRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_result);
        }

        public ValueTask<IReadOnlyList<DatabaseBackupManifest>> ListHistoryAsync(
            Guid serverProfileId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<DatabaseBackupManifest>>([]);
        }
    }

    private sealed class CapturingAudit : IOperationAudit
    {
        public List<OperationAuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(OperationAuditEntry entry, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Entries.Add(entry);
            return ValueTask.CompletedTask;
        }

        public ValueTask<IReadOnlyList<OperationAuditEntry>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult<IReadOnlyList<OperationAuditEntry>>(Entries.Take(limit).ToArray());
        }
    }
}
