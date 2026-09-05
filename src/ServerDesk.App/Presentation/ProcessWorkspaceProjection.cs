using System.Globalization;
using ServerDesk.Application.Processes;

namespace ServerDesk.App.Presentation;

public sealed record ProcessWorkspaceRow(
    int ProcessId,
    int ParentProcessId,
    string User,
    string State,
    double CpuPercent,
    long ResidentBytes,
    string CpuText,
    string MemoryText,
    string ElapsedText,
    string Command,
    string Arguments,
    string SearchText);

public sealed record ProcessWorkspaceSummary(
    int TotalProcesses,
    int VisibleProcesses,
    int UserCount,
    long ResidentBytes)
{
    public string ResidentMemoryText => ProcessWorkspaceProjection.FormatBytes(ResidentBytes);
}

public static class ProcessWorkspaceProjection
{
    public static IReadOnlyList<ProcessWorkspaceRow> Project(IEnumerable<ServerProcessInfo> processes)
    {
        ArgumentNullException.ThrowIfNull(processes);

        return processes
            .Select(process =>
            {
                var memory = FormatBytes(process.ResidentBytes);
                var elapsed = FormatElapsed(process.Elapsed);
                return new ProcessWorkspaceRow(
                    process.ProcessId,
                    process.ParentProcessId,
                    process.User,
                    process.State,
                    process.CpuPercent,
                    process.ResidentBytes,
                    process.CpuPercent.ToString("0.0", CultureInfo.InvariantCulture) + "%",
                    memory,
                    elapsed,
                    process.Command,
                    process.Arguments,
                    $"{process.ProcessId} {process.ParentProcessId} {process.User} {process.State} {process.Command} {process.Arguments}");
            })
            .ToArray();
    }

    public static IReadOnlyList<ProcessWorkspaceRow> Filter(
        IEnumerable<ProcessWorkspaceRow> rows,
        string? query)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var normalized = query?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? rows.ToArray()
            : rows
                .Where(row => row.SearchText.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                .ToArray();
    }

    public static ProcessWorkspaceSummary Summarize(
        IReadOnlyCollection<ProcessWorkspaceRow> allRows,
        IReadOnlyCollection<ProcessWorkspaceRow> visibleRows)
    {
        ArgumentNullException.ThrowIfNull(allRows);
        ArgumentNullException.ThrowIfNull(visibleRows);

        return new ProcessWorkspaceSummary(
            allRows.Count,
            visibleRows.Count,
            allRows.Select(row => row.User).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            allRows.Sum(row => Math.Max(0L, row.ResidentBytes)));
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024d && unit < units.Length - 1)
        {
            value /= 1024d;
            unit++;
        }

        return $"{value:0.#} {units[unit]}";
    }

    public static string FormatElapsed(TimeSpan elapsed) =>
        elapsed.TotalDays >= 1
            ? $"{(int)elapsed.TotalDays}d {elapsed:hh\\:mm\\:ss}"
            : elapsed.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
}
