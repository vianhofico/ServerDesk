using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class EnvironmentLocalizationTests
{
    [Fact]
    public void EnvironmentEnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.Env.en.xaml");
        var vietnamese = ReadResources("Strings.Env.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void EnvironmentParameterizedResourcesFormatInBothLanguages()
    {
        var english = ReadResources("Strings.Env.en.xaml");
        var vietnamese = ReadResources("Strings.Env.vi.xaml");

        var englishText = string.Format(CultureInfo.GetCultureInfo("en-US"), english["Loc.Env.ApplyConfirmMessage"], "/srv/app/.env", 3);
        var vietnameseText = string.Format(CultureInfo.GetCultureInfo("vi-VN"), vietnamese["Loc.Env.ApplyConfirmMessage"], "/srv/app/.env", 3);

        Assert.Contains("/srv/app/.env", englishText, StringComparison.Ordinal);
        Assert.Contains("3", englishText, StringComparison.Ordinal);
        Assert.Contains("/srv/app/.env", vietnameseText, StringComparison.Ordinal);
        Assert.Contains("3", vietnameseText, StringComparison.Ordinal);
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
}
