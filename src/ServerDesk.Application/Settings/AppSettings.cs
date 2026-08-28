namespace ServerDesk.Application.Settings;

public enum AppThemePreference
{
    System,
    Light,
    Dark,
}

public enum AppThemeKind
{
    Light,
    Dark,
}

public enum AppLanguagePreference
{
    System,
    English,
    Vietnamese,
}

public enum AppLanguageKind
{
    English,
    Vietnamese,
}

public sealed record AppSettings(
    AppThemePreference ThemePreference,
    AppLanguagePreference LanguagePreference = AppLanguagePreference.System)
{
    public static AppSettings Default { get; } = new(AppThemePreference.System, AppLanguagePreference.System);
}

public interface IAppSettingsStore
{
    ValueTask<AppSettings> LoadAsync(CancellationToken cancellationToken = default);

    ValueTask SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface ISystemThemeDetector
{
    AppThemeKind GetCurrentTheme();
}

public interface ISystemCultureDetector
{
    string GetCurrentCultureName();
}

public static class ThemePreferenceResolver
{
    public static AppThemeKind Resolve(
        AppThemePreference preference,
        AppThemeKind systemTheme) =>
        preference switch
        {
            AppThemePreference.System => systemTheme,
            AppThemePreference.Light => AppThemeKind.Light,
            AppThemePreference.Dark => AppThemeKind.Dark,
            _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, null),
        };
}

public static class LanguagePreferenceResolver
{
    public const string EnglishCode = "en";
    public const string VietnameseCode = "vi";

    public static AppLanguageKind Resolve(
        AppLanguagePreference preference,
        string? systemCultureName) =>
        preference switch
        {
            AppLanguagePreference.System => IsVietnamese(systemCultureName)
                ? AppLanguageKind.Vietnamese
                : AppLanguageKind.English,
            AppLanguagePreference.English => AppLanguageKind.English,
            AppLanguagePreference.Vietnamese => AppLanguageKind.Vietnamese,
            _ => throw new ArgumentOutOfRangeException(nameof(preference), preference, null),
        };

    public static string GetCultureCode(AppLanguageKind language) =>
        language switch
        {
            AppLanguageKind.English => EnglishCode,
            AppLanguageKind.Vietnamese => VietnameseCode,
            _ => throw new ArgumentOutOfRangeException(nameof(language), language, null),
        };

    private static bool IsVietnamese(string? cultureName)
    {
        if (string.IsNullOrWhiteSpace(cultureName))
        {
            return false;
        }

        return cultureName.Equals(VietnameseCode, StringComparison.OrdinalIgnoreCase) ||
               cultureName.StartsWith(VietnameseCode + "-", StringComparison.OrdinalIgnoreCase);
    }
}
