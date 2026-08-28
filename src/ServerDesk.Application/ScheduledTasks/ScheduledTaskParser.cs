using System.Globalization;
using System.Text;
using ServerDesk.Domain.Errors;

namespace ServerDesk.Application.ScheduledTasks;

public static class ScheduledTaskParser
{
    public const string DisabledCronPrefix = "# serverdesk-disabled ";

    public static IReadOnlyList<ScheduledTaskInfo> ParseCrontab(string raw)
    {
        var tasks = new List<ScheduledTaskInfo>();
        var lines = NormalizeNewlines(raw).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var source = lines[index];
            if (string.IsNullOrWhiteSpace(source))
            {
                continue;
            }

            var enabled = true;
            var candidate = source;
            if (source.StartsWith(DisabledCronPrefix, StringComparison.Ordinal))
            {
                enabled = false;
                candidate = source[DisabledCronPrefix.Length..];
            }
            else if (source.TrimStart().StartsWith('#'))
            {
                continue;
            }

            var trimmed = candidate.Trim();
            if (trimmed.Length == 0 || LooksLikeEnvironmentAssignment(trimmed))
            {
                continue;
            }

            if (trimmed.StartsWith('@'))
            {
                var separator = trimmed.IndexOfAny([' ', '\t']);
                if (separator <= 1 || separator >= trimmed.Length - 1)
                {
                    continue;
                }

                var schedule = Sanitize(trimmed[..separator]);
                var command = Sanitize(trimmed[(separator + 1)..].Trim());
                tasks.Add(new ScheduledTaskInfo(
                    ScheduledTaskIdentity.Cron(index, source),
                    ScheduledTaskKind.Cron,
                    BuildCronName(command, index),
                    schedule,
                    command,
                    enabled,
                    null,
                    null,
                    null,
                    false,
                    source,
                    index));
                continue;
            }

            var fields = SplitCronFields(trimmed);
            if (fields is null)
            {
                continue;
            }

            var scheduleText = string.Join(' ', fields.Value.Schedule);
            var commandText = Sanitize(fields.Value.Command);
            var simple = IsSimpleSchedule(fields.Value.Schedule) && commandText.IndexOfAny(['\r', '\n', '\0']) < 0;
            tasks.Add(new ScheduledTaskInfo(
                ScheduledTaskIdentity.Cron(index, source),
                ScheduledTaskKind.Cron,
                BuildCronName(commandText, index),
                scheduleText,
                commandText,
                enabled,
                null,
                null,
                null,
                simple,
                source,
                index));
        }

        return tasks;
    }

    public static IReadOnlyList<(string Unit, string State)> ParseTimerUnitFiles(string output, int maximum)
    {
        if (maximum < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximum));
        }

        var items = new List<(string Unit, string State)>();
        foreach (var rawLine in NormalizeNewlines(output).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var columns = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (columns.Length < 2 || !columns[0].EndsWith(".timer", StringComparison.Ordinal))
            {
                continue;
            }

            var unit = ScheduledTaskIdentity.NormalizeTimerUnit(columns[0]);
            items.Add((unit, Sanitize(columns[1])));
            if (items.Count >= maximum)
            {
                break;
            }
        }

        return items;
    }

    public static ScheduledTaskInfo ParseTimerShow(string unit, string unitFileState, string output)
    {
        ScheduledTaskIdentity.NormalizeTimerUnit(unit);
        var properties = ParseProperties(output);
        var id = Get(properties, "Id", unit);
        if (!string.Equals(id, unit, StringComparison.Ordinal))
        {
            throw new FormatException($"systemctl returned timer '{id}' instead of requested '{unit}'.");
        }

        var loadState = Get(properties, "LoadState", "unknown");
        if (string.Equals(loadState, "not-found", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException($"systemd timer '{unit}' was not found.");
        }

        var activeState = Get(properties, "ActiveState", "unknown");
        var next = NullIfBlank(Get(properties, "NextElapseUSecRealtime", string.Empty));
        var last = NullIfBlank(Get(properties, "LastTriggerUSec", string.Empty));
        var calendar = NullIfBlank(Get(properties, "TimersCalendar", string.Empty));
        var trigger = NullIfBlank(Get(properties, "Triggers", string.Empty));
        var fragmentPath = NullIfBlank(Get(properties, "FragmentPath", string.Empty));
        var enabled = unitFileState.StartsWith("enabled", StringComparison.OrdinalIgnoreCase) ||
                      unitFileState.StartsWith("linked", StringComparison.OrdinalIgnoreCase);
        var active = string.Equals(activeState, "active", StringComparison.OrdinalIgnoreCase);
        var schedule = calendar ?? next ?? "systemd timer";
        var raw = Sanitize(output);

        return new ScheduledTaskInfo(
            $"systemd:{unit}",
            ScheduledTaskKind.SystemdTimer,
            unit,
            schedule,
            trigger ?? unit,
            enabled,
            active,
            last,
            next,
            false,
            raw,
            Unit: unit,
            SourcePath: fragmentPath,
            TriggerUnit: trigger);
    }

    public static IReadOnlyList<string> ParseHistory(string output, int maximumLines)
    {
        if (maximumLines < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLines));
        }

        return NormalizeNewlines(output)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => Sanitize(line.TrimEnd()))
            .Where(line => line.Length > 0)
            .TakeLast(maximumLines)
            .ToArray();
    }

    public static string NormalizeRawCron(string value)
    {
        var normalized = NormalizeNewlines(value);
        return normalized.Length == 0 ? string.Empty : normalized.TrimEnd('\n') + "\n";
    }

    public static RemoteError MapFailure(string detail)
    {
        var text = string.IsNullOrWhiteSpace(detail) ? "Scheduled-task command failed." : Sanitize(detail.Trim());
        var code = text.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("not permitted", StringComparison.OrdinalIgnoreCase)
            ? RemoteErrorCode.PermissionDenied
            : text.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("no such file", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("no crontab", StringComparison.OrdinalIgnoreCase)
                ? RemoteErrorCode.PathNotFound
                : text.Contains("command not found", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("not installed", StringComparison.OrdinalIgnoreCase)
                    ? RemoteErrorCode.CommandNotFound
                    : text.Contains("invalid", StringComparison.OrdinalIgnoreCase) ||
                      text.Contains("bad minute", StringComparison.OrdinalIgnoreCase) ||
                      text.Contains("errors in crontab", StringComparison.OrdinalIgnoreCase)
                        ? RemoteErrorCode.PathConflict
                        : RemoteErrorCode.CommandFailed;
        return new RemoteError(code, text);
    }

    public static bool LooksLikeUnsupportedCrontabValidation(string stderr) =>
        stderr.Contains("invalid option", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("illegal option", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("usage:", StringComparison.OrdinalIgnoreCase) ||
        stderr.Contains("unknown option", StringComparison.OrdinalIgnoreCase);

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\t' or '\n' or >= ' ' and not '\u007f')
            {
                builder.Append(character);
            }
            else
            {
                builder.Append('\uFFFD');
            }
        }

        return builder.ToString();
    }

    private static Dictionary<string, string> ParseProperties(string output)
    {
        var properties = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in NormalizeNewlines(output).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = line.IndexOf('=');
            if (separator <= 0)
            {
                continue;
            }

            properties[line[..separator]] = Sanitize(line[(separator + 1)..]);
        }

        return properties;
    }

    private static string Get(IReadOnlyDictionary<string, string> properties, string key, string fallback) =>
        properties.TryGetValue(key, out var value) ? value : fallback;

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool LooksLikeEnvironmentAssignment(string value)
    {
        var whitespace = value.IndexOfAny([' ', '\t']);
        var equals = value.IndexOf('=');
        return equals > 0 && (whitespace < 0 || equals < whitespace);
    }

    private static (string[] Schedule, string Command)? SplitCronFields(string value)
    {
        var fields = new List<string>(5);
        var position = 0;
        for (var index = 0; index < 5; index++)
        {
            while (position < value.Length && char.IsWhiteSpace(value[position]))
            {
                position++;
            }

            if (position >= value.Length)
            {
                return null;
            }

            var start = position;
            while (position < value.Length && !char.IsWhiteSpace(value[position]))
            {
                position++;
            }

            fields.Add(value[start..position]);
        }

        while (position < value.Length && char.IsWhiteSpace(value[position]))
        {
            position++;
        }

        if (position >= value.Length)
        {
            return null;
        }

        return (fields.ToArray(), value[position..]);
    }

    private static bool IsSimpleSchedule(IReadOnlyList<string> fields)
    {
        try
        {
            new CronTaskDraft(null, fields[0], fields[1], fields[2], fields[3], fields[4], "true", true).Validate();
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string BuildCronName(string command, int index)
    {
        if (command.Length == 0)
        {
            return $"Cron #{index + 1}";
        }

        const int maximum = 72;
        return command.Length <= maximum ? command : command[..maximum] + "…";
    }

    private static string NormalizeNewlines(string? value) =>
        (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
}
