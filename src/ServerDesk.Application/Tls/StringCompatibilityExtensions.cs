namespace ServerDesk.Application.Tls;

internal static class StringCompatibilityExtensions
{
    public static bool StartsWith(this string value, char prefix, StringComparison comparisonType) =>
        value.StartsWith(prefix.ToString(), comparisonType);
}
