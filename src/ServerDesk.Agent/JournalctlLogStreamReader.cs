using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ServerDesk.Agent;

public enum ObservedJournalSeverity
{
    Unknown,
    Emergency,
    Alert,
    Critical,
    Error,
    Warning,
    Notice,
    Info,
    Debug,
}

public sealed record ObservedJournalLog(
    DateTimeOffset CapturedAtUtc,
    ObservedJournalSeverity Severity,
    string Message,
    string Identifier,
    string SystemdUnit,
    int? ProcessId,
    string Hostname);

public interface IAgentJournalLogReader
{
    IAsyncEnumerable<ObservedJournalLog> StreamAsync(CancellationToken cancellationToken = default);
}

public sealed partial class JournalctlLogStreamReader : IAgentJournalLogReader
{
    internal const int MaximumMessageLength = 4096;
    internal const int MaximumMetadataLength = 256;

    public async IAsyncEnumerable<ObservedJournalLog> StreamAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var process = new Process { StartInfo = BuildStartInfo() };
        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start journal observation.");
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
                throw new InvalidOperationException("Journal observation ended with a non-zero exit status.");
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
                // The process already completed during cleanup.
            }

            await errorDrain.ConfigureAwait(false);
        }
    }

    internal static ProcessStartInfo BuildStartInfo()
    {
        var info = new ProcessStartInfo("journalctl")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "--follow", "--output=json", "--utc", "--no-pager", "--all", "--lines=0" })
        {
            info.ArgumentList.Add(argument);
        }

        info.Environment["LC_ALL"] = "C";
        return info;
    }

    internal static ObservedJournalLog Parse(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);
        using var document = JsonDocument.Parse(line, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16,
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Journal payload is not an object.");
        }

        var root = document.RootElement;
        return new ObservedJournalLog(
            ParseTimestamp(Field(root, "__REALTIME_TIMESTAMP")),
            ParseSeverity(Field(root, "PRIORITY")),
            SanitizeField(Field(root, "MESSAGE"), MaximumMessageLength, redactSecrets: true),
            SanitizeField(Field(root, "SYSLOG_IDENTIFIER"), MaximumMetadataLength, redactSecrets: true),
            SanitizeField(Field(root, "_SYSTEMD_UNIT"), MaximumMetadataLength, redactSecrets: true),
            ParseProcessId(Field(root, "_PID")),
            SanitizeField(Field(root, "_HOSTNAME"), MaximumMetadataLength, redactSecrets: true));
    }

    internal static string SanitizeField(string? value, int maximumLength, bool redactSecrets)
    {
        if (maximumLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumLength));
        }

        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var sanitized = SanitizeControls(value);
        if (redactSecrets)
        {
            sanitized = RedactSecrets(sanitized);
        }

        return sanitized.Length <= maximumLength ? sanitized : sanitized[..maximumLength];
    }

    internal static string RedactSecrets(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
        {
            return "[REDACTED SENSITIVE LOG MESSAGE]";
        }

        var redacted = BearerTokenRegex().Replace(value, "$1[REDACTED]");
        redacted = UriUserInfoRegex().Replace(redacted, "$1[REDACTED]@");
        redacted = SensitiveAssignmentRegex().Replace(redacted, "$1=[REDACTED]");
        return redacted;
    }

    private static string SanitizeControls(string value)
    {
        StringBuilder? builder = null;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            var replacement = character switch
            {
                '\t' => '\t',
                '\r' or '\n' => ' ',
                _ when char.IsControl(character) => '\uFFFD',
                _ => character,
            };
            if (replacement == character && builder is null)
            {
                continue;
            }

            builder ??= new StringBuilder(value.Length).Append(value, 0, index);
            builder.Append(replacement);
        }

        return builder?.ToString() ?? value;
    }

    private static string Field(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private static DateTimeOffset ParseTimestamp(string raw)
    {
        if (!long.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var microseconds) || microseconds <= 0)
        {
            throw new InvalidOperationException("Journal timestamp is invalid.");
        }

        try
        {
            return DateTimeOffset.UnixEpoch.AddTicks(checked(microseconds * 10));
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or OverflowException)
        {
            throw new InvalidOperationException("Journal timestamp is invalid.", exception);
        }
    }

    private static int? ParseProcessId(string raw) =>
        int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var processId) && processId > 0
            ? processId
            : null;

    private static ObservedJournalSeverity ParseSeverity(string raw) => raw switch
    {
        "0" => ObservedJournalSeverity.Emergency,
        "1" => ObservedJournalSeverity.Alert,
        "2" => ObservedJournalSeverity.Critical,
        "3" => ObservedJournalSeverity.Error,
        "4" => ObservedJournalSeverity.Warning,
        "5" => ObservedJournalSeverity.Notice,
        "6" => ObservedJournalSeverity.Info,
        "7" => ObservedJournalSeverity.Debug,
        _ => ObservedJournalSeverity.Unknown,
    };

    private static async Task DrainAsync(StreamReader reader)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is not null)
        {
        }
    }

    [GeneratedRegex("(?i)\\b(Bearer\\s+)[A-Za-z0-9._~+/=-]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex("(?i)\\b(https?://[^/\\s:@]+:)[^@\\s/]+@", RegexOptions.CultureInvariant)]
    private static partial Regex UriUserInfoRegex();

    [GeneratedRegex("(?i)\\b(password|passwd|pwd|passphrase|secret|token|api[_-]?key|apikey|authorization)\\b\\s*[:=]\\s*(?:\"[^\"]*\"|'[^']*'|[^\\s,;]+)", RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveAssignmentRegex();
}
