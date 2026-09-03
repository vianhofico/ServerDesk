namespace ServerDesk.Application.Agent;

public static class AgentLifecyclePlanPolicy
{
    private static readonly IReadOnlyList<AgentOwnedResource> ExpectedResources =
    [
        new(AgentOwnedResourceKind.Binary, AgentLifecyclePlanner.BinaryPath),
        new(AgentOwnedResourceKind.StateDirectory, AgentLifecyclePlanner.StateDirectory),
        new(AgentOwnedResourceKind.CacheDirectory, AgentLifecyclePlanner.CacheDirectory),
        new(AgentOwnedResourceKind.ServiceUnit, AgentLifecyclePlanner.ServiceUnitPath),
    ];

    public static void Validate(AgentLifecyclePlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.PlanId == Guid.Empty)
        {
            throw new ArgumentException("Agent lifecycle plan id must be non-empty.", nameof(plan));
        }

        if (!string.Equals(plan.ServiceUnit, AgentLifecyclePlanner.ServiceUnit, StringComparison.Ordinal))
        {
            throw new ArgumentException("Agent lifecycle plan service unit is outside the fixed ownership boundary.", nameof(plan));
        }

        if (plan.OwnedResources is null || plan.OwnedResources.Count != ExpectedResources.Count)
        {
            throw new ArgumentException("Agent lifecycle plan owned-resource set is invalid.", nameof(plan));
        }

        for (var index = 0; index < ExpectedResources.Count; index++)
        {
            if (plan.OwnedResources[index] != ExpectedResources[index])
            {
                throw new ArgumentException("Agent lifecycle plan contains a resource outside the fixed ownership boundary.", nameof(plan));
            }
        }

        if (plan.Steps is null || plan.Steps.Count == 0 || plan.Steps.Count > 8)
        {
            throw new ArgumentException("Agent lifecycle plan step count is invalid.", nameof(plan));
        }

        for (var index = 0; index < plan.Steps.Count; index++)
        {
            var step = plan.Steps[index];
            if (step.Sequence != index + 1 ||
                string.IsNullOrWhiteSpace(step.Action) ||
                step.Action.Length > 512 ||
                string.IsNullOrWhiteSpace(step.Verification) ||
                step.Verification.Length > 512)
            {
                throw new ArgumentException("Agent lifecycle plan contains an invalid step.", nameof(plan));
            }
        }

        switch (plan.Operation)
        {
            case AgentLifecycleOperation.Install:
                Require(plan.CurrentVersion is null && plan.TargetVersion is not null && plan.Steps.Count == 4, plan);
                break;
            case AgentLifecycleOperation.Update:
                if (plan.CurrentVersion is not AgentReleaseVersion current ||
                    plan.TargetVersion is not AgentReleaseVersion target ||
                    plan.Steps.Count != 4)
                {
                    throw new ArgumentException("Agent lifecycle plan version/operation invariants are invalid.", nameof(plan));
                }

                Require(target.CompareTo(current) > 0, plan);
                break;
            case AgentLifecycleOperation.Uninstall:
                Require(plan.TargetVersion is null && plan.Steps.Count == 3, plan);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(plan), "Agent lifecycle plan operation is invalid.");
        }
    }

    private static void Require(bool condition, AgentLifecyclePlan plan)
    {
        if (!condition)
        {
            throw new ArgumentException("Agent lifecycle plan version/operation invariants are invalid.", nameof(plan));
        }
    }
}
