using System.Text.Json;
using ServerDesk.Application.Deployment;
using ServerDesk.Application.Docker;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DeploymentFixtureTests
{
    [Theory]
    [InlineData("ubuntu-24.04.json")]
    [InlineData("ubuntu-26.04.json")]
    [InlineData("debian-13.json")]
    public void CertifiedDistroTargetsNormalizeWithExplicitIdentityAndHealth(string file)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "Deployment", file)));
        var root = document.RootElement;
        var kind = Enum.Parse<DeploymentTargetKind>(root.GetProperty("kind").GetString()!, ignoreCase: false);
        var healthKind = Enum.Parse<DeploymentHealthCheckKind>(root.GetProperty("healthKind").GetString()!, ignoreCase: false);
        var composeProject = root.TryGetProperty("composeProject", out var composeName)
            ? new DockerComposeProject(
                composeName.GetString()!,
                string.Empty,
                [root.GetProperty("composeConfig").GetString()!])
            : null;
        var health = new DeploymentHealthCheck(
            "fixture-health",
            healthKind,
            root.GetProperty("healthTarget").GetString()!,
            root.TryGetProperty("healthPort", out var port) ? port.GetInt32() : null);
        var target = new DeploymentTarget(
            root.GetProperty("targetId").GetString()!,
            root.GetProperty("distro").GetString()!,
            root.GetProperty("environment").GetString()!,
            kind,
            root.TryGetProperty("repositoryPath", out var repositoryPath) ? repositoryPath.GetString() : null,
            composeProject,
            composeProject is null ? null : DeploymentComposeMode.Up,
            ComposePull: false,
            ComposeBuild: false,
            SystemdUnit: root.TryGetProperty("systemdUnit", out var unit) ? unit.GetString() : null,
            HealthChecks: [health]);

        var normalized = DeploymentTargetPolicy.Normalize(target, DeploymentOptions.Default);

        Assert.Equal(root.GetProperty("targetId").GetString(), normalized.Id);
        Assert.Equal(root.GetProperty("environment").GetString(), normalized.Environment);
        Assert.Equal(kind, normalized.Kind);
        Assert.Single(normalized.HealthChecks);
    }
}
