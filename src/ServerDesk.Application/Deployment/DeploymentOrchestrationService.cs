using System.Security.Cryptography;
using System.Text;
using ServerDesk.Application.Audit;
using ServerDesk.Application.Docker;
using ServerDesk.Application.Git;
using ServerDesk.Application.Services;
using ServerDesk.Domain.Audit;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Deployment;

public sealed class DeploymentOrchestrationService : IDeploymentOrchestrationService
{
    private readonly IGitOperationsService _git;
    private readonly IDockerComposeService _compose;
    private readonly IServerServiceManager _services;
    private readonly IDeploymentHealthCheckRunner _health;
    private readonly IOperationAudit _audit;
    private readonly DeploymentOptions _options;

    public DeploymentOrchestrationService(
        IGitOperationsService git,
        IDockerComposeService compose,
        IServerServiceManager services,
        IDeploymentHealthCheckRunner health,
        IOperationAudit audit,
        DeploymentOptions options)
    {
        _git = git ?? throw new ArgumentNullException(nameof(git));
        _compose = compose ?? throw new ArgumentNullException(nameof(compose));
        _services = services ?? throw new ArgumentNullException(nameof(services));
        _health = health ?? throw new ArgumentNullException(nameof(health));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _options.Validate();
    }

    public async Task<DeploymentPlanResult> PreviewAsync(
        ServerProfile profile,
        DeploymentTarget target,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        DeploymentTarget normalized;
        try
        {
            normalized = DeploymentTargetPolicy.Normalize(target, _options);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return PlanFailure(RemoteErrorCode.InvalidEndpoint, exception.Message);
        }

        var observed = await ObserveAsync(profile, normalized, cancellationToken).ConfigureAwait(false);
        if (observed.Error is not null)
        {
            return new DeploymentPlanResult(null, observed.Error);
        }

        var steps = BuildSteps(normalized);
        var rollbackPossible = normalized.Kind == DeploymentTargetKind.Compose &&
                               normalized.ComposeMode == DeploymentComposeMode.Up &&
                               !normalized.ComposePull &&
                               !normalized.ComposeBuild &&
                               !observed.State!.ComposeProjectPresent;
        var rollbackSummary = rollbackPossible
            ? "Rollback can deterministically return this Compose-only target to its pre-deployment absent state by running verified compose down."
            : "Automatic rollback is unavailable because the supported primitives cannot deterministically restore the complete pre-deployment revision/config/image state.";

        return new DeploymentPlanResult(
            new DeploymentPlan(
                Guid.NewGuid(),
                normalized,
                steps,
                observed.State!,
                rollbackPossible,
                rollbackSummary),
            null);
    }

    public async Task<DeploymentRunResult> ExecuteAsync(
        ServerProfile profile,
        DeploymentPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(plan);
        var executionId = Guid.NewGuid();
        var results = new List<DeploymentStepResult>(plan.Steps.Count);

        DeploymentTarget normalized;
        try
        {
            normalized = DeploymentTargetPolicy.Normalize(plan.Target, _options);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            var error = new RemoteError(RemoteErrorCode.InvalidEndpoint, exception.Message);
            return new DeploymentRunResult(executionId, DeploymentRunStatus.Failed, results, error.Message, error);
        }

        if (!Equals(normalized, plan.Target))
        {
            var error = new RemoteError(RemoteErrorCode.PathConflict, "The deployment target changed after preview. Create a new plan before executing.");
            return new DeploymentRunResult(executionId, DeploymentRunStatus.Failed, results, error.Message, error);
        }

        var current = await ObserveAsync(profile, normalized, cancellationToken).ConfigureAwait(false);
        if (current.Error is not null)
        {
            return new DeploymentRunResult(executionId, DeploymentRunStatus.Failed, results, current.Error.Message, current.Error);
        }

        if (!ObservedStateMatches(plan.ObservedState, current.State!))
        {
            var error = new RemoteError(
                RemoteErrorCode.PathConflict,
                "Deployment state changed after preview. Refresh and preview again before any mutation.");
            return new DeploymentRunResult(executionId, DeploymentRunStatus.Failed, results, error.Message, error);
        }

        GitRepositorySnapshot? gitSnapshot = null;
        DeploymentRollbackPlan? rollback = null;
        var mutatingStepStarted = false;

        foreach (var step in plan.Steps)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                DeploymentStepResult result;
                switch (step.Kind)
                {
                    case DeploymentStepKind.GitFetch:
                        mutatingStepStarted = true;
                        result = await ExecuteGitFetchAsync(profile, normalized, step, cancellationToken).ConfigureAwait(false);
                        if (result.Outcome == DeploymentStepOutcome.Succeeded)
                        {
                            var fetch = _lastGitFetch;
                            gitSnapshot = fetch?.VerifiedSnapshot;
                        }
                        break;
                    case DeploymentStepKind.GitFastForward:
                        if (gitSnapshot is null)
                        {
                            result = StepFailure(step, RemoteErrorCode.PathConflict, "Git fast-forward cannot run because the verified post-fetch repository state is unavailable.");
                            break;
                        }

                        if (gitSnapshot.Behind == 0 && gitSnapshot.Ahead == 0)
                        {
                            result = new DeploymentStepResult(step, DeploymentStepOutcome.Skipped, "Git fast-forward was skipped because the fetched upstream state is already current.");
                            break;
                        }

                        mutatingStepStarted = true;
                        result = await ExecuteGitFastForwardAsync(profile, normalized, step, gitSnapshot, cancellationToken).ConfigureAwait(false);
                        break;
                    case DeploymentStepKind.ComposePull:
                        mutatingStepStarted = true;
                        result = await ExecuteComposeAsync(profile, normalized, step, DockerComposeAction.Pull, executionId, cancellationToken).ConfigureAwait(false);
                        break;
                    case DeploymentStepKind.ComposeBuild:
                        mutatingStepStarted = true;
                        result = await ExecuteComposeAsync(profile, normalized, step, DockerComposeAction.Build, executionId, cancellationToken).ConfigureAwait(false);
                        break;
                    case DeploymentStepKind.ComposeUp:
                        mutatingStepStarted = true;
                        var up = await ExecuteComposeDetailedAsync(profile, normalized, step, DockerComposeAction.Up, cancellationToken).ConfigureAwait(false);
                        result = up.Step;
                        if (result.Outcome == DeploymentStepOutcome.Succeeded &&
                            plan.DeterministicRollbackPossible &&
                            up.Action?.VerifiedDetails is { } verifiedDetails)
                        {
                            rollback = new DeploymentRollbackPlan(
                                executionId,
                                DeploymentRollbackKind.ComposeDown,
                                normalized.Id,
                                normalized.Environment,
                                normalized.ComposeProject!,
                                ComposeFingerprint(verifiedDetails),
                                "Return the Compose-only target to the verified pre-deployment absent state with compose down.");
                        }
                        break;
                    case DeploymentStepKind.ComposeRestart:
                        mutatingStepStarted = true;
                        result = await ExecuteComposeAsync(profile, normalized, step, DockerComposeAction.Restart, executionId, cancellationToken).ConfigureAwait(false);
                        break;
                    case DeploymentStepKind.SystemdRestart:
                        mutatingStepStarted = true;
                        result = await ExecuteSystemdRestartAsync(profile, normalized, step, cancellationToken).ConfigureAwait(false);
                        break;
                    case DeploymentStepKind.HealthCheck:
                        mutatingStepStarted = false;
                        result = await ExecuteHealthAsync(profile, normalized, step, cancellationToken).ConfigureAwait(false);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(step));
                }

                result = await AppendAuditWarningAsync(profile, normalized, result, cancellationToken).ConfigureAwait(false);
                results.Add(result);
                if (result.Outcome is DeploymentStepOutcome.Failed or DeploymentStepOutcome.Unknown or DeploymentStepOutcome.Cancelled)
                {
                    return StopForStep(executionId, results, result, rollback);
                }

                mutatingStepStarted = false;
            }
            catch (OperationCanceledException)
            {
                var outcome = mutatingStepStarted ? DeploymentStepOutcome.Unknown : DeploymentStepOutcome.Cancelled;
                var code = mutatingStepStarted ? RemoteErrorCode.AmbiguousState : RemoteErrorCode.OperationCancelled;
                var message = mutatingStepStarted
                    ? "Deployment cancellation occurred after a mutation step started. Current remote state is unknown; refresh before any retry."
                    : "Deployment execution was cancelled before the next mutation.";
                var error = new RemoteError(code, message);
                var cancelled = new DeploymentStepResult(step, outcome, message, error);
                cancelled = await AppendAuditWarningAsync(profile, normalized, cancelled, CancellationToken.None).ConfigureAwait(false);
                results.Add(cancelled);
                return StopForStep(executionId, results, cancelled, rollback);
            }
        }

        return new DeploymentRunResult(
            executionId,
            DeploymentRunStatus.Succeeded,
            results,
            $"Deployment '{normalized.Id}' for environment '{normalized.Environment}' completed and all configured health checks passed.");
    }

    public async Task<DeploymentRollbackResult> RollbackAsync(
        ServerProfile profile,
        DeploymentRollbackPlan rollback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(rollback);
        if (rollback.ExecutionId == Guid.Empty ||
            !IsAuditToken(rollback.TargetId) ||
            !IsAuditToken(rollback.Environment) ||
            rollback.Kind != DeploymentRollbackKind.ComposeDown)
        {
            return RollbackFailure(RemoteErrorCode.InvalidEndpoint, "The rollback token is invalid or unsupported.");
        }

        DockerComposeProject project;
        try
        {
            project = DockerComposeIdentity.Normalize(rollback.Project);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            return RollbackFailure(RemoteErrorCode.InvalidEndpoint, exception.Message);
        }

        var snapshot = await _compose.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!snapshot.IsSuccess || snapshot.Snapshot is null)
        {
            return RollbackFailure(
                snapshot.Error?.Code ?? RemoteErrorCode.CommandFailed,
                snapshot.Error?.Message ?? "Compose state could not be inspected before rollback.",
                snapshot.Error);
        }

        var liveProject = FindProject(snapshot.Snapshot, project);
        if (liveProject is null)
        {
            return RollbackFailure(RemoteErrorCode.PathConflict, "Rollback was not executed because the Compose project is no longer present in the expected state.");
        }

        var details = await _compose.InspectProjectAsync(profile, liveProject, cancellationToken).ConfigureAwait(false);
        if (!details.IsSuccess || details.Details is null)
        {
            return RollbackFailure(
                details.Error?.Code ?? RemoteErrorCode.CommandFailed,
                details.Error?.Message ?? "Compose project details could not be verified before rollback.",
                details.Error);
        }

        if (!string.Equals(ComposeFingerprint(details.Details), rollback.ExpectedComposeFingerprint, StringComparison.Ordinal))
        {
            return RollbackFailure(
                RemoteErrorCode.PathConflict,
                "Rollback was blocked because the Compose project changed after the failed deployment. Refresh and resolve the live state explicitly.");
        }

        var step = new DeploymentPlanStep(
            1,
            DeploymentStepKind.RollbackComposeDown,
            OperationRisk.Destructive,
            "compose-down-rollback");
        DockerComposeActionResult action;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            action = await _compose.ExecuteAsync(profile, project, DockerComposeAction.Down, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var error = new RemoteError(
                RemoteErrorCode.AmbiguousState,
                "Rollback cancellation occurred after compose down started. Refresh Compose state before any retry.");
            var unknown = new DeploymentStepResult(step, DeploymentStepOutcome.Unknown, error.Message, error);
            _ = await TryAuditAsync(profile, rollback.TargetId, rollback.Environment, unknown, CancellationToken.None).ConfigureAwait(false);
            return new DeploymentRollbackResult(false, true, error.Message, error, unknown);
        }

        var outcome = ToOutcome(action.IsSuccess, action.Error);
        var result = new DeploymentStepResult(step, outcome, action.Message, action.Error);
        result = await AppendAuditWarningAsync(profile, rollback.TargetId, rollback.Environment, result, cancellationToken).ConfigureAwait(false);
        if (!action.IsSuccess)
        {
            var ambiguous = action.Error?.Code == RemoteErrorCode.AmbiguousState;
            return new DeploymentRollbackResult(false, ambiguous, result.Message, action.Error, result);
        }

        return new DeploymentRollbackResult(
            true,
            false,
            "Deterministic Compose rollback completed and the project absence was verified.",
            Step: result);
    }

    private GitFetchResult? _lastGitFetch;

    private async Task<DeploymentStepResult> ExecuteGitFetchAsync(
        ServerProfile profile,
        DeploymentTarget target,
        DeploymentPlanStep step,
        CancellationToken cancellationToken)
    {
        var result = await _git.FetchAsync(profile, target.RepositoryPath!, cancellationToken).ConfigureAwait(false);
        _lastGitFetch = result;
        return new DeploymentStepResult(step, ToOutcome(result.IsSuccess, result.Error), result.Message, result.Error);
    }

    private async Task<DeploymentStepResult> ExecuteGitFastForwardAsync(
        ServerProfile profile,
        DeploymentTarget target,
        DeploymentPlanStep step,
        GitRepositorySnapshot fetched,
        CancellationToken cancellationToken)
    {
        var preview = await _git.PreviewPullAsync(profile, target.RepositoryPath!, cancellationToken).ConfigureAwait(false);
        if (!preview.IsSuccess || preview.Preview is null)
        {
            var error = preview.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, "Git safe-pull preview failed after fetch.");
            return new DeploymentStepResult(step, DeploymentStepOutcome.Failed, error.Message, error);
        }

        if (!preview.Preview.CanApply)
        {
            return StepFailure(step, RemoteErrorCode.PathConflict, preview.Preview.Message);
        }

        if (!string.Equals(preview.Preview.CurrentRevision, fetched.Revision, StringComparison.Ordinal))
        {
            return StepFailure(step, RemoteErrorCode.PathConflict, "Repository revision changed between fetch verification and safe-pull execution.");
        }

        var result = await _git.PullAsync(profile, target.RepositoryPath!, fetched.Revision, cancellationToken).ConfigureAwait(false);
        return new DeploymentStepResult(step, ToOutcome(result.IsSuccess, result.Error), result.Message, result.Error);
    }

    private async Task<DeploymentStepResult> ExecuteComposeAsync(
        ServerProfile profile,
        DeploymentTarget target,
        DeploymentPlanStep step,
        DockerComposeAction action,
        Guid executionId,
        CancellationToken cancellationToken)
    {
        _ = executionId;
        var result = await _compose.ExecuteAsync(profile, target.ComposeProject!, action, cancellationToken).ConfigureAwait(false);
        return new DeploymentStepResult(step, ToOutcome(result.IsSuccess, result.Error), result.Message, result.Error);
    }

    private async Task<ComposeStepResult> ExecuteComposeDetailedAsync(
        ServerProfile profile,
        DeploymentTarget target,
        DeploymentPlanStep step,
        DockerComposeAction action,
        CancellationToken cancellationToken)
    {
        var result = await _compose.ExecuteAsync(profile, target.ComposeProject!, action, cancellationToken).ConfigureAwait(false);
        return new ComposeStepResult(
            new DeploymentStepResult(step, ToOutcome(result.IsSuccess, result.Error), result.Message, result.Error),
            result);
    }

    private async Task<DeploymentStepResult> ExecuteSystemdRestartAsync(
        ServerProfile profile,
        DeploymentTarget target,
        DeploymentPlanStep step,
        CancellationToken cancellationToken)
    {
        var result = await _services.ExecuteAsync(profile, target.SystemdUnit!, ServerServiceAction.Restart, cancellationToken).ConfigureAwait(false);
        return new DeploymentStepResult(step, ToOutcome(result.IsSuccess, result.Error), result.Message, result.Error);
    }

    private async Task<DeploymentStepResult> ExecuteHealthAsync(
        ServerProfile profile,
        DeploymentTarget target,
        DeploymentPlanStep step,
        CancellationToken cancellationToken)
    {
        var check = target.HealthChecks.FirstOrDefault(candidate => string.Equals(candidate.Name, step.HealthCheckName, StringComparison.Ordinal));
        if (check is null)
        {
            return StepFailure(step, RemoteErrorCode.PathConflict, "The health-check definition changed after preview.");
        }

        var result = await _health.RunAsync(profile, check, cancellationToken).ConfigureAwait(false);
        return new DeploymentStepResult(
            step,
            result.IsSuccess ? DeploymentStepOutcome.Succeeded : DeploymentStepOutcome.Failed,
            result.Message,
            result.Error);
    }

    private async Task<ObservationResult> ObserveAsync(
        ServerProfile profile,
        DeploymentTarget target,
        CancellationToken cancellationToken)
    {
        string? gitRevision = null;
        string? gitBranch = null;
        string? gitUpstream = null;
        var composePresent = false;
        string? composeFingerprint = null;
        string? systemdActive = null;
        string? systemdSub = null;

        if (target.Kind is DeploymentTargetKind.GitCompose or DeploymentTargetKind.GitSystemd)
        {
            var git = await _git.InspectAsync(profile, target.RepositoryPath!, cancellationToken).ConfigureAwait(false);
            if (!git.IsSuccess || git.Snapshot is null)
            {
                return ObservationResult.Failure(git.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, "Git repository inspection failed."));
            }

            var snapshot = git.Snapshot;
            if (snapshot.IsDetached)
            {
                return ObservationResult.Failure(new RemoteError(RemoteErrorCode.PathConflict, "Deployment requires a named Git branch; detached HEAD is unsupported."));
            }

            if (!snapshot.IsClean)
            {
                return ObservationResult.Failure(new RemoteError(RemoteErrorCode.PathConflict, "Deployment requires a clean Git index and worktree."));
            }

            if (string.IsNullOrWhiteSpace(snapshot.Upstream))
            {
                return ObservationResult.Failure(new RemoteError(RemoteErrorCode.PathConflict, "Deployment requires the selected Git branch to have an explicit upstream."));
            }

            if (snapshot.Ahead > 0)
            {
                return ObservationResult.Failure(new RemoteError(RemoteErrorCode.PathConflict, "Deployment will not reconcile local commits that are ahead of or diverged from upstream."));
            }

            gitRevision = snapshot.Revision;
            gitBranch = snapshot.Branch;
            gitUpstream = snapshot.Upstream;
        }

        if (target.Kind is DeploymentTargetKind.GitCompose or DeploymentTargetKind.Compose)
        {
            var compose = await _compose.InspectAsync(profile, cancellationToken).ConfigureAwait(false);
            if (!compose.IsSuccess || compose.Snapshot is null)
            {
                return ObservationResult.Failure(compose.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, "Compose runtime inspection failed."));
            }

            if (!compose.Snapshot.Runtime.IsUsable)
            {
                return ObservationResult.Failure(new RemoteError(RemoteErrorCode.CapabilityUnavailable, compose.Snapshot.Runtime.Detail));
            }

            var liveProject = FindProject(compose.Snapshot, target.ComposeProject!);
            composePresent = liveProject is not null;
            if (target.ComposeMode == DeploymentComposeMode.Restart && liveProject is null)
            {
                return ObservationResult.Failure(new RemoteError(RemoteErrorCode.PathNotFound, "Compose restart requires the explicitly configured project to already exist."));
            }

            if (liveProject is not null)
            {
                var details = await _compose.InspectProjectAsync(profile, liveProject, cancellationToken).ConfigureAwait(false);
                if (!details.IsSuccess || details.Details is null)
                {
                    return ObservationResult.Failure(details.Error ?? new RemoteError(RemoteErrorCode.CommandFailed, "Compose project details could not be inspected."));
                }

                composeFingerprint = ComposeFingerprint(details.Details);
            }
        }

        if (target.Kind == DeploymentTargetKind.GitSystemd)
        {
            var service = await _services.GetAsync(profile, target.SystemdUnit!, cancellationToken).ConfigureAwait(false);
            if (!service.IsSuccess || service.Services.Count != 1)
            {
                return ObservationResult.Failure(service.Error ?? new RemoteError(RemoteErrorCode.PathNotFound, "The configured systemd service could not be inspected."));
            }

            systemdActive = service.Services[0].ActiveState;
            systemdSub = service.Services[0].SubState;
        }

        return ObservationResult.Success(new DeploymentObservedState(
            gitRevision,
            gitBranch,
            gitUpstream,
            composePresent,
            composeFingerprint,
            systemdActive,
            systemdSub));
    }

    private static IReadOnlyList<DeploymentPlanStep> BuildSteps(DeploymentTarget target)
    {
        var steps = new List<DeploymentPlanStep>();
        void Add(DeploymentStepKind kind, OperationRisk risk, string description, bool conditional = false, string? healthName = null) =>
            steps.Add(new DeploymentPlanStep(steps.Count + 1, kind, risk, description, conditional, healthName));

        if (target.Kind is DeploymentTargetKind.GitCompose or DeploymentTargetKind.GitSystemd)
        {
            Add(DeploymentStepKind.GitFetch, OperationRisk.Mutating, "git-fetch");
            Add(DeploymentStepKind.GitFastForward, OperationRisk.Mutating, "git-fast-forward-if-behind", conditional: true);
        }

        if (target.Kind is DeploymentTargetKind.GitCompose or DeploymentTargetKind.Compose)
        {
            if (target.ComposePull)
            {
                Add(DeploymentStepKind.ComposePull, OperationRisk.Mutating, "compose-pull");
            }

            if (target.ComposeBuild)
            {
                Add(DeploymentStepKind.ComposeBuild, OperationRisk.Mutating, "compose-build");
            }

            if (target.ComposeMode == DeploymentComposeMode.Up)
            {
                Add(DeploymentStepKind.ComposeUp, OperationRisk.Destructive, "compose-up");
            }
            else
            {
                Add(DeploymentStepKind.ComposeRestart, OperationRisk.Destructive, "compose-restart");
            }
        }

        if (target.Kind == DeploymentTargetKind.GitSystemd)
        {
            Add(DeploymentStepKind.SystemdRestart, OperationRisk.Destructive, "systemd-restart");
        }

        foreach (var check in target.HealthChecks)
        {
            Add(DeploymentStepKind.HealthCheck, OperationRisk.ReadOnly, "health-check", healthName: check.Name);
        }

        return steps;
    }

    private static DockerComposeProject? FindProject(DockerComposeSnapshot snapshot, DockerComposeProject expected)
    {
        var normalizedExpected = DockerComposeIdentity.Normalize(expected);
        var byName = snapshot.Projects.FirstOrDefault(project => string.Equals(project.Name, normalizedExpected.Name, StringComparison.Ordinal));
        if (byName is null)
        {
            return null;
        }

        var normalizedLive = DockerComposeIdentity.Normalize(byName);
        if (!normalizedLive.ConfigFiles.SequenceEqual(normalizedExpected.ConfigFiles, StringComparer.Ordinal))
        {
            throw new FormatException("A Compose project with the requested name exists, but its ordered config-file identity does not match the deployment target.");
        }

        return normalizedLive;
    }

    private static string ComposeFingerprint(DockerComposeProjectDetails details)
    {
        var builder = new StringBuilder(details.NormalizedConfigJson.Length + details.Services.Count * 160);
        builder.Append(details.Project.Name).Append('\n');
        foreach (var config in details.Project.ConfigFiles)
        {
            builder.Append(config).Append('\n');
        }

        builder.Append(details.NormalizedConfigJson).Append('\n');
        foreach (var service in details.Services.OrderBy(service => service.Service, StringComparer.Ordinal).ThenBy(service => service.Name, StringComparer.Ordinal))
        {
            builder.Append(service.Id).Append('|')
                .Append(service.Name).Append('|')
                .Append(service.Service).Append('|')
                .Append(service.Image).Append('\n');
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private static bool ObservedStateMatches(DeploymentObservedState expected, DeploymentObservedState actual) =>
        string.Equals(expected.GitRevision, actual.GitRevision, StringComparison.Ordinal) &&
        string.Equals(expected.GitBranch, actual.GitBranch, StringComparison.Ordinal) &&
        string.Equals(expected.GitUpstream, actual.GitUpstream, StringComparison.Ordinal) &&
        expected.ComposeProjectPresent == actual.ComposeProjectPresent &&
        string.Equals(expected.ComposeFingerprint, actual.ComposeFingerprint, StringComparison.Ordinal) &&
        string.Equals(expected.SystemdActiveState, actual.SystemdActiveState, StringComparison.Ordinal) &&
        string.Equals(expected.SystemdSubState, actual.SystemdSubState, StringComparison.Ordinal);

    private async Task<DeploymentStepResult> AppendAuditWarningAsync(
        ServerProfile profile,
        DeploymentTarget target,
        DeploymentStepResult result,
        CancellationToken cancellationToken) =>
        await AppendAuditWarningAsync(profile, target.Id, target.Environment, result, cancellationToken).ConfigureAwait(false);

    private async Task<DeploymentStepResult> AppendAuditWarningAsync(
        ServerProfile profile,
        string targetId,
        string environment,
        DeploymentStepResult result,
        CancellationToken cancellationToken)
    {
        var persisted = await TryAuditAsync(profile, targetId, environment, result, cancellationToken).ConfigureAwait(false);
        return persisted
            ? result
            : result with { Message = result.Message + " Audit persistence failed; do not retry a mutation solely for that reason." };
    }

    private async ValueTask<bool> TryAuditAsync(
        ServerProfile profile,
        string targetId,
        string environment,
        DeploymentStepResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = result.Outcome switch
            {
                DeploymentStepOutcome.Succeeded or DeploymentStepOutcome.Skipped => OperationOutcome.Succeeded,
                DeploymentStepOutcome.Unknown => OperationOutcome.Unknown,
                DeploymentStepOutcome.Cancelled => OperationOutcome.Cancelled,
                _ => OperationOutcome.Failed,
            };
            var target = $"{profile.Username}@{profile.Host}:{profile.Port} deployment:{targetId}/{environment}";
            var entry = OperationAuditEntry.Create(
                "deployment-step",
                $"Deployment step {result.Step.Sequence}:{result.Step.Kind} for {targetId}/{environment}",
                result.Step.Risk,
                outcome,
                target);
            await _audit.AppendAsync(entry, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DeploymentRunResult StopForStep(
        Guid executionId,
        IReadOnlyList<DeploymentStepResult> results,
        DeploymentStepResult failed,
        DeploymentRollbackPlan? rollback)
    {
        var status = failed.Outcome switch
        {
            DeploymentStepOutcome.Unknown => DeploymentRunStatus.Ambiguous,
            DeploymentStepOutcome.Cancelled => DeploymentRunStatus.Cancelled,
            _ => DeploymentRunStatus.Failed,
        };
        return new DeploymentRunResult(executionId, status, results, failed.Message, failed.Error, rollback);
    }

    private static DeploymentStepOutcome ToOutcome(bool success, RemoteError? error) =>
        success
            ? DeploymentStepOutcome.Succeeded
            : error?.Code == RemoteErrorCode.AmbiguousState
                ? DeploymentStepOutcome.Unknown
                : DeploymentStepOutcome.Failed;

    private static DeploymentStepResult StepFailure(DeploymentPlanStep step, RemoteErrorCode code, string message)
    {
        var error = new RemoteError(code, message);
        return new DeploymentStepResult(step, code == RemoteErrorCode.AmbiguousState ? DeploymentStepOutcome.Unknown : DeploymentStepOutcome.Failed, message, error);
    }

    private static DeploymentPlanResult PlanFailure(RemoteErrorCode code, string message) =>
        new(null, new RemoteError(code, message));

    private static DeploymentRollbackResult RollbackFailure(RemoteErrorCode code, string message, RemoteError? original = null)
    {
        var error = original ?? new RemoteError(code, message);
        return new DeploymentRollbackResult(false, code == RemoteErrorCode.AmbiguousState, message, error);
    }

    private static bool IsAuditToken(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 64 &&
        char.IsAsciiLetterOrDigit(value[0]) &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.');

    private sealed record ObservationResult(DeploymentObservedState? State, RemoteError? Error)
    {
        public static ObservationResult Success(DeploymentObservedState state) => new(state, null);

        public static ObservationResult Failure(RemoteError error) => new(null, error);
    }

    private sealed record ComposeStepResult(DeploymentStepResult Step, DockerComposeActionResult? Action);
}
