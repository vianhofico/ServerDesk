using ServerDesk.Application.Audit;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Security;

namespace ServerDesk.Application.HostTrust;

public interface IKnownHostRepository
{
    ValueTask<IReadOnlyList<KnownHostRecord>> ListForEndpointAsync(
        string host,
        int port,
        CancellationToken cancellationToken = default);

    ValueTask UpsertAsync(KnownHostRecord record, CancellationToken cancellationToken = default);

    ValueTask DeleteEndpointAsync(string host, int port, CancellationToken cancellationToken = default);
}

public enum HostTrustChallengeKind
{
    Unknown,
    Changed,
}

public enum HostTrustDecision
{
    Cancel,
    TrustOnce,
    TrustAndSave,
    ForgetKnownKey,
}

public sealed record HostTrustChallenge(
    HostTrustChallengeKind Kind,
    HostKeyObservation Observation,
    IReadOnlyList<KnownHostRecord> KnownHosts);

public interface IHostTrustPrompt
{
    ValueTask<HostTrustDecision> PromptAsync(
        HostTrustChallenge challenge,
        CancellationToken cancellationToken = default);
}

public enum HostTrustOutcome
{
    TrustedSaved,
    TrustedOnce,
    RejectedUnknown,
    RejectedChangedKey,
    RejectedChangedKeyAndForgotten,
}

public sealed record HostTrustVerification(
    HostTrustOutcome Outcome,
    HostKeyObservation Observation,
    IReadOnlyList<KnownHostRecord> KnownHosts)
{
    public bool IsTrusted => Outcome is HostTrustOutcome.TrustedSaved or HostTrustOutcome.TrustedOnce;
}

public interface IHostTrustService
{
    ValueTask<HostTrustVerification> VerifyAsync(
        HostKeyObservation observation,
        CancellationToken cancellationToken = default);
}

public sealed class HostTrustService : IHostTrustService
{
    private readonly IKnownHostRepository _repository;
    private readonly IHostTrustPrompt _prompt;
    private readonly IOperationAudit _audit;

    public HostTrustService(
        IKnownHostRepository repository,
        IHostTrustPrompt prompt,
        IOperationAudit audit)
    {
        _repository = repository;
        _prompt = prompt;
        _audit = audit;
    }

    public async ValueTask<HostTrustVerification> VerifyAsync(
        HostKeyObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        var knownHosts = await _repository.ListForEndpointAsync(
                observation.Host,
                observation.Port,
                cancellationToken)
            .ConfigureAwait(false);

        var exactMatch = knownHosts.Any(record =>
            string.Equals(record.KeyAlgorithm, observation.KeyAlgorithm, StringComparison.Ordinal) &&
            record.Fingerprint == observation.Fingerprint);
        if (exactMatch)
        {
            return new HostTrustVerification(HostTrustOutcome.TrustedSaved, observation, knownHosts);
        }

        if (knownHosts.Count > 0)
        {
            return await HandleChangedKeyAsync(observation, knownHosts, cancellationToken).ConfigureAwait(false);
        }

        return await HandleUnknownHostAsync(observation, knownHosts, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<HostTrustVerification> HandleUnknownHostAsync(
        HostKeyObservation observation,
        IReadOnlyList<KnownHostRecord> knownHosts,
        CancellationToken cancellationToken)
    {
        var challenge = new HostTrustChallenge(HostTrustChallengeKind.Unknown, observation, knownHosts);
        var decision = await _prompt.PromptAsync(challenge, cancellationToken).ConfigureAwait(false);

        switch (decision)
        {
            case HostTrustDecision.TrustOnce:
                await AppendAuditAsync(
                        observation,
                        "Trusted unknown SSH host key once",
                        OperationRisk.ReadOnly,
                        OperationOutcome.Succeeded,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new HostTrustVerification(HostTrustOutcome.TrustedOnce, observation, knownHosts);

            case HostTrustDecision.TrustAndSave:
                var record = KnownHostRecord.Trust(observation);
                await _repository.UpsertAsync(record, cancellationToken).ConfigureAwait(false);
                await AppendAuditAsync(
                        observation,
                        "Trusted and saved unknown SSH host key",
                        OperationRisk.Mutating,
                        OperationOutcome.Succeeded,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new HostTrustVerification(
                    HostTrustOutcome.TrustedSaved,
                    observation,
                    [record]);

            case HostTrustDecision.Cancel:
            case HostTrustDecision.ForgetKnownKey:
            default:
                await AppendAuditAsync(
                        observation,
                        "Rejected unknown SSH host key",
                        OperationRisk.ReadOnly,
                        OperationOutcome.Cancelled,
                        cancellationToken)
                    .ConfigureAwait(false);
                return new HostTrustVerification(HostTrustOutcome.RejectedUnknown, observation, knownHosts);
        }
    }

    private async ValueTask<HostTrustVerification> HandleChangedKeyAsync(
        HostKeyObservation observation,
        IReadOnlyList<KnownHostRecord> knownHosts,
        CancellationToken cancellationToken)
    {
        var challenge = new HostTrustChallenge(HostTrustChallengeKind.Changed, observation, knownHosts);
        var decision = await _prompt.PromptAsync(challenge, cancellationToken).ConfigureAwait(false);

        if (decision == HostTrustDecision.ForgetKnownKey)
        {
            await _repository.DeleteEndpointAsync(observation.Host, observation.Port, cancellationToken)
                .ConfigureAwait(false);
            await AppendAuditAsync(
                    observation,
                    "Blocked changed SSH host key and forgot saved key",
                    OperationRisk.Mutating,
                    OperationOutcome.Failed,
                    cancellationToken)
                .ConfigureAwait(false);
            return new HostTrustVerification(
                HostTrustOutcome.RejectedChangedKeyAndForgotten,
                observation,
                knownHosts);
        }

        await AppendAuditAsync(
                observation,
                "Blocked changed SSH host key",
                OperationRisk.ReadOnly,
                OperationOutcome.Failed,
                cancellationToken)
            .ConfigureAwait(false);
        return new HostTrustVerification(HostTrustOutcome.RejectedChangedKey, observation, knownHosts);
    }

    private ValueTask AppendAuditAsync(
        HostKeyObservation observation,
        string action,
        OperationRisk risk,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        var entry = OperationAuditEntry.Create(
            "ssh-host-trust",
            $"{action}: {observation.KeyAlgorithm} {observation.Fingerprint.Value}",
            risk,
            outcome,
            observation.EndpointDisplay);
        return _audit.AppendAsync(entry, cancellationToken);
    }
}
