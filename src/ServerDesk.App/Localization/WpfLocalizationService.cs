using System.Globalization;
using System.Windows;
using ServerDesk.Application.Settings;

namespace ServerDesk.App.Localization;

public interface ILocalizationService
{
    AppLanguageKind EffectiveLanguage { get; }

    string EffectiveCultureCode { get; }

    event Action? LanguageChanged;

    void Apply(AppLanguagePreference preference);

    string Get(string key);

    string Format(string key, params object?[] arguments);
}

public sealed class WpfLocalizationService : ILocalizationService
{
    private const string LocalizationResourceMarker = "ServerDesk.App;component/Localization/Strings.";
    private readonly ISystemCultureDetector _systemCultureDetector;

    public WpfLocalizationService(ISystemCultureDetector systemCultureDetector)
    {
        _systemCultureDetector = systemCultureDetector ?? throw new ArgumentNullException(nameof(systemCultureDetector));
    }

    public AppLanguageKind EffectiveLanguage { get; private set; } = AppLanguageKind.English;

    public string EffectiveCultureCode => LanguagePreferenceResolver.GetCultureCode(EffectiveLanguage);

    public event Action? LanguageChanged;

    public void Apply(AppLanguagePreference preference)
    {
        EffectiveLanguage = LanguagePreferenceResolver.Resolve(
            preference,
            _systemCultureDetector.GetCurrentCultureName());

        var culture = CultureInfo.GetCultureInfo(
            EffectiveLanguage == AppLanguageKind.Vietnamese ? "vi-VN" : "en-US");
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;

        if (System.Windows.Application.Current?.Resources is { } resources)
        {
            var existing = resources.MergedDictionaries
                .Where(IsLocalizationDictionary)
                .ToArray();
            foreach (var dictionary in existing)
            {
                resources.MergedDictionaries.Remove(dictionary);
            }

            resources.MergedDictionaries.Add(CreateDictionary(LanguagePreferenceResolver.EnglishCode));
            if (EffectiveLanguage == AppLanguageKind.Vietnamese)
            {
                resources.MergedDictionaries.Add(CreateDictionary(LanguagePreferenceResolver.VietnameseCode));
            }
        }

        LanguageChanged?.Invoke();
    }

    public string Get(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return System.Windows.Application.Current?.TryFindResource(key) as string ?? key;
    }

    public string Format(string key, params object?[] arguments)
    {
        var template = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, arguments);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private static ResourceDictionary CreateDictionary(string cultureCode) =>
        new()
        {
            Source = new Uri(
                $"pack://application:,,,/ServerDesk.App;component/Localization/Strings.{cultureCode}.xaml",
                UriKind.Absolute),
        };

    private static bool IsLocalizationDictionary(ResourceDictionary dictionary)
    {
        var source = dictionary.Source?.OriginalString;
        return source is not null &&
               source.Contains(LocalizationResourceMarker, StringComparison.OrdinalIgnoreCase);
    }
}
