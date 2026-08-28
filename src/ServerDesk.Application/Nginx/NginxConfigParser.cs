using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ServerDesk.Application.Nginx;

public static class NginxConfigParser
{
    private static readonly Regex DirectivePattern = new(
        @"(?m)^\s*(listen|server_name|proxy_pass|ssl_certificate|ssl_certificate_key)\s+([^;\r\n]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AnsiPattern = new(
        "\\x1B\\[[0-?]*[ -/]*[@-~]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static (IReadOnlyList<NginxConfigSource> Sources, IReadOnlyList<NginxSiteInfo> Sites, string RawDump) Parse(
        string rawDump,
        NginxInventoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(rawDump);
        ArgumentNullException.ThrowIfNull(options);

        var sanitized = Sanitize(rawDump);
        if (Encoding.UTF8.GetByteCount(sanitized) > options.MaximumDumpBytes)
        {
            throw new FormatException($"nginx configuration dump exceeds the {options.MaximumDumpBytes} byte safety limit.");
        }

        var sections = SplitSources(sanitized, options.MaximumSources);
        var sites = new List<NginxSiteInfo>();
        var sources = new List<NginxConfigSource>(sections.Count);
        foreach (var section in sections)
        {
            var blocks = ExtractServerBlocks(section.Content).ToArray();
            sources.Add(new NginxConfigSource(section.Path, section.Content, blocks.Length));
            for (var index = 0; index < blocks.Length; index++)
            {
                if (sites.Count >= options.MaximumSites)
                {
                    throw new FormatException($"nginx configuration contains more than {options.MaximumSites} server blocks.");
                }

                sites.Add(ParseSite(section.Path, index, blocks[index]));
            }
        }

        return (sources, sites, sanitized);
    }

    public static string ParseVersion(string standardOutput, string standardError)
    {
        var text = Sanitize(string.Join("\n", standardOutput, standardError));
        var marker = "nginx version:";
        var index = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return text.Trim();
        }

        return text[(index + marker.Length)..].Split('\n')[0].Trim();
    }

    public static string Sanitize(string value) =>
        AnsiPattern.Replace(value.Replace("\0", string.Empty, StringComparison.Ordinal), string.Empty);

    private static IReadOnlyList<(string Path, string Content)> SplitSources(string raw, int maximumSources)
    {
        var sections = new List<(string Path, string Content)>();
        string? currentPath = null;
        var current = new StringBuilder();

        foreach (var line in raw.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            if (TryParseSourceMarker(line, out var path))
            {
                Flush();
                currentPath = path;
                continue;
            }

            if (currentPath is not null)
            {
                current.AppendLine(line);
            }
        }

        Flush();
        if (sections.Count == 0)
        {
            sections.Add(("(combined nginx output)", raw));
        }

        return sections;

        void Flush()
        {
            if (currentPath is null)
            {
                current.Clear();
                return;
            }

            if (sections.Count >= maximumSources)
            {
                throw new FormatException($"nginx configuration contains more than {maximumSources} source files.");
            }

            sections.Add((currentPath, current.ToString()));
            currentPath = null;
            current.Clear();
        }
    }

    private static bool TryParseSourceMarker(string line, out string path)
    {
        const string prefix = "# configuration file ";
        var trimmed = line.Trim();
        if (trimmed.StartsWith(prefix, StringComparison.Ordinal) && trimmed.EndsWith(':'))
        {
            path = trimmed[prefix.Length..^1].Trim();
            if (path.Length > 0 && path.Length <= 4096 && !path.Contains('\0'))
            {
                return true;
            }
        }

        path = string.Empty;
        return false;
    }

    private static IEnumerable<string> ExtractServerBlocks(string content)
    {
        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var capturing = false;
        var depth = 0;
        var block = new StringBuilder();

        foreach (var line in lines)
        {
            var code = RemoveComment(line);
            if (!capturing)
            {
                if (!Regex.IsMatch(code, @"^\s*server\s*\{", RegexOptions.CultureInvariant))
                {
                    continue;
                }

                capturing = true;
                depth = 0;
                block.Clear();
            }

            block.AppendLine(line);
            depth += CountBraces(code);
            if (capturing && depth <= 0)
            {
                capturing = false;
                yield return block.ToString();
            }
        }
    }

    private static NginxSiteInfo ParseSite(string sourcePath, int ordinal, string rawBlock)
    {
        var serverNames = new List<string>();
        var listens = new List<string>();
        var proxies = new List<string>();
        var certificates = new List<string>();
        var keys = new List<string>();

        foreach (Match match in DirectivePattern.Matches(rawBlock))
        {
            var directive = match.Groups[1].Value;
            var value = match.Groups[2].Value.Trim();
            switch (directive)
            {
                case "server_name":
                    serverNames.AddRange(value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "listen":
                    listens.Add(value);
                    break;
                case "proxy_pass":
                    proxies.Add(RedactProxyTarget(value));
                    break;
                case "ssl_certificate":
                    certificates.Add(value);
                    break;
                case "ssl_certificate_key":
                    keys.Add(value);
                    break;
            }
        }

        var identityMaterial = Encoding.UTF8.GetBytes($"{sourcePath}\n{ordinal}\n{rawBlock}");
        var id = "nginx:" + Convert.ToHexString(SHA256.HashData(identityMaterial))[..16];
        return new NginxSiteInfo(
            id,
            sourcePath,
            ordinal,
            Distinct(serverNames),
            Distinct(listens),
            Distinct(proxies),
            Distinct(certificates),
            Distinct(keys),
            rawBlock);
    }

    private static IReadOnlyList<string> Distinct(IEnumerable<string> values) =>
        values.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string RedactProxyTarget(string value)
    {
        var scheme = value.IndexOf("://", StringComparison.Ordinal);
        if (scheme < 0)
        {
            return value;
        }

        var authorityStart = scheme + 3;
        var at = value.IndexOf('@', authorityStart);
        if (at < 0)
        {
            return value;
        }

        var slash = value.IndexOf('/', authorityStart);
        if (slash >= 0 && at > slash)
        {
            return value;
        }

        return value[..authorityStart] + "***@" + value[(at + 1)..];
    }

    private static string RemoveComment(string line)
    {
        var quote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (character == quote && (index == 0 || line[index - 1] != '\\'))
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '#')
            {
                return line[..index];
            }
        }

        return line;
    }

    private static int CountBraces(string line)
    {
        var depth = 0;
        var quote = '\0';
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (character == quote && (index == 0 || line[index - 1] != '\\'))
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}')
            {
                depth--;
            }
        }

        return depth;
    }
}
