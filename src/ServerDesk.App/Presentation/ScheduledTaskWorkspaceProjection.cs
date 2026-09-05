using ServerDesk.Application.ScheduledTasks;

namespace ServerDesk.App.Presentation;

public sealed record ScheduledTaskWorkspaceSummary(
    int Total,
    int Visible,
    int Cron,
    int SystemdTimers,
    int Enabled);

public sealed record ScheduledTaskCommandState(
    bool CanEnable,
    bool CanDisable,
    bool CanDelete,
    bool CanLoadHistory,
    bool CanLoadRawSource,
    bool CanEditSimpleCron);

public static class ScheduledTaskWorkspaceProjection
{
    public static IReadOnlyList<ScheduledTaskInfo> Filter(
        IReadOnlyList<ScheduledTaskInfo> tasks,
        string? search)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var query = search?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            return tasks.ToArray();
        }

        return tasks.Where(task =>
                task.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                task.Schedule.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                task.CommandOrUnit.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                task.Kind.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (task.Unit?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
            .ToArray();
    }

    public static ScheduledTaskWorkspaceSummary Summarize(
        IReadOnlyList<ScheduledTaskInfo> allTasks,
        IReadOnlyList<ScheduledTaskInfo> visibleTasks)
    {
        ArgumentNullException.ThrowIfNull(allTasks);
        ArgumentNullException.ThrowIfNull(visibleTasks);
        return new ScheduledTaskWorkspaceSummary(
            allTasks.Count,
            visibleTasks.Count,
            allTasks.Count(task => task.Kind == ScheduledTaskKind.Cron),
            allTasks.Count(task => task.Kind == ScheduledTaskKind.SystemdTimer),
            allTasks.Count(task => task.Enabled));
    }

    public static ScheduledTaskCommandState GetCommandState(
        ScheduledTaskInfo? task,
        ScheduledTaskSnapshot? snapshot,
        bool connected,
        bool busy)
    {
        if (!connected || busy || task is null)
        {
            return new ScheduledTaskCommandState(false, false, false, false, false, false);
        }

        var cronAvailable = snapshot?.CronAvailable == true;
        var systemdAvailable = snapshot?.SystemdAvailable == true;
        var capabilityAvailable = task.Kind switch
        {
            ScheduledTaskKind.Cron => cronAvailable,
            ScheduledTaskKind.SystemdTimer => systemdAvailable,
            _ => false,
        };
        if (!capabilityAvailable)
        {
            return new ScheduledTaskCommandState(false, false, false, false, false, false);
        }

        var canDelete = task.Kind == ScheduledTaskKind.Cron ||
                        task.SourcePath?.StartsWith("/etc/systemd/system/", StringComparison.Ordinal) == true;
        return new ScheduledTaskCommandState(
            CanEnable: !task.Enabled,
            CanDisable: task.Enabled,
            CanDelete: canDelete,
            CanLoadHistory: task.Kind == ScheduledTaskKind.SystemdTimer,
            CanLoadRawSource: true,
            CanEditSimpleCron: task.Kind == ScheduledTaskKind.Cron && task.IsSimpleEditable && cronAvailable);
    }
}
