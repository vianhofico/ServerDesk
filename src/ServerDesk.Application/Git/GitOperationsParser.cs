using System.Globalization;
using System.Text;
using ServerDesk.Domain.Errors;

namespace ServerDesk.Application.Git;

public static class GitOperationsParser
{
    public static GitRepositorySnapshot ParseStatus(
        string requestedPath,
        string repositoryRoot,
        string statusOutput,
        string unstagedDiffSummary,
        string stagedDiffSummary,
        string remotesOutput,
        int maximumChanges,
        int maximumRemotes)
    {
        if (maximumChanges < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumChanges));
        }

        var branch = string.Empty;
        var revision = string.Empty;
        string? upstream = null;
        var ahead = 0;
        var behind = 0;
        var changes = new List<GitChange>();
        var pendingRenameOriginal = false;

        foreach (var rawRecord in (statusOutput ?? string.Empty).Split('\0', StringSplitOptions.RemoveEmptyEntries))
        {
            var record = rawRecord.TrimEnd('\r', '\n');
            if (record.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                revision = Sanitize(record[13..].Trim());
                continue;
            }

            if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                branch = Sanitize(record[14..].Trim());
                continue;
            }

            if (record.StartsWith("# branch.upstream ", StringComparison.Ordinal))
            {
                upstream = Sanitize(record[18..].Trim());
                continue;
            }

            if (record.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                ParseAheadBehind(record[12..], out ahead, out behind);
                continue;
            }

            if (pendingRenameOriginal)
            {
                if (changes.Count > 0)
                {
                    changes[^1] = changes[^1] with { OriginalPath = Sanitize(record) };
                }

                pendingRenameOriginal = false;
                continue;
            }

            if (changes.Count >= maximumChanges)
            {
                continue;
            }

            if (record.StartsWith("? ", StringComparison.Ordinal))
            {
                changes.Add(new GitChange('?', '?', Sanitize(record[2..])));
                continue;
            }

            if (record.StartsWith("! ", StringComparison.Ordinal))
            {
                continue;
            }

            if (record.StartsWith("1 ", StringComparison.Ordinal))
            {
                var xy = ReadField(record, 1);
                var path = TailAfterSpaces(record, 8);
                changes.Add(CreateChange(xy, path));
                continue;
            }

            if (record.StartsWith("2 ", StringComparison.Ordinal))
            {
                var xy = ReadField(record, 1);
                var path = TailAfterSpaces(record, 9);
                changes.Add(CreateChange(xy, path));
                pendingRenameOriginal = true;
                continue;
            }

            if (record.StartsWith("u ", StringComparison.Ordinal))
            {
                var xy = ReadField(record, 1);
                var path = TailAfterSpaces(record, 10);
                changes.Add(CreateChange(xy, path));
            }
        }

        if (revision.Length == 0)
        {
            throw new FormatException("Git porcelain v2 output did not include branch.oid.");
        }

        if (branch.Length == 0)
        {
            branch = "(unknown)";
        }

        var detached = string.Equals(branch, "(detached)", StringComparison.Ordinal) ||
                       string.Equals(branch, "(unknown)", StringComparison.Ordinal);
        var remotes = ParseRemotes(remotesOutput, maximumRemotes);
        return new GitRepositorySnapshot(
            GitRepositoryPath.Normalize(requestedPath),
            GitRepositoryPath.Normalize(repositoryRoot),
            branch,
            revision,
            string.IsNullOrWhiteSpace(upstream) ? null : upstream,
            detached,
            ahead,
            behind,
            changes,
            remotes,
            SanitizeSummary(unstagedDiffSummary),
            SanitizeSummary(stagedDiffSummary));
    }

    public static IReadOnlyList<GitRemoteInfo> ParseRemotes(string output, int maximumRemotes)
    {
        if (maximumRemotes < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumRemotes));
        }

        var remotes = new List<GitRemoteInfo>();
        foreach (var rawLine in NormalizeLines(output))
        {
            var separator = rawLine.IndexOfAny([' ', '\t']);
            if (separator <= 0 || separator >= rawLine.Length - 1)
            {
                continue;
            }

            var key = rawLine[..separator].Trim();
            var value = rawLine[(separator + 1)..].Trim();
            const string prefix = "remote.";
            const string suffix = ".url";
            if (!key.StartsWith(prefix, StringComparison.Ordinal) ||
                !key.EndsWith(suffix, StringComparison.Ordinal) ||
                key.Length <= prefix.Length + suffix.Length)
            {
                continue;
            }

            var name = key[prefix.Length..^suffix.Length];
            if (name.Length == 0 || remotes.Any(remote => string.Equals(remote.Name, name, StringComparison.Ordinal)))
            {
                continue;
            }

            remotes.Add(new GitRemoteInfo(Sanitize(name), RedactRemoteUrl(value)));
            if (remotes.Count >= maximumRemotes)
            {
                break;
            }
        }

        return remotes;
    }

    public static IReadOnlyList<string> ParseIncomingCommits(string output, int maximumCommits)
    {
        if (maximumCommits < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumCommits));
        }

        return NormalizeLines(output)
            .Select(line => Sanitize(line.Trim()))
            .Where(line => line.Length > 0)
            .Take(maximumCommits)
            .ToArray();
    }

    public static IReadOnlyList<string> ParseDiscovery(string output, int maximumResults)
    {
        if (maximumResults < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumResults));
        }

        var paths = new List<string>();
        foreach (var rawLine in NormalizeLines(output))
        {
            var candidate = rawLine.Trim();
            if (candidate.Length <= 5 || !candidate.EndsWith("/.git", StringComparison.Ordinal))
            {
                continue;
            }

            var repositoryPath = candidate[..^5];
            try
            {
                var normalized = GitRepositoryPath.Normalize(repositoryPath.Length == 0 ? "/" : repositoryPath);
                if (!paths.Contains(normalized, StringComparer.Ordinal))
                {
                    paths.Add(normalized);
                }
            }
            catch (FormatException)
            {
                // Remote discovery output is untrusted; malformed paths are skipped rather than reused as arguments.
            }

            if (paths.Count >= maximumResults)
            {
                break;
            }
        }

        return paths;
    }

    public static RemoteError MapFailure(string detail)
    {
        var text = string.IsNullOrWhiteSpace(detail) ? "Git command failed." : Sanitize(detail.Trim());
        var code = text.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("does not exist", StringComparison.OrdinalIgnoreCase)
            ? RemoteErrorCode.PathNotFound
            : text.Contains("dubious ownership", StringComparison.OrdinalIgnoreCase) ||
              text.Contains("permission denied", StringComparison.OrdinalIgnoreCase)
                ? RemoteErrorCode.PermissionDenied
                : text.Contains("authentication failed", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("could not read username", StringComparison.OrdinalIgnoreCase) ||
                  text.Contains("publickey", StringComparison.OrdinalIgnoreCase)
                    ? RemoteErrorCode.AuthenticationFailed
                    : text.Contains("could not resolve host", StringComparison.OrdinalIgnoreCase) ||
                      text.Contains("network is unreachable", StringComparison.OrdinalIgnoreCase) ||
                      text.Contains("connection timed out", StringComparison.OrdinalIgnoreCase)
                        ? RemoteErrorCode.NetworkInterrupted
                        : text.Contains("not possible to fast-forward", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("diverging branches", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("would be overwritten", StringComparison.OrdinalIgnoreCase)
                            ? RemoteErrorCode.PathConflict
                            : text.Contains("unknown option", StringComparison.OrdinalIgnoreCase) ||
                              text.Contains("unsupported", StringComparison.OrdinalIgnoreCase)
                                ? RemoteErrorCode.UnsupportedVersion
                                : text.Contains("git: command not found", StringComparison.OrdinalIgnoreCase) ||
                                  text.Contains("git: not found", StringComparison.OrdinalIgnoreCase)
                                    ? RemoteErrorCode.CommandNotFound
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

    private static GitChange CreateChange(string xy, string path)
    {
        if (xy.Length != 2)
        {
            throw new FormatException("Git porcelain v2 change status did not contain a two-character XY field.");
        }

        return new GitChange(xy[0], xy[1], Sanitize(path));
    }

    private static void ParseAheadBehind(string value, out int ahead, out int behind)
    {
        ahead = 0;
        behind = 0;
        foreach (var token in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length < 2 || !int.TryParse(token[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var count))
            {
                continue;
            }

            if (token[0] == '+')
            {
                ahead = count;
            }
            else if (token[0] == '-')
            {
                behind = count;
            }
        }
    }

    private static string ReadField(string value, int fieldIndex)
    {
        var start = 0;
        for (var index = 0; index <= fieldIndex; index++)
        {
            var end = value.IndexOf(' ', start);
            if (index == fieldIndex)
            {
                return end < 0 ? value[start..] : value[start..end];
            }

            if (end < 0)
            {
                throw new FormatException("Git porcelain v2 row is missing required fields.");
            }

            start = end + 1;
        }

        throw new FormatException("Git porcelain v2 row is missing required fields.");
    }

    private static string TailAfterSpaces(string value, int spaces)
    {
        var position = -1;
        for (var count = 0; count < spaces; count++)
        {
            position = value.IndexOf(' ', position + 1);
            if (position < 0)
            {
                throw new FormatException("Git porcelain v2 row is missing its path field.");
            }
        }

        return value[(position + 1)..];
    }

    private static string RedactRemoteUrl(string value)
    {
        var sanitized = Sanitize(value.Trim());
        if (!Uri.TryCreate(sanitized, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Scheme))
        {
            return sanitized;
        }

        try
        {
            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty,
            };
            return builder.Uri.AbsoluteUri;
        }
        catch (UriFormatException)
        {
            return "<redacted-invalid-remote>";
        }
    }

    private static string SanitizeSummary(string value)
    {
        var line = NormalizeLines(value).FirstOrDefault()?.Trim() ?? string.Empty;
        return line.Length == 0 ? "—" : Sanitize(line);
    }

    private static IEnumerable<string> NormalizeLines(string? value) =>
        (value ?? string.Empty)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
}
