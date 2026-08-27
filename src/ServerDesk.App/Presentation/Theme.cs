using System.Windows;
using ServerDesk.Application.Settings;

namespace ServerDesk.App.Presentation;

public interface IThemeService
{
    AppThemeKind EffectiveTheme { get; }

    void Apply(AppThemePreference preference);
}

public sealed class WpfThemeService : IThemeService
{
    private readonly ISystemThemeDetector _systemThemeDetector;

    public WpfThemeService(ISystemThemeDetector systemThemeDetector)
    {
        _systemThemeDetector = systemThemeDetector;
    }

    public AppThemeKind EffectiveTheme { get; private set; } = AppThemeKind.Light;

    public void Apply(AppThemePreference preference)
    {
        EffectiveTheme = ThemePreferenceResolver.Resolve(
            preference,
            _systemThemeDetector.GetCurrentTheme());

        var resources = System.Windows.Application.Current.Resources;
        var oldThemes = resources.MergedDictionaries
            .Where(IsServerDeskThemeDictionary)
            .ToArray();

        foreach (var dictionary in oldThemes)
        {
            resources.MergedDictionaries.Remove(dictionary);
        }

        resources.MergedDictionaries.Insert(
            0,
            new ResourceDictionary
            {
                Source = new Uri(
                    $"pack://application:,,,/ServerDesk.App;component/Themes/{EffectiveTheme}.xaml",
                    UriKind.Absolute),
            });
    }

    private static bool IsServerDeskThemeDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return source is not null &&
               source.Contains("ServerDesk.App;component/Themes/", StringComparison.OrdinalIgnoreCase);
    }
}
