using System.Globalization;
using System.Net;
using ServerDesk.Application.Docker;
using ServerDesk.Application.Git;
using ServerDesk.Application.Services;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Deployment;

public enum DeploymentTargetKind
{
    GitCompose,
    GitSystemd,
    Compose,
}

public enum DeploymentComposeMode
{
    Up,
    Restart,
}

public enum DeploymentHealthCheckKind
{
    Http,
    Tcp,
    Process,
    SystemdService,
    DockerContainer,
}

public enum DeploymentStepKind
{
    GitFetch,
    GitFastForward,
    ComposePull,
    ComposeBuild,
    ComposeUp,
    ComposeRestart,
    SystemdRestart,
    HealthCheck,
    RollbackComposeDown,
}

public enum DeploymentStepOutcome
{
    Succeeded,
    Failed,
    Skipped,
    Unknown,
    Cancelled,
}

public enum DeploymentRunStatus
{
    Succeeded,
    Failed,
    Ambiguous,
    Cancelled,
}

public enum DeploymentRollbackKind
{
    ComposeDown,
}

public sealed record DeploymentHealthCheck(
    string Name,
    DeploymentHealthCheckKind Kind,
    string Target,
    int? Port = null);

public sealed record DeploymentTarget(
    string Id,
    string Name,
    string Environment,
    DeploymentTargetKind Kind,
    string? RepositoryPath,
    DockerComposeProject? ComposeProject,
    DeploymentComposeMode? ComposeMode,
    bool ComposePull,
    bool ComposeBuild,
    string? SystemdUnit,
    IReadOnlyList<DeploymentHealthCheck> HealthChecks);

public sealed record DeploymentPlanStep(
    int Sequence,
    DeploymentStepKind Kind,
    OperationRisk Risk,
    string Description,
    bool Conditional = false,
    string? HealthCheckName = null);

public sealed record DeploymentObservedState(
    string? GitRevision,
    string? GitBranch,
    string? GitUpstream,
    bool ComposeProjectPresent,
    string? ComposeFingerprint,
    string? SystemdActiveState,
    string? SystemdSubState);

public sealed record DeploymentPlan(
    Guid PlanId,
    DeploymentTarget Target,
    IReadOnlyList<DeploymentPlanStep> Steps,
    DeploymentObservedState ObservedState,
    bool DeterministicRollbackPossible,
    string RollbackSummary);

public sealed record DeploymentPlanResult(
    DeploymentPlan? Plan,
    RemoteError? Error)
{
    public bool IsSuccess => Plan is not null && Error is null;
}

public sealed record DeploymentStepResult(
    DeploymentPlanStep Step,
    DeploymentStepOutcome Outcome,
    string Message,
    RemoteError? Error = null);

public sealed record DeploymentRollbackPlan(
    Guid ExecutionId,
    DeploymentRollbackKind Kind,
    string TargetId,
    string Environment,
    DockerComposeProject Project,
    string ExpectedComposeFingerprint,
    string Description);

public sealed record DeploymentRunResult(
    Guid ExecutionId,
    DeploymentRunStatus Status,
    IReadOnlyList<DeploymentStepResult> Steps,
    string Message,
    RemoteError? Error = null,
    DeploymentRollbackPlan? Rollback = null)
{
    public bool IsSuccess => Status == DeploymentRunStatus.Succeeded;
}

public sealed record DeploymentRollbackResult(
    bool IsSuccess,
    bool AmbiguousState,
    string Message,
    RemoteError? Error = null,
    DeploymentStepResult? Step = null);

public sealed record DeploymentHealthCheckResult(
    bool IsSuccess,
    string Message,
    RemoteError? Error = null);

public sealed record DeploymentOptions(
    TimeSpan HealthCommandTimeout,
    int HealthAttempts,
    TimeSpan HealthRetryDelay,
    int MaximumHealthChecks)
{
    public static DeploymentOptions Default { get; } = new(
        TimeSpan.FromSeconds(8),
        3,
        TimeSpan.FromSeconds(1),
        20);

    public void Validate()
    {
        if (HealthCommandTimeout <= TimeSpan.Zero || HealthCommandTimeout > TimeSpan.FromMinutes(1))
        {
            throw new ArgumentOutOfRangeException(nameof(HealthCommandTimeout));
        }

        if (HealthAttempts is < 1 or > 10)
        {
            throw new ArgumentOutOfRangeException(nameof(HealthAttempts));
        }

        if (HealthRetryDelay < TimeSpan.Zero || HealthRetryDelay > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(HealthRetryDelay));
        }

        if (MaximumHealthChecks is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumHealthChecks));
        }
    }
}

public interface IDeploymentHealthCheckRunner
{
    Task<DeploymentHealthCheckResult> RunAsync(
        ServerProfile profile,
        DeploymentHealthCheck check,
        CancellationToken cancellationToken = default);
}

public interface IDeploymentOrchestrationService
{
    Task<DeploymentPlanResult> PreviewAsync(
        ServerProfile profile,
        DeploymentTarget target,
        CancellationToken cancellationToken = default);

    Task<DeploymentRunResult> ExecuteAsync(
        ServerProfile profile,
        DeploymentPlan plan,
        CancellationToken cancellationToken = default);

    Task<DeploymentRollbackResult> RollbackAsync(
        ServerProfile profile,
        DeploymentRollbackPlan rollback,
        CancellationToken cancellationToken = default);
}

public static class DeploymentTargetPolicy
{
    public static DeploymentTarget Normalize(DeploymentTarget target, DeploymentOptions options)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        var id = NormalizeToken(target.Id, nameof(target.Id), 64);
        var environment = NormalizeToken(target.Environment, nameof(target.Environment), 64);
        var name = NormalizeLabel(target.Name, nameof(target.Name), 128);
        var healthChecks = target.HealthChecks ?? throw new ArgumentException("Deployment targets require explicit health checks.", nameof(target));
        if (healthChecks.Count is < 1 || healthChecks.Count > 100 || healthChecks.Count > options.MaximumHealthChecks)
        {
            throw new ArgumentException($"Deployment targets require between 1 and {options.MaximumHealthChecks} explicit health checks.", nameof(target));
        }

        var normalizedChecks = healthChecks.Select(NormalizeHealthCheck).ToArray();
        if (normalizedChecks.Select(check => check.Name).Distinct(StringComparer.Ordinal).Count() != normalizedChecks.Length)
        {
            throw new ArgumentException("Deployment health-check names must be unique within a target.", nameof(target));
        }

        var repositoryPath = string.IsNullOrWhiteSpace(target.RepositoryPath)
            ? null
            : GitRepositoryPath.Normalize(target.RepositoryPath);
        var composeProject = target.ComposeProject is null ? null : DockerComposeIdentity.Normalize(target.ComposeProject);
        var systemdUnit = string.IsNullOrWhiteSpace(target.SystemdUnit) ? null : target.SystemdUnit;
        if (systemdUnit is not null)
        {
            SystemdServiceManager.ValidateUnitName(systemdUnit);
        }

        switch (target.Kind)
        {
            case DeploymentTargetKind.GitCompose:
                Require(repositoryPath is not null, "Git + Compose deployment requires an explicit repository path.");
                Require(composeProject is not null, "Git + Compose deployment requires an explicit Compose project and config path list.");
                Require(target.ComposeMode is not null, "Git + Compose deployment requires an explicit Compose up/restart mode.");
                Require(systemdUnit is null, "Git + Compose deployment cannot also contain a systemd restart target.");
                break;
            case DeploymentTargetKind.GitSystemd:
                Require(repositoryPath is not null, "Git + systemd deployment requires an explicit repository path.");
                Require(systemdUnit is not null, "Git + systemd deployment requires an explicit .service unit.");
                Require(composeProject is null && target.ComposeMode is null && !target.ComposePull && !target.ComposeBuild,
                    "Git + systemd deployment cannot contain Compose operations.");
                break;
            case DeploymentTargetKind.Compose:
                Require(repositoryPath is null, "Compose-only deployment cannot contain a Git repository path.");
                Require(composeProject is not null, "Compose-only deployment requires an explicit Compose project and config path list.");
                Require(target.ComposeMode is not null, "Compose-only deployment requires an explicit Compose up/restart mode.");
                Require(systemdUnit is null, "Compose-only deployment cannot also contain a systemd restart target.");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(target));
        }

        return target with
        {
            Id = id,
            Name = name,
            Environment = environment,
            RepositoryPath = repositoryPath,
            ComposeProject = composeProject,
            SystemdUnit = systemdUnit,
            HealthChecks = normalizedChecks,
        };
    }

    public static DeploymentHealthCheck NormalizeHealthCheck(DeploymentHealthCheck check)
    {
        ArgumentNullException.ThrowIfNull(check);
        var name = NormalizeLabel(check.Name, nameof(check.Name), 100);
        return check.Kind switch
        {
            DeploymentHealthCheckKind.Http => NormalizeHttp(check with { Name = name }),
            DeploymentHealthCheckKind.Tcp => NormalizeTcp(check with { Name = name }),
            DeploymentHealthCheckKind.Process => NormalizeProcess(check with { Name = name }),
            DeploymentHealthCheckKind.SystemdService => NormalizeService(check with { Name = name }),
            DeploymentHealthCheckKind.DockerContainer => NormalizeContainer(check with { Name = name }),
            _ => throw new ArgumentOutOfRangeException(nameof(check)),
        };
    }

    private static DeploymentHealthCheck NormalizeHttp(DeploymentHealthCheck check)
    {
        if (!Uri.TryCreate(check.Target, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new FormatException("HTTP health checks require an absolute http/https URL without credentials, query strings or fragments.");
        }

        if (uri.Port is < 1 or > 65535)
        {
            throw new FormatException("HTTP health-check port is outside the valid TCP range.");
        }

        return check with { Target = uri.AbsoluteUri, Port = null };
    }

    private static DeploymentHealthCheck NormalizeTcp(DeploymentHealthCheck check)
    {
        var host = NormalizeLabel(check.Target, nameof(check.Target), 253);
        if (host.StartsWith("-", StringComparison.Ordinal) ||
            !(IPAddress.TryParse(host, out _) || Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            throw new FormatException("TCP health checks require a normalized DNS name or IP address.");
        }

        if (check.Port is not { } port || port is < 1 or > 65535)
        {
            throw new FormatException("TCP health checks require an explicit port between 1 and 65535.");
        }

        return check with { Target = host, Port = port };
    }

    private static DeploymentHealthCheck NormalizeProcess(DeploymentHealthCheck check)
    {
        if (!int.TryParse(check.Target, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
        {
            throw new FormatException("Process health checks require an explicit positive PID.");
        }

        return check with { Target = pid.ToString(CultureInfo.InvariantCulture), Port = null };
    }

    private static DeploymentHealthCheck NormalizeService(DeploymentHealthCheck check)
    {
        SystemdServiceManager.ValidateUnitName(check.Target);
        return check with { Port = null };
    }

    private static DeploymentHealthCheck NormalizeContainer(DeploymentHealthCheck check)
    {
        var target = NormalizeToken(check.Target, nameof(check.Target), 128);
        return check with { Target = target, Port = null };
    }

    private static string NormalizeToken(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal) || value.Length > maximumLength ||
            !char.IsAsciiLetterOrDigit(value[0]) ||
            value.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')))
        {
            throw new FormatException($"{parameterName} must start with a letter or digit and contain only letters, digits, '.', '-' or '_' without surrounding whitespace.");
        }

        return value;
    }

    private static string NormalizeLabel(string value, string parameterName, int maximumLength)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        var normalized = value.Trim();
        if (!string.Equals(value, normalized, StringComparison.Ordinal) || normalized.Length > maximumLength || normalized.Any(char.IsControl))
        {
            throw new FormatException($"{parameterName} must be normalized text without surrounding whitespace or control characters.");
        }

        return normalized;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new ArgumentException(message);
        }
    }
}
