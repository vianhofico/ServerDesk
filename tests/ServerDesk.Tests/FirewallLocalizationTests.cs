using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class FirewallLocalizationTests
{
    [Fact]
    public void FirewallEnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.Firewall.en.xaml");
        var vietnamese = ReadResources("Strings.Firewall.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Loc.Firewall.Title", "Production")]
    [InlineData("Loc.Firewall.AdapterSummary", "UFW: active")]
    public void FirewallSingleArgumentResourcesFormatInBothLanguages(string key, string value)
    {
        var english = ReadResources("Strings.Firewall.en.xaml");
        var vietnamese = ReadResources("Strings.Firewall.vi.xaml");

        var englishText = string.Format(CultureInfo.GetCultureInfo("en-US"), english[key], value);
        var vietnameseText = string.Format(CultureInfo.GetCultureInfo("vi-VN"), vietnamese[key], value);

        Assert.Contains(value, englishText, StringComparison.Ordinal);
        Assert.Contains(value, vietnameseText, StringComparison.Ordinal);
    }

    [Fact]
    public void AvailableStatusFormatsInBothLanguages()
    {
        var english = ReadResources("Strings.Firewall.en.xaml");
        var vietnamese = ReadResources("Strings.Firewall.vi.xaml");

        var englishText = string.Format(CultureInfo.GetCultureInfo("en-US"), english["Loc.Firewall.Available"], "UFW", 3);
        var vietnameseText = string.Format(CultureInfo.GetCultureInfo("vi-VN"), vietnamese["Loc.Firewall.Available"], "UFW", 3);

        Assert.Contains("UFW", englishText, StringComparison.Ordinal);
        Assert.Contains("3", englishText, StringComparison.Ordinal);
        Assert.Contains("UFW", vietnameseText, StringComparison.Ordinal);
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
