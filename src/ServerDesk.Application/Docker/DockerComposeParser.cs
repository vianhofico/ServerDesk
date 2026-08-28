using System.Text;
using System.Text.Json;
using ServerDesk.Domain.Errors;

namespace ServerDesk.Application.Docker;

public static class DockerComposeParser
{
    public static IReadOnlyList<DockerComposeProject> ParseProjects(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<DockerComposeProject>();
        }

        using var document = JsonDocument.Parse(output);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException("Docker Compose project output must be a JSON array.");
        }

        var projects = new List<DockerComposeProject>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            var name = ReadString(element, "Name", "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var status = ReadString(element, "Status", "status") ?? string.Empty;
            var configFiles = ReadConfigFiles(element);
            if (configFiles.Count == 0)
            {
                continue;
            }

            projects.Add(DockerComposeIdentity.Normalize(new DockerComposeProject(
                name,
                Sanitize(status),
                configFiles)));
        }

        return projects
            .OrderBy(project => project.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static IReadOnlyList<DockerComposeServiceInfo> ParseServices(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return Array.Empty<DockerComposeServiceInfo>();
        }

        var elements = ParseArrayOrJsonLines(output);
        var services = new List<DockerComposeServiceInfo>();
        foreach (var element in elements)
        {
            var name = ReadString(element, "Name", "name") ?? string.Empty;
            var service = ReadString(element, "Service", "service") ?? string.Empty;
            if (name.Length == 0 && service.Length == 0)
            {
                continue;
            }

            services.Add(new DockerComposeServiceInfo(
                Sanitize(ReadString(element, "ID", "Id", "id") ?? string.Empty),
                Sanitize(name),
                Sanitize(service),
                Sanitize(ReadString(element, "Image", "image") ?? string.Empty),
                Sanitize(ReadString(element, "State", "state") ?? string.Empty),
                Sanitize(ReadString(element, "Status", "status") ?? string.Empty),
                Sanitize(ReadPublishers(element))));
        }

        return services
            .OrderBy(service => service.Service, StringComparer.OrdinalIgnoreCase)
            .ThenBy(service => service.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static string NormalizeConfigJson(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return "{}";
        }

        using var document = JsonDocument.Parse(output);
        return JsonSerializer.Serialize(document.RootElement, new JsonSerializerOptions { WriteIndented = true });
    }

    public static IReadOnlyList<string> ParseLogLines(string output, int maximumRows)
    {
        if (maximumRows < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRows));
        }

        return (output ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(Sanitize)
            .Where(line => line.Length > 0)
            .TakeLast(maximumRows)
            .ToArray();
    }

    public static RemoteError MapFailure(string detail, RemoteError? executionError = null)
    {
        if (executionError is not null)
        {
            return executionError;
        }

        var text = string.IsNullOrWhiteSpace(detail) ? "Docker Compose command failed." : Sanitize(detail.Trim());
        var code = text.Contains("permission denied", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("operation not permitted", StringComparison.OrdinalIgnoreCase)
            ? RemoteErrorCode.PermissionDenied
            : text.Contains("cannot connect to the docker daemon", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("is the docker daemon running", StringComparison.OrdinalIgnoreCase)
                ? RemoteErrorCode.CapabilityUnavailable
                : text.Contains("no configuration file", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("no such file", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("not found", StringComparison.OrdinalIgnoreCase)
                    ? RemoteErrorCode.PathNotFound
                    : text.Contains("unsupported", StringComparison.OrdinalIgnoreCase) ||
                      text.Contains("requires", StringComparison.OrdinalIgnoreCase) && text.Contains("version", StringComparison.OrdinalIgnoreCase)
                        ? RemoteErrorCode.UnsupportedVersion
                        : RemoteErrorCode.CommandFailed;
        return new RemoteError(code, text);
    }

    public static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\t' or >= ' ' and not '\u007f')
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

    private static IReadOnlyList<JsonElement> ParseArrayOrJsonLines(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.StartsWith("[", StringComparison.Ordinal))
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new FormatException("Docker Compose service output must be a JSON array or JSON lines.");
            }

            return document.RootElement.EnumerateArray().Select(element => element.Clone()).ToArray();
        }

        var rows = new List<JsonElement>();
        foreach (var line in trimmed.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException("Docker Compose service JSON-line row must be an object.");
            }

            rows.Add(document.RootElement.Clone());
        }

        return rows;
    }

    private static IReadOnlyList<string> ReadConfigFiles(JsonElement element)
    {
        foreach (var name in new[] { "ConfigFiles", "configFiles", "configfiles" })
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.Array)
            {
                return property.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString() ?? string.Empty)
                    .Where(path => path.Length > 0)
                    .ToArray();
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                return (property.GetString() ?? string.Empty)
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToArray();
            }
        }

        return Array.Empty<string>();
    }

    private static string ReadPublishers(JsonElement element)
    {
        if (!element.TryGetProperty("Publishers", out var property) &&
            !element.TryGetProperty("publishers", out property))
        {
            return string.Empty;
        }

        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? string.Empty;
        }

        if (property.ValueKind != JsonValueKind.Array)
        {
            return property.ToString();
        }

        return string.Join(", ", property.EnumerateArray().Select(publisher =>
        {
            var url = ReadString(publisher, "URL", "Url", "url") ?? string.Empty;
            var targetPort = ReadStringOrNumber(publisher, "TargetPort", "targetPort") ?? string.Empty;
            var publishedPort = ReadStringOrNumber(publisher, "PublishedPort", "publishedPort") ?? string.Empty;
            var protocol = ReadString(publisher, "Protocol", "protocol") ?? string.Empty;
            return publishedPort.Length > 0
                ? $"{url}:{publishedPort}->{targetPort}/{protocol}".Trim(':')
                : $"{targetPort}/{protocol}".Trim('/');
        }).Where(value => value.Length > 0));
    }

    private static string? ReadString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static string? ReadStringOrNumber(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var property))
            {
                continue;
            }

            if (property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }

            if (property.ValueKind == JsonValueKind.Number)
            {
                return property.GetRawText();
            }
        }

        return null;
    }
}
