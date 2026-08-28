using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ServerDesk.Application.Tls;

public sealed record ParsedOpenSslCertificate(
    string? Subject,
    IReadOnlyList<string> SubjectAlternativeNames,
    string? Issuer,
    DateTimeOffset NotBeforeUtc,
    DateTimeOffset NotAfterUtc,
    string? FingerprintSha256);

public static class OpenSslCertificateParser
{
    private static readonly Regex DnsPattern = new(
        @"(?:^|[,\s])DNS:(?<name>[^,\s]+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static ParsedOpenSslCertificate Parse(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        string? subject = null;
        string? issuer = null;
        string? fingerprint = null;
        DateTimeOffset? notBefore = null;
        DateTimeOffset? notAfter = null;
        var sans = new List<string>();

        foreach (var rawLine in Normalize(output).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith("subject=", StringComparison.OrdinalIgnoreCase))
            {
                subject = line["subject=".Length..].Trim();
            }
            else if (line.StartsWith("issuer=", StringComparison.OrdinalIgnoreCase))
            {
                issuer = line["issuer=".Length..].Trim();
            }
            else if (line.StartsWith("notBefore=", StringComparison.OrdinalIgnoreCase))
            {
                notBefore = ParseOpenSslDate(line["notBefore=".Length..]);
            }
            else if (line.StartsWith("notAfter=", StringComparison.OrdinalIgnoreCase))
            {
                notAfter = ParseOpenSslDate(line["notAfter=".Length..]);
            }
            else if (line.Contains("Fingerprint=", StringComparison.OrdinalIgnoreCase))
            {
                var separator = line.IndexOf('=');
                fingerprint = separator >= 0 ? line[(separator + 1)..].Trim() : null;
            }

            foreach (Match match in DnsPattern.Matches(line))
            {
                var name = match.Groups["name"].Value.Trim();
                if (name.Length > 0 && !sans.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    sans.Add(name);
                }
            }
        }

        if (notBefore is null || notAfter is null)
        {
            throw new FormatException("OpenSSL output did not contain both notBefore and notAfter dates.");
        }

        if (notAfter <= notBefore)
        {
            throw new FormatException("OpenSSL certificate validity interval is invalid.");
        }

        return new ParsedOpenSslCertificate(
            subject,
            sans,
            issuer,
            notBefore.Value,
            notAfter.Value,
            fingerprint);
    }

    private static DateTimeOffset ParseOpenSslDate(string value)
    {
        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        if (DateTimeOffset.TryParse(
                normalized,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        throw new FormatException($"OpenSSL certificate date '{value.Trim()}' is not recognized.");
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\0", string.Empty, StringComparison.Ordinal);
}

public static class CertbotOutputParser
{
    private static readonly Regex NginxPluginPattern = new(
        @"(?im)^\s*(?:\*\s*)?nginx(?:\s|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string ParseVersion(string output, string error)
    {
        var text = string.Join('\n', output, error).Trim();
        const string prefix = "certbot ";
        var index = text.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            return text[(index + prefix.Length)..].Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        }

        return text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
    }

    public static bool HasNginxPlugin(string output) => NginxPluginPattern.IsMatch(output ?? string.Empty);

    public static IReadOnlyList<CertbotManagedCertificate> ParseCertificates(string output, int maximumCertificates)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (maximumCertificates < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCertificates));
        }

        var result = new List<CertbotManagedCertificate>();
        string? name = null;
        IReadOnlyList<string> domains = [];
        string? certificatePath = null;
        string? privateKeyPath = null;

        foreach (var raw in Normalize(output).Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Certificate Name:", StringComparison.OrdinalIgnoreCase))
            {
                Flush();
                name = ValueAfterColon(line);
            }
            else if (line.StartsWith("Domains:", StringComparison.OrdinalIgnoreCase))
            {
                domains = ValueAfterColon(line)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
            else if (line.StartsWith("Certificate Path:", StringComparison.OrdinalIgnoreCase))
            {
                certificatePath = ValueAfterColon(line);
            }
            else if (line.StartsWith("Private Key Path:", StringComparison.OrdinalIgnoreCase))
            {
                privateKeyPath = ValueAfterColon(line);
            }
        }

        Flush();
        return result;

        void Flush()
        {
            if (name is null)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(certificatePath) &&
                certificatePath.StartsWith('/', StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(privateKeyPath) &&
                privateKeyPath.StartsWith('/', StringComparison.Ordinal))
            {
                if (result.Count >= maximumCertificates)
                {
                    throw new FormatException($"Certbot reports more than {maximumCertificates} managed certificates.");
                }

                result.Add(new CertbotManagedCertificate(
                    name.Trim(),
                    domains,
                    certificatePath.Trim(),
                    privateKeyPath.Trim()));
            }

            name = null;
            domains = [];
            certificatePath = null;
            privateKeyPath = null;
        }
    }

    private static string ValueAfterColon(string line)
    {
        var colon = line.IndexOf(':');
        return colon < 0 ? string.Empty : line[(colon + 1)..].Trim();
    }

    private static string Normalize(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\0", string.Empty, StringComparison.Ordinal);
}
