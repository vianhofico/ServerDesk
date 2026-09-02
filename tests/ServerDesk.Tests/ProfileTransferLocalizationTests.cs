using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ProfileTransferLocalizationTests
{
    [Fact]
    public void EnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.ProfileTransfer.en.xaml");
        var vietnamese = ReadResources("Strings.ProfileTransfer.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void ImportSummaryFormatsInBothLanguages()
    {
        var english = ReadResources("Strings.ProfileTransfer.en.xaml");
        var vietnamese = ReadResources("Strings.ProfileTransfer.vi.xaml");
        var arguments = new object?[] { 2, 1, 1, 0, 4 };

        var englishText = string.Format(english["Loc.Transfer.ImportComplete"], arguments);
        var vietnameseText = string.Format(vietnamese["Loc.Transfer.ImportComplete"], arguments);

        Assert.Contains("4", englishText, StringComparison.Ordinal);
        Assert.Contains("4", vietnameseText, StringComparison.Ordinal);
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
