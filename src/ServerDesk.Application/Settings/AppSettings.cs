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

public sealed record AppSettings(AppThemePreference ThemePreference)
{
    public static AppSettings Default { get; } = new(AppThemePreference.System);
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
