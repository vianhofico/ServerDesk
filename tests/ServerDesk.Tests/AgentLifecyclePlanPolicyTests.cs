using ServerDesk.Application.Agent;
using ServerDesk.Domain.Operations;
using Xunit;

namespace ServerDesk.Tests;

public sealed class AgentLifecyclePlanPolicyTests
{
    [Fact]
    public void PlannerGeneratedUninstallPlanPassesOwnershipPolicy()
    {
        var plan = AgentLifecyclePlanner.PlanUninstall(new AgentReleaseVersion(1, 2, 3));

        AgentLifecyclePlanPolicy.Validate(plan);
    }

    [Fact]
    public void ForgedPathOrUnitFailsClosed()
    {
        var valid = AgentLifecyclePlanner.PlanUninstall(new AgentReleaseVersion(1, 2, 3));
        var forgedPath = valid with
        {
            OwnedResources =
            [
                new(AgentOwnedResourceKind.Binary, "/usr/bin/ssh"),
                new(AgentOwnedResourceKind.StateDirectory, AgentLifecyclePlanner.StateDirectory),
                new(AgentOwnedResourceKind.CacheDirectory, AgentLifecyclePlanner.CacheDirectory),
                new(AgentOwnedResourceKind.ServiceUnit, AgentLifecyclePlanner.ServiceUnitPath),
            ],
        };
        var forgedUnit = valid with { ServiceUnit = "ssh.service" };

        Assert.Throws<ArgumentException>(() => AgentLifecyclePlanPolicy.Validate(forgedPath));
        Assert.Throws<ArgumentException>(() => AgentLifecyclePlanPolicy.Validate(forgedUnit));
    }

    [Fact]
    public void ForgedVersionInvariantFailsClosed()
    {
        var plan = new AgentLifecyclePlan(
            Guid.NewGuid(),
            AgentLifecycleOperation.Update,
            new AgentReleaseVersion(2, 0, 0),
            new AgentReleaseVersion(1, 0, 0),
            AgentLifecyclePlanner.ServiceUnit,
            [
                new(AgentOwnedResourceKind.Binary, AgentLifecyclePlanner.BinaryPath),
                new(AgentOwnedResourceKind.StateDirectory, AgentLifecyclePlanner.StateDirectory),
                new(AgentOwnedResourceKind.CacheDirectory, AgentLifecyclePlanner.CacheDirectory),
                new(AgentOwnedResourceKind.ServiceUnit, AgentLifecyclePlanner.ServiceUnitPath),
            ],
            [
                new(1, OperationRisk.Mutating, "stage", "verify"),
                new(2, OperationRisk.Destructive, "replace", "verify"),
                new(3, OperationRisk.Destructive, "restart", "verify"),
                new(4, OperationRisk.Mutating, "cleanup", "verify"),
            ]);

        Assert.Throws<ArgumentException>(() => AgentLifecyclePlanPolicy.Validate(plan));
    }
}
