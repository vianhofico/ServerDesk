namespace ServerDesk.Application.Backups;

internal static class BackupStringCompatibilityExtensions
{
    internal static bool StartsWith(this string value, char character, StringComparison comparisonType) =>
        value.StartsWith(character.ToString(), comparisonType);
}
