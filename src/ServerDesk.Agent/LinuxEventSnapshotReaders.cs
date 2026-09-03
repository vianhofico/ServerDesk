using System.Diagnostics;
using System.Globalization;

namespace ServerDesk.Agent;

public interface IAgentProcessSnapshotReader
{
    ValueTask<IReadOnlyDictionary<int, string>> CaptureAsync(CancellationToken cancellationToken = default);
}

public interface IAgentServiceSnapshotReader
{
    ValueTask<IReadOnlyDictionary<string, ObservedServiceState>> CaptureAsync(CancellationToken cancellationToken = default);
}

public enum ObservedServiceState
{
    Unknown,
    Active,
    Inactive,
    Activating,
    Deactivating,
    Failed,
}

internal sealed record ProcessEventReading(bool Started, int ProcessId, string Name, DateTimeOffset CapturedAtUtc);
internal sealed record ServiceEventReading(string Unit, ObservedServiceState PreviousState, ObservedServiceState CurrentState, DateTimeOffset CapturedAtUtc);

public sealed class LinuxProcessSnapshotReader : IAgentProcessSnapshotReader
{
    public async ValueTask<IReadOnlyDictionary<int, string>> CaptureAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = new Dictionary<int, string>();
        foreach (var directory in Directory.EnumerateDirectories("/proc"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = Path.GetFileName(directory);
            if (!int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) || processId <= 0)
            {
                continue;
            }

            try
            {
                var processName = (await File.ReadAllTextAsync(Path.Combine(directory, "comm"), cancellationToken)
                    .ConfigureAwait(false)).Trim();
                if (!string.IsNullOrWhiteSpace(processName))
                {
                    snapshot[processId] = processName.Length <= 256 ? processName : processName[..256];
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (IOException)
            {
                // Process exited between directory enumeration and reading /proc/<pid>/comm.
            }
            catch (UnauthorizedAccessException)
            {
                // Skip entries the service account cannot inspect.
            }
        }

        return snapshot;
    }
}

public sealed class SystemdServiceSnapshotReader : IAgentServiceSnapshotReader
{
    private static readonly string[] Arguments =
    [
        "list-units",
        "--type=service",
        "--all",
        "--no-legend",
        "--no-pager",
        "--plain",
    ];

    public async ValueTask<IReadOnlyDictionary<string, ObservedServiceState>> CaptureAsync(
        CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = BuildStartInfo() };
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start the fixed systemd observation command.");
        }

        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            _ = await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("systemd service observation failed with non-zero exit status.");
            }

            return Parse(output);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process completed while cancellation was being observed.
            }

            throw;
        }
    }

    internal static ProcessStartInfo BuildStartInfo()
    {
        var info = new ProcessStartInfo("systemctl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in Arguments)
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["LC_ALL"] = "C";
        return info;
    }

    internal static IReadOnlyDictionary<string, ObservedServiceState> Parse(string output)
    {
        var snapshot = new Dictionary<string, ObservedServiceState>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 4 || !fields[0].EndsWith(".service", StringComparison.Ordinal))
            {
                continue;
            }

            snapshot[fields[0]] = NormalizeState(fields[2]);
        }

        return snapshot;
    }

    internal static ObservedServiceState NormalizeState(string state) =>
        state.Trim().ToLowerInvariant() switch
        {
            "active" => ObservedServiceState.Active,
            "inactive" => ObservedServiceState.Inactive,
            "activating" => ObservedServiceState.Activating,
            "deactivating" => ObservedServiceState.Deactivating,
            "failed" => ObservedServiceState.Failed,
            _ => ObservedServiceState.Unknown,
        };
}

internal static class AgentEventDiff
{
    public static IReadOnlyList<ProcessEventReading> Processes(
        IReadOnlyDictionary<int, string> previous,
        IReadOnlyDictionary<int, string> current,
        DateTimeOffset capturedAtUtc)
    {
        var events = new List<ProcessEventReading>();
        foreach (var entry in previous.OrderBy(pair => pair.Key))
        {
            if (!current.TryGetValue(entry.Key, out var currentName))
            {
                events.Add(new ProcessEventReading(false, entry.Key, entry.Value, capturedAtUtc));
            }
            else if (!string.Equals(entry.Value, currentName, StringComparison.Ordinal))
            {
                events.Add(new ProcessEventReading(false, entry.Key, entry.Value, capturedAtUtc));
                events.Add(new ProcessEventReading(true, entry.Key, currentName, capturedAtUtc));
            }
        }

        foreach (var entry in current.OrderBy(pair => pair.Key))
        {
            if (!previous.ContainsKey(entry.Key))
            {
                events.Add(new ProcessEventReading(true, entry.Key, entry.Value, capturedAtUtc));
            }
        }

        return events;
    }

    public static IReadOnlyList<ServiceEventReading> Services(
        IReadOnlyDictionary<string, ObservedServiceState> previous,
        IReadOnlyDictionary<string, ObservedServiceState> current,
        DateTimeOffset capturedAtUtc)
    {
        var units = previous.Keys.Concat(current.Keys).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal);
        var events = new List<ServiceEventReading>();
        foreach (var unit in units)
        {
            var previousState = previous.TryGetValue(unit, out var before) ? before : ObservedServiceState.Unknown;
            var currentState = current.TryGetValue(unit, out var after) ? after : ObservedServiceState.Unknown;
            if (previousState != currentState)
            {
                events.Add(new ServiceEventReading(unit, previousState, currentState, capturedAtUtc));
            }
        }

        return events;
    }
}
