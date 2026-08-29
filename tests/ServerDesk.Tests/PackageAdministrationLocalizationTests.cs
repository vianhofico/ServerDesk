using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class PackageAdministrationLocalizationTests
{
    [Fact]
    public void EnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.PackageAdministration.en.xaml");
        var vietnamese = ReadResources("Strings.PackageAdministration.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void BothLanguagesExplicitlyRejectAutomaticGlobalProductionUpgrade()
    {
        var english = ReadResources("Strings.PackageAdministration.en.xaml");
        var vietnamese = ReadResources("Strings.PackageAdministration.vi.xaml");

        Assert.Contains(english.Values, value =>
            value.Contains("never automatically", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("global", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vietnamese.Values, value =>
            value.Contains("không bao giờ tự động", StringComparison.OrdinalIgnoreCase) &&
            value.Contains("toàn hệ thống", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Loc.PackageAdmin.Title", "Production")]
    [InlineData("Loc.PackageAdmin.StatusLoaded", "Apt", 12, "cached")]
    public void RequiredResourcesFormatInBothLanguages(
        string key,
        object first,
        object? second = null,
        object? third = null)
    {
        var english = ReadResources("Strings.PackageAdministration.en.xaml");
        var vietnamese = ReadResources("Strings.PackageAdministration.vi.xaml");
        var arguments = third is not null
            ? new[] { first, second, third }
            : second is not null
                ? new[] { first, second }
                : [first];

        var englishText = string.Format(CultureInfo.GetCultureInfo("en-US"), english[key], arguments);
        var vietnameseText = string.Format(CultureInfo.GetCultureInfo("vi-VN"), vietnamese[key], arguments);

        Assert.Contains(first.ToString()!, englishText, StringComparison.Ordinal);
        Assert.Contains(first.ToString()!, vietnameseText, StringComparison.Ordinal);
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
