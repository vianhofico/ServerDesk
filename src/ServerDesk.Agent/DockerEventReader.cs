using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ServerDesk.Agent;

public enum ObservedDockerObjectType
{
    Unknown,
    Container,
    Image,
    Volume,
    Network,
    Daemon,
}

public enum ObservedDockerEventKind
{
    Unknown,
    Create,
    Start,
    Stop,
    Die,
    Destroy,
    Pause,
    Unpause,
    Restart,
    Rename,
    HealthStatus,
    Attach,
    Detach,
    Kill,
    Oom,
    Update,
    Connect,
    Disconnect,
    Pull,
    Push,
    Tag,
    Untag,
    Delete,
    Mount,
    Unmount,
    Reload,
    Prune,
    ExecCreated,
    ExecStarted,
    ExecDied,
}

public sealed record ObservedDockerEvent(
    ObservedDockerObjectType ObjectType,
    ObservedDockerEventKind Kind,
    string? ObjectId,
    DateTimeOffset CapturedAtUtc);

public interface IAgentDockerEventReader
{
    IAsyncEnumerable<ObservedDockerEvent> StreamAsync(CancellationToken cancellationToken = default);
}

public sealed class DockerCliEventReader : IAgentDockerEventReader
{
    public async IAsyncEnumerable<ObservedDockerEvent> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = BuildStartInfo() };
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start Docker event observation.");
        }

        var errorDrain = DrainAsync(process.StandardError);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (!string.IsNullOrWhiteSpace(line))
                {
                    yield return Parse(line);
                }
            }

            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Docker event observation ended with a non-zero exit status.");
            }
        }
        finally
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
                // The process completed while cancellation or cleanup was being observed.
            }

            try
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // The process never reached a started state or already completed during cleanup.
            }

            await errorDrain.ConfigureAwait(false);
        }
    }

    internal static ProcessStartInfo BuildStartInfo()
    {
        var info = new ProcessStartInfo("docker")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("events");
        info.ArgumentList.Add("--format");
        info.ArgumentList.Add("{{json .}}");
        info.Environment["LC_ALL"] = "C";
        return info;
    }

    internal static ObservedDockerEvent Parse(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        using var document = JsonDocument.Parse(line, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Docker event payload is not an object.");
        }

        var rawType = GetString(root, "Type") ?? string.Empty;
        var rawAction = GetString(root, "Action") ?? GetString(root, "status") ?? string.Empty;
        var objectId = ReadObjectId(root);
        var capturedAtUtc = ReadTimestamp(root);
        return new ObservedDockerEvent(
            NormalizeObjectType(rawType),
            NormalizeEventKind(rawAction),
            objectId,
            capturedAtUtc);
    }

    internal static ObservedDockerObjectType NormalizeObjectType(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "container" => ObservedDockerObjectType.Container,
            "image" => ObservedDockerObjectType.Image,
            "volume" => ObservedDockerObjectType.Volume,
            "network" => ObservedDockerObjectType.Network,
            "daemon" => ObservedDockerObjectType.Daemon,
            _ => ObservedDockerObjectType.Unknown,
        };

    internal static ObservedDockerEventKind NormalizeEventKind(string value)
    {
        var normalized = value.Trim().ToLowerInvariant();
        if (normalized.StartsWith("health_status", StringComparison.Ordinal))
        {
            return ObservedDockerEventKind.HealthStatus;
        }

        if (normalized.StartsWith("exec_create", StringComparison.Ordinal))
        {
            return ObservedDockerEventKind.ExecCreated;
        }

        if (normalized.StartsWith("exec_start", StringComparison.Ordinal))
        {
            return ObservedDockerEventKind.ExecStarted;
        }

        if (normalized.StartsWith("exec_die", StringComparison.Ordinal))
        {
            return ObservedDockerEventKind.ExecDied;
        }

        return normalized switch
        {
            "create" => ObservedDockerEventKind.Create,
            "start" => ObservedDockerEventKind.Start,
            "stop" => ObservedDockerEventKind.Stop,
            "die" => ObservedDockerEventKind.Die,
            "destroy" => ObservedDockerEventKind.Destroy,
            "pause" => ObservedDockerEventKind.Pause,
            "unpause" => ObservedDockerEventKind.Unpause,
            "restart" => ObservedDockerEventKind.Restart,
            "rename" => ObservedDockerEventKind.Rename,
            "attach" => ObservedDockerEventKind.Attach,
            "detach" => ObservedDockerEventKind.Detach,
            "kill" => ObservedDockerEventKind.Kill,
            "oom" => ObservedDockerEventKind.Oom,
            "update" => ObservedDockerEventKind.Update,
            "connect" => ObservedDockerEventKind.Connect,
            "disconnect" => ObservedDockerEventKind.Disconnect,
            "pull" => ObservedDockerEventKind.Pull,
            "push" => ObservedDockerEventKind.Push,
            "tag" => ObservedDockerEventKind.Tag,
            "untag" => ObservedDockerEventKind.Untag,
            "delete" => ObservedDockerEventKind.Delete,
            "mount" => ObservedDockerEventKind.Mount,
            "unmount" => ObservedDockerEventKind.Unmount,
            "reload" => ObservedDockerEventKind.Reload,
            "prune" => ObservedDockerEventKind.Prune,
            _ => ObservedDockerEventKind.Unknown,
        };
    }

    private static string? ReadObjectId(JsonElement root)
    {
        string? value = null;
        if (TryGetProperty(root, "Actor", out var actor) && actor.ValueKind == JsonValueKind.Object)
        {
            value = GetString(actor, "ID");
        }

        value ??= GetString(root, "id") ?? GetString(root, "ID");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        value = value.Trim();
        if (value.Length > 256 || value.Any(char.IsControl))
        {
            throw new InvalidOperationException("Docker event object identifier is invalid.");
        }

        return value;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement root)
    {
        if (!TryGetProperty(root, "time", out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var seconds) ||
            seconds <= 0)
        {
            throw new InvalidOperationException("Docker event timestamp is invalid.");
        }

        return DateTimeOffset.FromUnixTimeSeconds(seconds);
    }

    private static string? GetString(JsonElement element, string name) =>
        TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
        {
        }
    }
}
