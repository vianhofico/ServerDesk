using ServerDesk.App.Presentation;
using ServerDesk.Application.ScheduledTasks;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ScheduledTaskWorkspaceProjectionTests
{
    [Fact]
    public void FilterMatchesNameScheduleCommandKindAndUnitLocally()
    {
        var tasks = Tasks();

        Assert.Equal(["Nightly backup"], ScheduledTaskWorkspaceProjection.Filter(tasks, "BACKUP").Select(task => task.Name));
        Assert.Equal(["Nightly backup"], ScheduledTaskWorkspaceProjection.Filter(tasks, "0 2").Select(task => task.Name));
        Assert.Equal(["Cleanup timer"], ScheduledTaskWorkspaceProjection.Filter(tasks, "cleanup.service").Select(task => task.Name));
        Assert.Equal(["Cleanup timer"], ScheduledTaskWorkspaceProjection.Filter(tasks, "systemd").Select(task => task.Name));
        Assert.Equal(tasks.Select(task => task.Name), ScheduledTaskWorkspaceProjection.Filter(tasks, " ").Select(task => task.Name));
    }

    [Fact]
    public void SummarizeUsesLoadedSnapshotAndVisibleProjection()
    {
        var tasks = Tasks();
        var visible = ScheduledTaskWorkspaceProjection.Filter(tasks, "cleanup");

        var summary = ScheduledTaskWorkspaceProjection.Summarize(tasks, visible);

        Assert.Equal(2, summary.Total);
        Assert.Equal(1, summary.Visible);
        Assert.Equal(1, summary.Cron);
        Assert.Equal(1, summary.SystemdTimers);
        Assert.Equal(1, summary.Enabled);
    }

    [Fact]
    public void CommandStateRespectsCapabilityConnectionBusyAndTaskSafety()
    {
        var tasks = Tasks();
        var cron = tasks[0];
        var timer = tasks[1];
        var snapshot = new ScheduledTaskSnapshot(tasks, string.Empty, CronAvailable: true, SystemdAvailable: true, []);

        var cronState = ScheduledTaskWorkspaceProjection.GetCommandState(cron, snapshot, connected: true, busy: false);
        var timerState = ScheduledTaskWorkspaceProjection.GetCommandState(timer, snapshot, connected: true, busy: false);
        var busyState = ScheduledTaskWorkspaceProjection.GetCommandState(cron, snapshot, connected: true, busy: true);
        var disconnectedState = ScheduledTaskWorkspaceProjection.GetCommandState(cron, snapshot, connected: false, busy: false);

        Assert.True(cronState.CanDisable);
        Assert.False(cronState.CanEnable);
        Assert.True(cronState.CanDelete);
        Assert.False(cronState.CanLoadHistory);
        Assert.True(cronState.CanLoadRawSource);
        Assert.True(cronState.CanEditSimpleCron);

        Assert.True(timerState.CanEnable);
        Assert.False(timerState.CanDisable);
        Assert.True(timerState.CanDelete);
        Assert.True(timerState.CanLoadHistory);
        Assert.True(timerState.CanLoadRawSource);
        Assert.False(timerState.CanEditSimpleCron);

        Assert.Equal(new ScheduledTaskCommandState(false, false, false, false, false, false), busyState);
        Assert.Equal(new ScheduledTaskCommandState(false, false, false, false, false, false), disconnectedState);
    }

    [Fact]
    public void SystemdDeleteIsAllowedOnlyForLocallyManagedTimerUnits()
    {
        var localTimer = Tasks()[1];
        var systemTimer = localTimer with { SourcePath = "/usr/lib/systemd/system/cleanup.timer" };
        var snapshot = new ScheduledTaskSnapshot([localTimer, systemTimer], string.Empty, true, true, []);

        var localState = ScheduledTaskWorkspaceProjection.GetCommandState(localTimer, snapshot, true, false);
        var systemState = ScheduledTaskWorkspaceProjection.GetCommandState(systemTimer, snapshot, true, false);

        Assert.True(localState.CanDelete);
        Assert.False(systemState.CanDelete);
    }

    [Fact]
    public void MissingAdapterCapabilityDisablesActionsForThatTaskKind()
    {
        var tasks = Tasks();
        var snapshot = new ScheduledTaskSnapshot(tasks, string.Empty, CronAvailable: false, SystemdAvailable: true, []);

        var cronState = ScheduledTaskWorkspaceProjection.GetCommandState(tasks[0], snapshot, true, false);
        var timerState = ScheduledTaskWorkspaceProjection.GetCommandState(tasks[1], snapshot, true, false);

        Assert.Equal(new ScheduledTaskCommandState(false, false, false, false, false, false), cronState);
        Assert.True(timerState.CanLoadHistory);
    }

    private static ScheduledTaskInfo[] Tasks() =>
    [
        new ScheduledTaskInfo(
            "cron:1:ABCDEF123456",
            ScheduledTaskKind.Cron,
            "Nightly backup",
            "0 2 * * *",
            "/usr/local/bin/backup --target /srv/data",
            Enabled: true,
            Active: null,
            LastRun: null,
            NextRun: null,
            IsSimpleEditable: true,
            Raw: "0 2 * * * /usr/local/bin/backup --target /srv/data",
            SourceLineIndex: 1),
        new ScheduledTaskInfo(
            "systemd:cleanup.timer",
            ScheduledTaskKind.SystemdTimer,
            "Cleanup timer",
            "daily",
            "cleanup.service",
            Enabled: false,
            Active: false,
            LastRun: "2026-09-04 03:00:00 +0700",
            NextRun: "2026-09-05 03:00:00 +0700",
            IsSimpleEditable: false,
            Raw: "cleanup.timer",
            Unit: "cleanup.timer",
            SourcePath: "/etc/systemd/system/cleanup.timer",
            TriggerUnit: "cleanup.service"),
    ];
}
