using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class FirewallMutationLocalizationTests
{
    [Fact]
    public void EnglishAndVietnameseMutationResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.FirewallMutations.en.xaml");
        var vietnamese = ReadResources("Strings.FirewallMutations.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void BothLanguagesExplicitlyAvoidConnectivityGuarantees()
    {
        var english = ReadResources("Strings.FirewallMutations.en.xaml");
        var vietnamese = ReadResources("Strings.FirewallMutations.vi.xaml");

        Assert.Contains(
            english.Values,
            value => value.Contains("guarantee", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            vietnamese.Values,
            value => value.Contains("bảo đảm", StringComparison.OrdinalIgnoreCase) ||
                     value.Contains("đảm bảo", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Loc.FirewallMutation.Title", "Production")]
    [InlineData("Loc.FirewallMutation.SshObserved", "203.0.113.10", 22)]
    public void RequiredMutationResourcesFormatInBothLanguages(
        string key,
        string first,
        object? second = null)
    {
        var english = ReadResources("Strings.FirewallMutations.en.xaml");
        var vietnamese = ReadResources("Strings.FirewallMutations.vi.xaml");
        var arguments = second is null ? new object?[] { first } : [first, second];

        var englishText = string.Format(CultureInfo.GetCultureInfo("en-US"), english[key], arguments);
        var vietnameseText = string.Format(CultureInfo.GetCultureInfo("vi-VN"), vietnamese[key], arguments);

        Assert.Contains(first, englishText, StringComparison.Ordinal);
        Assert.Contains(first, vietnameseText, StringComparison.Ordinal);
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
