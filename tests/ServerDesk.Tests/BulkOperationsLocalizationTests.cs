using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class BulkOperationsLocalizationTests
{
    [Fact]
    public void EnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.BulkOperations.en.xaml");
        var vietnamese = ReadResources("Strings.BulkOperations.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("Loc.Bulk.RefreshingSelected", 2, 3)]
    [InlineData("Loc.Bulk.Running", 3, null)]
    public void ParameterizedResourcesFormatInBothLanguages(string key, int first, int? second)
    {
        var english = ReadResources("Strings.BulkOperations.en.xaml");
        var vietnamese = ReadResources("Strings.BulkOperations.vi.xaml");
        object?[] arguments = second is null ? [first] : [first, second.Value];

        var englishText = string.Format(english[key], arguments);
        var vietnameseText = string.Format(vietnamese[key], arguments);

        Assert.Contains(first.ToString(), englishText, StringComparison.Ordinal);
        Assert.Contains(first.ToString(), vietnameseText, StringComparison.Ordinal);
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
