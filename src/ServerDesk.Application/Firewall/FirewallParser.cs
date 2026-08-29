using System.Text;
using System.Text.RegularExpressions;

namespace ServerDesk.Application.Firewall;

public static partial class FirewallParser
{
    private static readonly Regex UfwRulePattern = new(
        @"^\[\s*(?<index>\d+)\]\s+(?<target>.+?)\s{2,}(?<action>ALLOW|DENY|REJECT|LIMIT)\s+(?<direction>IN|OUT)\s+(?<source>.+)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex ZoneHeaderPattern = new(
        @"^(?<zone>[A-Za-z0-9_.-]+)(?:\s+\(active\))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static FirewallAdapterObservation ParseUfw(
        string versionOutput,
        string statusOutput,
        FirewallInventoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var raw = ValidateAndSanitize(statusOutput, options);
        var lines = SplitLines(raw);
        var statusLine = lines.FirstOrDefault(line => line.StartsWith("Status:", StringComparison.OrdinalIgnoreCase));
        if (statusLine is null)
        {
            throw new FormatException("UFW status output did not contain a status line.");
        }

        var statusValue = statusLine[(statusLine.IndexOf(':') + 1)..].Trim();
        var active = string.Equals(statusValue, "active", StringComparison.OrdinalIgnoreCase);
        var inactive = string.Equals(statusValue, "inactive", StringComparison.OrdinalIgnoreCase);
        if (!active && !inactive)
        {
            throw new FormatException("UFW returned an unrecognized runtime state.");
        }

        var rules = new List<FirewallRuleInfo>();
        foreach (var line in lines)
        {
            var match = UfwRulePattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            if (rules.Count >= options.MaximumRules)
            {
                throw new FormatException("UFW rule count exceeded the configured safety bound.");
            }

            var target = match.Groups["target"].Value.Trim();
            var protocol = string.Empty;
            var port = target;
            var slash = target.LastIndexOf('/');
            if (slash > 0 && slash < target.Length - 1)
            {
                var candidate = target[(slash + 1)..].Trim();
                if (candidate.Equals("tcp", StringComparison.OrdinalIgnoreCase) ||
                    candidate.Equals("udp", StringComparison.OrdinalIgnoreCase))
                {
                    protocol = candidate.ToLowerInvariant();
                    port = target[..slash].Trim();
                }
            }

            rules.Add(new FirewallRuleInfo(
                $"ufw:{match.Groups["index"].Value}",
                FirewallAdapterKind.Ufw,
                null,
                ParseAction(match.Groups["action"].Value),
                ParseDirection(match.Groups["direction"].Value),
                protocol,
                port,
                match.Groups["source"].Value.Trim(),
                "host",
                line.Trim()));
        }

        return new FirewallAdapterObservation(
            FirewallAdapterKind.Ufw,
            CliAvailable: true,
            IsActive: active,
            PermissionDenied: false,
            ParseVersion(versionOutput, "ufw"),
            active ? "ufw-active" : "ufw-inactive",
            rules,
            raw);
    }

    public static FirewallAdapterObservation ParseFirewalld(
        string versionOutput,
        string stateOutput,
        string zonesOutput,
        FirewallInventoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        var state = ValidateAndSanitize(stateOutput, options).Trim();
        var active = string.Equals(state, "running", StringComparison.OrdinalIgnoreCase);
        if (!active && !string.Equals(state, "not running", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("firewalld returned an unrecognized runtime state.");
        }

        var raw = ValidateAndSanitize(zonesOutput, options);
        var rules = active ? ParseFirewalldZones(raw, options) : [];
        return new FirewallAdapterObservation(
            FirewallAdapterKind.Firewalld,
            CliAvailable: true,
            IsActive: active,
            PermissionDenied: false,
            ParseVersion(versionOutput, "firewalld"),
            active ? "firewalld-running" : "firewalld-not-running",
            rules,
            raw);
    }

    public static string Sanitize(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            if (character is '\r' or '\n' or '\t' || !char.IsControl(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }

    private static IReadOnlyList<FirewallRuleInfo> ParseFirewalldZones(
        string raw,
        FirewallInventoryOptions options)
    {
        var rules = new List<FirewallRuleInfo>();
        string? zone = null;
        var activeZone = false;
        var services = new List<string>();
        var ports = new List<string>();
        var sources = new List<string>();

        void Flush()
        {
            if (zone is null)
            {
                return;
            }

            var source = sources.Count == 0 ? "any" : string.Join(", ", sources);
            foreach (var service in services)
            {
                AddRule(service, string.Empty, "service");
            }

            foreach (var entry in ports)
            {
                var slash = entry.LastIndexOf('/');
                var port = slash > 0 ? entry[..slash] : entry;
                var protocol = slash > 0 && slash < entry.Length - 1 ? entry[(slash + 1)..] : string.Empty;
                AddRule(port, protocol, "port");
            }

            void AddRule(string portOrService, string protocol, string kind)
            {
                if (rules.Count >= options.MaximumRules)
                {
                    throw new FormatException("firewalld rule count exceeded the configured safety bound.");
                }

                rules.Add(new FirewallRuleInfo(
                    $"firewalld:{zone}:{kind}:{portOrService}:{protocol}",
                    FirewallAdapterKind.Firewalld,
                    zone,
                    FirewallRuleAction.Allow,
                    FirewallRuleDirection.Inbound,
                    protocol,
                    portOrService,
                    source,
                    "zone",
                    $"{zone}: {kind} {portOrService}{(string.IsNullOrEmpty(protocol) ? string.Empty : "/" + protocol)}{(activeZone ? " (active)" : string.Empty)}"));
            }
        }

        foreach (var line in SplitLines(raw))
        {
            if (line.Length == 0)
            {
                continue;
            }

            if (!char.IsWhiteSpace(line[0]))
            {
                var match = ZoneHeaderPattern.Match(line.Trim());
                if (!match.Success)
                {
                    continue;
                }

                Flush();
                zone = match.Groups["zone"].Value;
                activeZone = line.Contains("(active)", StringComparison.OrdinalIgnoreCase);
                services.Clear();
                ports.Clear();
                sources.Clear();
                continue;
            }

            if (zone is null)
            {
                continue;
            }

            var trimmed = line.Trim();
            AddValues(trimmed, "services:", services);
            AddValues(trimmed, "ports:", ports);
            AddValues(trimmed, "sources:", sources);
        }

        Flush();
        return rules;
    }

    private static void AddValues(string line, string prefix, ICollection<string> target)
    {
        if (!line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        foreach (var value in line[prefix.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            target.Add(value);
        }
    }

    private static string ValidateAndSanitize(string value, FirewallInventoryOptions options)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Encoding.UTF8.GetByteCount(value) > options.MaximumOutputBytes)
        {
            throw new FormatException("Firewall command output exceeded the configured safety bound.");
        }

        return Sanitize(value);
    }

    private static string ParseVersion(string value, string fallback)
    {
        var sanitized = Sanitize(value).Trim();
        if (sanitized.Length == 0)
        {
            return fallback;
        }

        var line = SplitLines(sanitized).FirstOrDefault()?.Trim();
        return string.IsNullOrWhiteSpace(line) ? fallback : line;
    }

    private static IReadOnlyList<string> SplitLines(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.None);

    private static FirewallRuleAction ParseAction(string value) =>
        value.ToUpperInvariant() switch
        {
            "ALLOW" => FirewallRuleAction.Allow,
            "DENY" => FirewallRuleAction.Deny,
            "REJECT" => FirewallRuleAction.Reject,
            "LIMIT" => FirewallRuleAction.Limit,
            _ => FirewallRuleAction.Unknown,
        };

    private static FirewallRuleDirection ParseDirection(string value) =>
        value.ToUpperInvariant() switch
        {
            "IN" => FirewallRuleDirection.Inbound,
            "OUT" => FirewallRuleDirection.Outbound,
            _ => FirewallRuleDirection.Unknown,
        };
}
