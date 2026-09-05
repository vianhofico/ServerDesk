using ServerDesk.App.Presentation;
using ServerDesk.Application.Services;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ServiceWorkspaceProjectionTests
{
    [Fact]
    public void FilterIsLocalCaseInsensitiveAndPreservesLoadedOrder()
    {
        ServerServiceInfo[] services =
        [
            Service("sshd.service", "OpenSSH server", "active", "running", "enabled"),
            Service("nginx.service", "Web Server", "active", "running", "enabled"),
            Service("worker.service", "Background worker", "inactive", "dead", "disabled"),
        ];

        Assert.Equal(
            ["nginx.service"],
            ServiceWorkspaceProjection.Filter(services, "WEB SERVER").Select(service => service.Unit));
        Assert.Equal(
            ["sshd.service", "nginx.service"],
            ServiceWorkspaceProjection.Filter(services, "RUNNING").Select(service => service.Unit));
        Assert.Equal(
            services.Select(service => service.Unit),
            ServiceWorkspaceProjection.Filter(services, " ").Select(service => service.Unit));
    }

    [Fact]
    public void SummarizeCountsSnapshotAndVisibleRows()
    {
        ServerServiceInfo[] services =
        [
            Service("sshd.service", "OpenSSH server", "active", "running", "enabled"),
            Service("nginx.service", "Web server", "active", "running", "enabled-runtime"),
            Service("worker.service", "Background worker", "inactive", "dead", "disabled"),
        ];
        var visible = ServiceWorkspaceProjection.Filter(services, "worker");

        var summary = ServiceWorkspaceProjection.Summarize(services, visible);

        Assert.Equal(3, summary.TotalServices);
        Assert.Equal(1, summary.VisibleServices);
        Assert.Equal(2, summary.ActiveServices);
        Assert.Equal(2, summary.EnabledServices);
    }

    [Theory]
    [InlineData("active", "enabled", ServerServiceAction.Start, false)]
    [InlineData("inactive", "enabled", ServerServiceAction.Start, true)]
    [InlineData("active", "enabled", ServerServiceAction.Stop, true)]
    [InlineData("inactive", "enabled", ServerServiceAction.Stop, false)]
    [InlineData("active", "enabled", ServerServiceAction.Restart, true)]
    [InlineData("inactive", "enabled", ServerServiceAction.Reload, false)]
    [InlineData("active", "disabled", ServerServiceAction.Enable, true)]
    [InlineData("active", "enabled", ServerServiceAction.Enable, false)]
    [InlineData("active", "enabled-runtime", ServerServiceAction.Disable, true)]
    [InlineData("active", "disabled", ServerServiceAction.Disable, false)]
    public void CanExecuteReflectsCurrentServiceState(
        string activeState,
        string enabledState,
        ServerServiceAction action,
        bool expected)
    {
        var service = Service("example.service", "Example", activeState, "running", enabledState);

        Assert.Equal(expected, ServiceWorkspaceProjection.CanExecute(service, action));
    }

    private static ServerServiceInfo Service(
        string unit,
        string description,
        string active,
        string sub,
        string enabled) =>
        new(unit, description, "loaded", active, sub, enabled, null, string.Empty);
}
