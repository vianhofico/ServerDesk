namespace ServerDesk.Application.UserAdministration;

internal static class StringCompatibilityExtensions
{
    public static bool StartsWith(this string value, char prefix, StringComparison comparisonType)
    {
        ArgumentNullException.ThrowIfNull(value);
        return value.Length > 0 && string.Equals(value[..1], prefix.ToString(), comparisonType);
    }
}
