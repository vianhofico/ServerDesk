using System.Globalization;
using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class UserAdministrationLocalizationTests
{
    [Fact]
    public void EnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.UserAdministration.en.xaml");
        var vietnamese = ReadResources("Strings.UserAdministration.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void BothLanguagesStateNoSshGuaranteeAndPublicKeyOnlyPolicy()
    {
        var english = ReadResources("Strings.UserAdministration.en.xaml");
        var vietnamese = ReadResources("Strings.UserAdministration.vi.xaml");

        Assert.Contains(english.Values, value =>
            value.Contains("cannot guarantee", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vietnamese.Values, value =>
            value.Contains("không thể đảm bảo", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(english.Values, value =>
            value.Contains("private-key", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("private key", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(vietnamese.Values, value =>
            value.Contains("private key", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Loc.UserAdmin.Title", "Production")]
    [InlineData("Loc.UserAdmin.KeysLoaded", "2", "dev")]
    public void RequiredResourcesFormatInBothLanguages(
        string key,
        string first,
        object? second = null)
    {
        var english = ReadResources("Strings.UserAdministration.en.xaml");
        var vietnamese = ReadResources("Strings.UserAdministration.vi.xaml");
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
