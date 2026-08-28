using System.Text.RegularExpressions;

namespace ServerDesk.Application.EnvironmentFiles;

public sealed record EnvironmentFileParseResult(
    IReadOnlyList<EnvironmentFileLine> Lines,
    IReadOnlyList<EnvironmentFileEntry> Entries,
    bool HasUnsupportedLines,
    string NewLine);

public static class EnvironmentFileParser
{
    private static readonly Regex AssignmentPattern = new(
        @"^\s*(?<key>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static EnvironmentFileParseResult Parse(string text)
    {
        text ??= string.Empty;
        var tokens = EnvironmentFileText.Tokenize(text);
        var lines = new List<EnvironmentFileLine>(tokens.Count);
        var entries = new List<EnvironmentFileEntry>();
        var hasUnsupported = false;

        for (var index = 0; index < tokens.Count; index++)
        {
            var lineNumber = index + 1;
            var raw = tokens[index].Content;
            if (string.IsNullOrWhiteSpace(raw))
            {
                lines.Add(new EnvironmentFileLine(lineNumber, EnvironmentFileLineKind.Blank, raw));
                continue;
            }

            if (raw.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                lines.Add(new EnvironmentFileLine(lineNumber, EnvironmentFileLineKind.Comment, raw));
                continue;
            }

            var match = AssignmentPattern.Match(raw);
            if (!match.Success)
            {
                hasUnsupported = true;
                lines.Add(new EnvironmentFileLine(lineNumber, EnvironmentFileLineKind.Unsupported, raw));
                continue;
            }

            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value;
            var secret = EnvironmentSecretClassifier.IsSecret(key, value);
            lines.Add(new EnvironmentFileLine(
                lineNumber,
                EnvironmentFileLineKind.Assignment,
                raw,
                key,
                value,
                secret));
            entries.Add(new EnvironmentFileEntry(lineNumber, key, value, secret));
        }

        return new EnvironmentFileParseResult(
            lines,
            entries,
            hasUnsupported,
            EnvironmentFileText.DetectNewLine(tokens));
    }

    internal static bool TryParseAssignment(string raw, out string key)
    {
        var match = AssignmentPattern.Match(raw ?? string.Empty);
        key = match.Success ? match.Groups["key"].Value : string.Empty;
        return match.Success;
    }
}

public static class EnvironmentSecretClassifier
{
    public const string Mask = "••••••••";

    private static readonly string[] SecretNameFragments =
    [
        "SECRET",
        "PASSWORD",
        "PASSWD",
        "TOKEN",
        "APIKEY",
        "PRIVATEKEY",
        "CLIENTSECRET",
        "ACCESSKEY",
        "CONNECTIONSTRING",
        "DATABASEURL",
        "CREDENTIAL",
        "AUTHKEY",
    ];

    private static readonly Regex JwtPattern = new(
        @"^[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CredentialUriPattern = new(
        @"://[^/\s:@]+:[^@\s]+@",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsSecret(string key, string value)
    {
        var normalizedKey = new string((key ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToUpperInvariant)
            .ToArray());
        if (SecretNameFragments.Any(normalizedKey.Contains))
        {
            return true;
        }

        var normalizedValue = Unquote((value ?? string.Empty).Trim());
        if (normalizedValue.Contains("PRIVATE KEY-----", StringComparison.OrdinalIgnoreCase) ||
            JwtPattern.IsMatch(normalizedValue) ||
            CredentialUriPattern.IsMatch(normalizedValue) ||
            normalizedValue.StartsWith("ghp_", StringComparison.Ordinal) ||
            normalizedValue.StartsWith("github_pat_", StringComparison.Ordinal) ||
            normalizedValue.StartsWith("glpat-", StringComparison.Ordinal) ||
            normalizedValue.StartsWith("xoxb-", StringComparison.Ordinal) ||
            normalizedValue.StartsWith("xoxp-", StringComparison.Ordinal) ||
            normalizedValue.StartsWith("sk-", StringComparison.Ordinal) ||
            normalizedValue.StartsWith("AKIA", StringComparison.Ordinal) ||
            normalizedValue.Contains("Password=", StringComparison.OrdinalIgnoreCase) ||
            normalizedValue.Contains("Pwd=", StringComparison.OrdinalIgnoreCase) ||
            normalizedValue.Contains("AccountKey=", StringComparison.OrdinalIgnoreCase) ||
            normalizedValue.Contains("SharedAccessSignature=", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static string DisplayValue(EnvironmentFileEntry entry, bool revealed) =>
        entry.IsSecret && !revealed ? Mask : entry.Value;

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}

public static class EnvironmentFileEditor
{
    private static readonly Regex KeyPattern = new(
        @"^[A-Za-z_][A-Za-z0-9_]*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string SetValueAtLine(string text, int lineNumber, string expectedKey, string value)
    {
        ValidateLineNumber(lineNumber);
        ValidateKey(expectedKey);
        ValidateSimpleValue(value);
        var tokens = EnvironmentFileText.Tokenize(text ?? string.Empty).ToList();
        if (lineNumber > tokens.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber));
        }

        var target = tokens[lineNumber - 1];
        if (!EnvironmentFileParser.TryParseAssignment(target.Content, out var actualKey) ||
            !string.Equals(actualKey, expectedKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The selected line is no longer the expected supported environment assignment.");
        }

        var equals = target.Content.IndexOf('=');
        var valuePart = target.Content[(equals + 1)..];
        var spacingLength = 0;
        while (spacingLength < valuePart.Length && valuePart[spacingLength] is ' ' or '\t')
        {
            spacingLength++;
        }

        var spacing = valuePart[..spacingLength];
        tokens[lineNumber - 1] = target with
        {
            Content = target.Content[..(equals + 1)] + spacing + value,
        };
        return EnvironmentFileText.Join(tokens);
    }

    public static string DeleteAssignmentAtLine(string text, int lineNumber, string expectedKey)
    {
        ValidateLineNumber(lineNumber);
        ValidateKey(expectedKey);
        var tokens = EnvironmentFileText.Tokenize(text ?? string.Empty).ToList();
        if (lineNumber > tokens.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber));
        }

        if (!EnvironmentFileParser.TryParseAssignment(tokens[lineNumber - 1].Content, out var actualKey) ||
            !string.Equals(actualKey, expectedKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The selected line is no longer the expected supported environment assignment.");
        }

        tokens.RemoveAt(lineNumber - 1);
        return EnvironmentFileText.Join(tokens);
    }

    public static string AddAssignment(string text, string key, string value)
    {
        ValidateKey(key);
        ValidateSimpleValue(value);
        text ??= string.Empty;
        if (text.Length == 0)
        {
            return key + "=" + value;
        }

        var parsed = EnvironmentFileParser.Parse(text);
        var separator = text.EndsWith("\n", StringComparison.Ordinal) || text.EndsWith("\r", StringComparison.Ordinal)
            ? string.Empty
            : parsed.NewLine;
        return text + separator + key + "=" + value;
    }

    public static void ValidateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        if (!KeyPattern.IsMatch(key))
        {
            throw new ArgumentException("Environment keys must use letters, digits and underscores and cannot start with a digit.", nameof(key));
        }
    }

    public static void ValidateSimpleValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.IndexOfAny(['\r', '\n', '\0']) >= 0)
        {
            throw new ArgumentException("Simple environment values must remain on one line. Use the raw advanced editor for multiline syntax.", nameof(value));
        }
    }

    private static void ValidateLineNumber(int lineNumber)
    {
        if (lineNumber < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(lineNumber));
        }
    }
}

internal sealed record EnvironmentFileTextLine(string Content, string Terminator);

internal static class EnvironmentFileText
{
    public static IReadOnlyList<EnvironmentFileTextLine> Tokenize(string text)
    {
        text ??= string.Empty;
        var result = new List<EnvironmentFileTextLine>();
        var index = 0;
        while (index < text.Length)
        {
            var start = index;
            while (index < text.Length && text[index] is not ('\r' or '\n'))
            {
                index++;
            }

            var content = text[start..index];
            var terminator = string.Empty;
            if (index < text.Length)
            {
                if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
                {
                    terminator = "\r\n";
                    index += 2;
                }
                else
                {
                    terminator = text[index].ToString();
                    index++;
                }
            }

            result.Add(new EnvironmentFileTextLine(content, terminator));
        }

        if (result.Count == 0)
        {
            result.Add(new EnvironmentFileTextLine(string.Empty, string.Empty));
        }

        return result;
    }

    public static string DetectNewLine(IReadOnlyList<EnvironmentFileTextLine> lines) =>
        lines.Select(line => line.Terminator).FirstOrDefault(value => value.Length > 0) ?? "\n";

    public static string Join(IEnumerable<EnvironmentFileTextLine> lines) =>
        string.Concat(lines.Select(line => line.Content + line.Terminator));
}
