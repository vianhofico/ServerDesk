using ServerDesk.Application.ScheduledTasks;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ScheduledTaskParserRegressionTests
{
    [Fact]
    public void TimerShowUsesActualUnitFileStateInsteadOfCallerExpectation()
    {
        const string output = "Id=demo.timer\nLoadState=loaded\nActiveState=inactive\nUnitFileState=disabled\nTriggers=demo.service\nFragmentPath=/etc/systemd/system/demo.timer\n";

        var timer = ScheduledTaskParser.ParseTimerShow("demo.timer", "enabled", output);

        Assert.False(timer.Enabled);
        Assert.False(timer.Active);
    }
}
