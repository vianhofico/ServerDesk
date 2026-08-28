using System.Globalization;
using System.Xml.Linq;
using ServerDesk.App.Localization;
using ServerDesk.Application.Settings;
using Xunit;

namespace ServerDesk.Tests;

public sealed class LocalizationFoundationTests
{
    [Theory]
    [InlineData(AppLanguagePreference.English, "vi-VN", AppLanguageKind.English)]
    [InlineData(AppLanguagePreference.Vietnamese, "en-US", AppLanguageKind.Vietnamese)]
    [InlineData(AppLanguagePreference.System, "vi", AppLanguageKind.Vietnamese)]
    [InlineData(AppLanguagePreference.System, "vi-VN", AppLanguageKind.Vietnamese)]
    [InlineData(AppLanguagePreference.System, "en-US", AppLanguageKind.English)]
    [InlineData(AppLanguagePreference.System, "fr-FR", AppLanguageKind.English)]
    [InlineData(AppLanguagePreference.System, "", AppLanguageKind.English)]
    public void LanguagePreferenceResolverUsesVietnameseSystemCultureAndEnglishFallback(
        AppLanguagePreference preference,
        string systemCulture,
        AppLanguageKind expected)
    {
        var actual = LanguagePreferenceResolver.Resolve(preference, systemCulture);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void EnglishAndVietnameseResourceDictionariesHaveIdenticalKeys()
    {
        AssertResourceParity("Strings.en.xaml", "Strings.vi.xaml");
    }

    [Fact]
    public void ScheduledTaskEnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        AssertResourceParity("Strings.Tasks.en.xaml", "Strings.Tasks.vi.xaml");
    }

    [Fact]
    public void ParameterizedResourceFormatsInBothLanguages()
    {
        var english = ReadResources("Strings.en.xaml");
        var vietnamese = ReadResources("Strings.vi.xaml");

        var englishText = string.Format(
            CultureInfo.GetCultureInfo("en-US"),
            english["Loc.Format.UnableToConnectServer"],
            "Production API");
        var vietnameseText = string.Format(
            CultureInfo.GetCultureInfo("vi-VN"),
            vietnamese["Loc.Format.UnableToConnectServer"],
            "Production API");

        Assert.Equal("Unable to connect to Production API.", englishText);
        Assert.Equal("Không thể kết nối tới Production API.", vietnameseText);
    }

    [Fact]
    public void ScheduledTaskParameterizedResourcesFormatInBothLanguages()
    {
        var english = ReadResources("Strings.Tasks.en.xaml");
        var vietnamese = ReadResources("Strings.Tasks.vi.xaml");

        var englishText = string.Format(CultureInfo.GetCultureInfo("en-US"), english["Loc.Tasks.Loaded"], 3);
        var vietnameseText = string.Format(CultureInfo.GetCultureInfo("vi-VN"), vietnamese["Loc.Tasks.Loaded"], 3);

        Assert.Contains("3", englishText, StringComparison.Ordinal);
        Assert.Contains("3", vietnameseText, StringComparison.Ordinal);
    }

    [Fact]
    public void MissingResourceFallsBackToKeyWithoutThrowing()
    {
        var service = new WpfLocalizationService(new StubSystemCultureDetector("en-US"));

        var value = service.Get("Loc.DoesNotExist");

        Assert.Equal("Loc.DoesNotExist", value);
    }

    private static void AssertResourceParity(string englishFile, string vietnameseFile)
    {
        var english = ReadResources(englishFile);
        var vietnamese = ReadResources(vietnameseFile);

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    private static IReadOnlyDictionary<string, string> ReadResources(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Localization", fileName);
        var document = XDocument.Load(path, LoadOptions.None);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return document.Root!
            .Elements()
            .Where(element => element.Attribute(x + "Key") is not null)
            .ToDictionary(
                element => element.Attribute(x + "Key")!.Value,
                element => element.Value,
                StringComparer.Ordinal);
    }

    private sealed class StubSystemCultureDetector : ISystemCultureDetector
    {
        private readonly string _cultureName;

        public StubSystemCultureDetector(string cultureName)
        {
            _cultureName = cultureName;
        }

        public string GetCurrentCultureName() => _cultureName;
    }
}
