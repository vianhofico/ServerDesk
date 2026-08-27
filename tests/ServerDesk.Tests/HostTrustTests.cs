using System.Text;
using ServerDesk.Application.Audit;
using ServerDesk.Application.HostTrust;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Security;
using Xunit;

namespace ServerDesk.Tests;

public sealed class HostTrustTests
{
    [Fact]
    public void FingerprintUsesCanonicalOpenSshSha256Shape()
    {
        var fingerprint = HostKeyFingerprint.FromHostKey(Encoding.UTF8.GetBytes("test-host-key"));

        Assert.StartsWith("SHA256:", fingerprint.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("=", fingerprint.Value, StringComparison.Ordinal);
        Assert.Equal(fingerprint, HostKeyFingerprint.Parse(fingerprint.Value));
    }

    [Fact]
    public async Task UnknownHostCannotPassWithoutExplicitTrustDecision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryKnownHostRepository();
        var prompt = new QueuePrompt(HostTrustDecision.Cancel);
        var service = new HostTrustService(repository, prompt, new InMemoryAudit());

        var result = await service.VerifyAsync(Observation("key-a"), cancellationToken);

        Assert.False(result.IsTrusted);
        Assert.Equal(HostTrustOutcome.RejectedUnknown, result.Outcome);
        Assert.Empty(repository.Records);
        Assert.Equal(1, prompt.CallCount);
    }

    [Fact]
    public async Task TrustOnceAllowsCurrentAttemptWithoutPersistence()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryKnownHostRepository();
        var prompt = new QueuePrompt(HostTrustDecision.TrustOnce);
        var service = new HostTrustService(repository, prompt, new InMemoryAudit());

        var result = await service.VerifyAsync(Observation("key-a"), cancellationToken);

        Assert.True(result.IsTrusted);
        Assert.Equal(HostTrustOutcome.TrustedOnce, result.Outcome);
        Assert.Empty(repository.Records);
    }

    [Fact]
    public async Task TrustAndSaveMakesNextMatchingObservationSilent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryKnownHostRepository();
        var prompt = new QueuePrompt(HostTrustDecision.TrustAndSave);
        var service = new HostTrustService(repository, prompt, new InMemoryAudit());
        var observation = Observation("key-a");

        var first = await service.VerifyAsync(observation, cancellationToken);
        var second = await service.VerifyAsync(observation, cancellationToken);

        Assert.Equal(HostTrustOutcome.TrustedSaved, first.Outcome);
        Assert.Equal(HostTrustOutcome.TrustedSaved, second.Outcome);
        Assert.True(second.IsTrusted);
        Assert.Single(repository.Records);
        Assert.Equal(1, prompt.CallCount);
    }

    [Fact]
    public async Task ChangedFingerprintIsBlockedEvenIfPromptAttemptsTrustAndSave()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryKnownHostRepository();
        await repository.UpsertAsync(KnownHostRecord.Trust(Observation("old-key")), cancellationToken);
        var prompt = new QueuePrompt(HostTrustDecision.TrustAndSave);
        var service = new HostTrustService(repository, prompt, new InMemoryAudit());

        var result = await service.VerifyAsync(Observation("new-key"), cancellationToken);

        Assert.False(result.IsTrusted);
        Assert.Equal(HostTrustOutcome.RejectedChangedKey, result.Outcome);
        Assert.Single(repository.Records);
    }

    [Fact]
    public async Task ChangedAlgorithmIsAlsoBlocked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryKnownHostRepository();
        await repository.UpsertAsync(KnownHostRecord.Trust(Observation("key-a", "ssh-ed25519")), cancellationToken);
        var service = new HostTrustService(
            repository,
            new QueuePrompt(HostTrustDecision.Cancel),
            new InMemoryAudit());

        var result = await service.VerifyAsync(
            Observation("key-b", "rsa-sha2-512"),
            cancellationToken);

        Assert.False(result.IsTrusted);
        Assert.Equal(HostTrustOutcome.RejectedChangedKey, result.Outcome);
    }

    [Fact]
    public async Task ForgetKnownKeyStillRejectsCurrentChangedKeyAttempt()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var repository = new InMemoryKnownHostRepository();
        await repository.UpsertAsync(KnownHostRecord.Trust(Observation("old-key")), cancellationToken);
        var prompt = new QueuePrompt(
            HostTrustDecision.ForgetKnownKey,
            HostTrustDecision.TrustAndSave);
        var service = new HostTrustService(repository, prompt, new InMemoryAudit());

        var changedAttempt = await service.VerifyAsync(Observation("new-key"), cancellationToken);
        var reconnectAttempt = await service.VerifyAsync(Observation("new-key"), cancellationToken);

        Assert.False(changedAttempt.IsTrusted);
        Assert.Equal(HostTrustOutcome.RejectedChangedKeyAndForgotten, changedAttempt.Outcome);
        Assert.True(reconnectAttempt.IsTrusted);
        Assert.Equal(HostTrustOutcome.TrustedSaved, reconnectAttempt.Outcome);
        Assert.Equal(2, prompt.CallCount);
    }

    private static HostKeyObservation Observation(
        string keyMaterial,
        string algorithm = "ssh-ed25519") =>
        HostKeyObservation.Create(
            "Example.COM",
            22,
            algorithm,
            HostKeyFingerprint.FromHostKey(Encoding.UTF8.GetBytes(keyMaterial)));

    private sealed class QueuePrompt(params HostTrustDecision[] decisions) : IHostTrustPrompt
    {
        private readonly Queue<HostTrustDecision> _decisions = new(decisions);

        public int CallCount { get; private set; }

        public ValueTask<HostTrustDecision> PromptAsync(
            HostTrustChallenge challenge,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(
                _decisions.Count > 0 ? _decisions.Dequeue() : HostTrustDecision.Cancel);
        }
    }

    private sealed class InMemoryKnownHostRepository : IKnownHostRepository
    {
        public List<KnownHostRecord> Records { get; } = [];

        public ValueTask<IReadOnlyList<KnownHostRecord>> ListForEndpointAsync(
            string host,
            int port,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = HostKeyObservation.NormalizeHost(host);
            IReadOnlyList<KnownHostRecord> result = Records
                .Where(record => record.Host == normalized && record.Port == port)
                .ToArray();
            return ValueTask.FromResult(result);
        }

        public ValueTask UpsertAsync(
            KnownHostRecord record,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Records.RemoveAll(existing =>
                existing.Host == record.Host &&
                existing.Port == record.Port &&
                existing.KeyAlgorithm == record.KeyAlgorithm);
            Records.Add(record);
            return ValueTask.CompletedTask;
        }

        public ValueTask DeleteEndpointAsync(
            string host,
            int port,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var normalized = HostKeyObservation.NormalizeHost(host);
            Records.RemoveAll(record => record.Host == normalized && record.Port == port);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class InMemoryAudit : IOperationAudit
    {
        public List<OperationAuditEntry> Entries { get; } = [];

        public ValueTask AppendAsync(
            OperationAuditEntry entry,
            CancellationToken cancellationToken = default)
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
            IReadOnlyList<OperationAuditEntry> result = Entries.Take(limit).ToArray();
            return ValueTask.FromResult(result);
        }
    }
}
