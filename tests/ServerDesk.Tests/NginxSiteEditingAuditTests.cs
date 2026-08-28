using ServerDesk.Application.Audit;
using ServerDesk.Application.Nginx;
using ServerDesk.Application.RemoteEditing;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class NginxSiteEditingAuditTests
{
    [Fact]
    public async Task AuditRecordsStableTargetWithoutCandidateSecretAndMapsAmbiguousToUnknown()
    {
        const string secret = "super-secret-in-nginx-candidate";
        var inner = new StubService(new NginxSiteApplyResult(
            false,
            false,
            false,
            true,
            "ambiguous",
            new RemoteError(RemoteErrorCode.AmbiguousState, "ambiguous")));
        var audit = new RecordingAudit();
        var service = new AuditedNginxSiteEditingService(inner, audit);
        var profile = ServerProfile.Create("nginx", "example.invalid", 22, "dev");
        var path = RemotePath.Parse("/etc/nginx/sites-available/app");
        var metadata = new RemoteFileEntry(
            path,
            "app",
            RemoteFileKind.File,
            10,
            DateTimeOffset.UtcNow,
            1000,
            1000,
            RemoteUnixPermissions.FromMode(640));
        var document = new NginxSiteEditDocument(
            path,
            path,
            new RemoteEditorDocument(metadata, "server {}"));

        var result = await service.ApplyAsync(
            profile,
            document,
            $"server {{ set $token '{secret}'; }}",
            TestContext.Current.CancellationToken);

        Assert.True(result.AmbiguousState);
        var entry = Assert.Single(audit.Entries);
        Assert.Equal("nginx-site-apply", entry.Category);
        Assert.Equal(OperationRisk.Destructive, entry.Risk);
        Assert.Equal(OperationOutcome.Unknown, entry.Outcome);
        Assert.Contains(path.Value, entry.Target, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, entry.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, entry.Target ?? string.Empty, StringComparison.Ordinal);
    }

    private sealed class StubService : INginxSiteEditingService
    {
        private readonly NginxSiteApplyResult _result;
        public StubService(NginxSiteApplyResult result) => _result = result;

        public ValueTask<NginxSiteEditLoadResult> LoadAsync(
            ServerProfile profile,
            RemotePath requestedPath,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<NginxSiteApplyResult> ApplyAsync(
            ServerProfile profile,
            NginxSiteEditDocument original,
            string candidateText,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_result);
    }

    private sealed class RecordingAudit : IOperationAudit
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
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<OperationAuditEntry>>(Entries.Take(limit).ToArray());
    }
}
