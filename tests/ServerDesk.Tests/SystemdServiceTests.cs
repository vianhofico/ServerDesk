using ServerDesk.Application.Services;
using Xunit;

namespace ServerDesk.Tests;

public sealed class SystemdServiceTests
{
    [Fact]
    public void ListUnitsParserNormalizesDescriptionWithSpaces()
    {
        const string output =
            "ssh.service loaded active running OpenBSD Secure Shell server\n" +
            "cron.service loaded active running Regular background program processing daemon\n";

        var services = SystemdServiceParser.ParseListUnits(output);

        Assert.Equal(2, services.Count);
        Assert.Equal("ssh.service", services[0].Unit);
        Assert.Equal("active", services[0].ActiveState);
        Assert.Equal("running", services[0].SubState);
        Assert.Equal("OpenBSD Secure Shell server", services[0].Description);
    }

    [Fact]
    public void UnitFileParserNormalizesEnabledStateAndIgnoresPresetColumn()
    {
        const string output =
            "ssh.service enabled enabled\n" +
            "cron.service disabled enabled\n";

        var states = SystemdServiceParser.ParseUnitFiles(output);

        Assert.Equal("enabled", states["ssh.service"]);
        Assert.Equal("disabled", states["cron.service"]);
    }

    [Fact]
    public void ShowParserRequiresTypedProperties()
    {
        const string output =
            "Id=ssh.service\n" +
            "Description=OpenBSD Secure Shell server\n" +
            "LoadState=loaded\n" +
            "ActiveState=active\n" +
            "SubState=running\n" +
            "UnitFileState=enabled\n" +
            "MainPID=4242\n" +
            "StatusText=Accepting connections\n";

        var service = SystemdServiceParser.ParseShow(output);

        Assert.Equal("ssh.service", service.Unit);
        Assert.Equal(4242, service.MainProcessId);
        Assert.Equal("Accepting connections", service.StatusText);
        Assert.True(service.IsActive);
    }

    [Fact]
    public void ShowParserMapsZeroMainPidToNull()
    {
        const string output =
            "Id=fixture.service\nDescription=Fixture\nLoadState=loaded\nActiveState=inactive\n" +
            "SubState=dead\nUnitFileState=disabled\nMainPID=0\nStatusText=\n";

        var service = SystemdServiceParser.ParseShow(output);

        Assert.Null(service.MainProcessId);
    }

    [Theory]
    [InlineData("ssh.service")]
    [InlineData("postgresql@15-main.service")]
    [InlineData("my-app.service")]
    public void UnitValidationAcceptsOrdinaryServiceIdentifiers(string unit)
    {
        SystemdServiceManager.ValidateUnitName(unit);
    }

    [Theory]
    [InlineData("ssh")]
    [InlineData("../ssh.service")]
    [InlineData("ssh.service;reboot")]
    [InlineData("ssh service.service")]
    [InlineData("/etc/ssh.service")]
    public void UnitValidationRejectsPathsWhitespaceAndShellSyntax(string unit)
    {
        Assert.Throws<ArgumentException>(() => SystemdServiceManager.ValidateUnitName(unit));
    }

    [Theory]
    [InlineData(ServerServiceAction.Stop)]
    [InlineData(ServerServiceAction.Restart)]
    [InlineData(ServerServiceAction.Disable)]
    public void WorkloadDisruptingActionsAreDestructive(ServerServiceAction action)
    {
        Assert.True(SystemdServiceManager.IsDisruptive(action));
    }

    [Theory]
    [InlineData(ServerServiceAction.Start)]
    [InlineData(ServerServiceAction.Reload)]
    [InlineData(ServerServiceAction.Enable)]
    public void NonDisruptingMutationsAreNotMarkedDestructive(ServerServiceAction action)
    {
        Assert.False(SystemdServiceManager.IsDisruptive(action));
    }

    [Fact]
    public void MalformedListRowFailsClosed()
    {
        Assert.Throws<FormatException>(() => SystemdServiceParser.ParseListUnits("not-enough-columns"));
    }
}
