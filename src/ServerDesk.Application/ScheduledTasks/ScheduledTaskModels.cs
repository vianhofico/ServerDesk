using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ServerDesk.Domain.Errors;

namespace ServerDesk.Application.ScheduledTasks;

public enum ScheduledTaskKind
{
    Cron,
    SystemdTimer,
}

public enum ScheduledTaskMutation
{
    Create,
    Update,
    Enable,
    Disable,
    Delete,
    ApplyRawCron,
}

public sealed record ScheduledTaskInfo(
    string Id,
    ScheduledTaskKind Kind,
    string Name,
    string Schedule,
    string CommandOrUnit,
    bool Enabled,
    bool? Active,
    string? LastRun,
    string? NextRun,
    bool IsSimpleEditable,
    string Raw,
    int? SourceLineIndex = null,
    string? Unit = null,
    string? SourcePath = null,
    string? TriggerUnit = null);

public sealed record ScheduledTaskSnapshot(
    IReadOnlyList<ScheduledTaskInfo> Tasks,
    string RawCrontab,
    bool CronAvailable,
    bool SystemdAvailable,
    IReadOnlyList<string> Warnings);

public sealed record ScheduledTaskSnapshotResult(
    ScheduledTaskSnapshot? Snapshot,
    RemoteError? Error)
{
    public bool IsSuccess => Snapshot is not null && Error is null;
}

public sealed record ScheduledTaskTextResult(
    string Text,
    RemoteError? Error)
{
    public bool IsSuccess => Error is null;
}

public sealed record ScheduledTaskMutationResult(
    bool IsSuccess,
    RemoteError? Error,
    string Message,
    ScheduledTaskSnapshot? VerifiedSnapshot = null);

public sealed record CronTaskDraft(
    string? ExistingTaskId,
    string Minute,
    string Hour,
    string DayOfMonth,
    string Month,
    string DayOfWeek,
    string Command,
    bool Enabled)
{
    private static readonly Regex FieldPattern = new(
        "^[0-9*/?,\\-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public string Schedule => $"{Minute} {Hour} {DayOfMonth} {Month} {DayOfWeek}";

    public string ToCronLine()
    {
        Validate();
        var line = $"{Schedule} {Command.Trim()}";
        return Enabled ? line : ScheduledTaskParser.DisabledCronPrefix + line;
    }

    public void Validate()
    {
        ValidateField(Minute, nameof(Minute));
        ValidateField(Hour, nameof(Hour));
        ValidateField(DayOfMonth, nameof(DayOfMonth));
        ValidateField(Month, nameof(Month));
        ValidateField(DayOfWeek, nameof(DayOfWeek));
        ArgumentException.ThrowIfNullOrWhiteSpace(Command);
        if (Command.Length > 4096 || Command.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new FormatException("Cron command must be a single line no longer than 4096 characters.");
        }
    }

    private static void ValidateField(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 || value != value.Trim() || !FieldPattern.IsMatch(value))
        {
            throw new FormatException($"Cron field '{name}' is not a supported simple cron field.");
        }
    }
}

public static class ScheduledTaskIdentity
{
    private static readonly Regex SystemdTimerPattern = new(
        "^[A-Za-z0-9_.@:-]+\\.timer$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Cron(int lineIndex, string rawLine)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lineIndex);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(rawLine)))[..12];
        return $"cron:{lineIndex}:{hash}";
    }

    public static string NormalizeTimerUnit(string unit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);
        if (unit != unit.Trim() || unit.Length > 240 || !SystemdTimerPattern.IsMatch(unit))
        {
            throw new FormatException("systemd timer unit name is invalid.");
        }

        return unit;
    }
}

public sealed record ScheduledTaskOptions(
    TimeSpan CommandTimeout,
    int MaximumTimers,
    int MaximumHistoryLines,
    int MaximumRawCronBytes)
{
    public static ScheduledTaskOptions Default { get; } = new(
        TimeSpan.FromSeconds(20),
        128,
        200,
        256 * 1024);
}
