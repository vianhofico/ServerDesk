using System.Text.RegularExpressions;

namespace ServerDesk.Application.Nginx;

public sealed record NginxSimpleSitePatchResult(
    bool IsSuccess,
    string CandidateText,
    string Message);

public static class NginxSimpleSiteEditor
{
    private static readonly Regex ServerStartPattern = new(
        @"(?m)^[ \t]*server[ \t]*\{",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ServerNamePattern = DirectivePattern("server_name");
    private static readonly Regex ListenPattern = DirectivePattern("listen");
    private static readonly Regex ProxyPassPattern = DirectivePattern("proxy_pass");

    public static NginxSimpleSitePatchResult Apply(
        string sourceText,
        int sourceOrdinal,
        NginxSimpleSitePatch patch)
    {
        ArgumentNullException.ThrowIfNull(sourceText);
        ArgumentNullException.ThrowIfNull(patch);
        if (sourceOrdinal < 0)
        {
            return Failure(sourceText, "The selected nginx server-block ordinal is invalid.");
        }

        var validation = ValidatePatch(patch);
        if (validation is not null)
        {
            return Failure(sourceText, validation);
        }

        var blocks = FindServerBlocks(sourceText);
        if (sourceOrdinal >= blocks.Count)
        {
            return Failure(sourceText, "The selected nginx server block no longer exists in this source file. Reload before editing.");
        }

        var span = blocks[sourceOrdinal];
        var block = sourceText.Substring(span.Start, span.Length);
        if (ServerNamePattern.Matches(block).Count != 1 ||
            ListenPattern.Matches(block).Count != 1 ||
            ProxyPassPattern.Matches(block).Count != 1)
        {
            return Failure(
                sourceText,
                "Simple mode requires exactly one server_name, one listen and one proxy_pass directive. Use raw mode for advanced nginx layouts.");
        }

        var patched = ReplaceDirective(block, ServerNamePattern, "server_name", string.Join(' ', patch.ServerNames));
        patched = ReplaceDirective(patched, ListenPattern, "listen", patch.Listen.Trim());
        patched = ReplaceDirective(patched, ProxyPassPattern, "proxy_pass", patch.ProxyPass.Trim());

        var candidate = string.Concat(
            sourceText.AsSpan(0, span.Start),
            patched,
            sourceText.AsSpan(span.Start + span.Length));
        return new NginxSimpleSitePatchResult(true, candidate, "Simple nginx fields were applied to the raw candidate without rewriting unsupported directives.");
    }

    private static string? ValidatePatch(NginxSimpleSitePatch patch)
    {
        if (patch.ServerNames.Count == 0)
        {
            return "At least one server name is required in simple mode.";
        }

        if (patch.ServerNames.Any(value => !IsSafeAtom(value)))
        {
            return "Server names cannot contain whitespace, comments, braces, semicolons or control characters.";
        }

        if (!IsSafeDirectiveValue(patch.Listen, allowSpaces: true))
        {
            return "The listen value contains syntax that is not supported by simple mode.";
        }

        if (!IsSafeDirectiveValue(patch.ProxyPass, allowSpaces: false) ||
            patch.ProxyPass.Contains("***@", StringComparison.Ordinal))
        {
            return "The proxy target is not safe for simple mode. Use raw mode for advanced or credential-bearing targets.";
        }

        return null;
    }

    private static bool IsSafeAtom(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length <= 255 &&
        !value.Any(char.IsWhiteSpace) &&
        value.IndexOfAny([';', '{', '}', '#', '\r', '\n', '\0']) < 0;

    private static bool IsSafeDirectiveValue(string value, bool allowSpaces)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            value.IndexOfAny([';', '{', '}', '#', '\r', '\n', '\0']) >= 0)
        {
            return false;
        }

        return allowSpaces || !value.Any(char.IsWhiteSpace);
    }

    private static string ReplaceDirective(string block, Regex pattern, string directive, string value) =>
        pattern.Replace(
            block,
            match => $"{match.Groups["indent"].Value}{directive} {value};{match.Groups["tail"].Value}",
            count: 1);

    private static Regex DirectivePattern(string directive) =>
        new(
            $@"(?m)^(?<indent>[ \t]*){Regex.Escape(directive)}[ \t]+[^;\r\n]*;(?<tail>[^\r\n]*)$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static IReadOnlyList<TextSpan> FindServerBlocks(string text)
    {
        var spans = new List<TextSpan>();
        foreach (Match start in ServerStartPattern.Matches(text))
        {
            var openBrace = text.IndexOf('{', start.Index, start.Length);
            if (openBrace < 0)
            {
                continue;
            }

            var closeBrace = FindMatchingBrace(text, openBrace);
            if (closeBrace < 0)
            {
                continue;
            }

            spans.Add(new TextSpan(start.Index, closeBrace - start.Index + 1));
        }

        return spans;
    }

    private static int FindMatchingBrace(string text, int openBrace)
    {
        var depth = 0;
        var quote = '\0';
        var inComment = false;
        for (var index = openBrace; index < text.Length; index++)
        {
            var character = text[index];
            if (inComment)
            {
                if (character == '\n')
                {
                    inComment = false;
                }

                continue;
            }

            if (quote != '\0')
            {
                if (character == quote && (index == 0 || text[index - 1] != '\\'))
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
                inComment = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static NginxSimpleSitePatchResult Failure(string original, string message) =>
        new(false, original, message);

    private readonly record struct TextSpan(int Start, int Length);
}
