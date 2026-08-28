namespace ServerDesk.Application.Docker;

internal static class StringCompatibilityExtensions
{
    public static bool StartsWith(this string value, char prefix, StringComparison comparison) =>
        value.StartsWith(prefix.ToString(), comparison);
}
