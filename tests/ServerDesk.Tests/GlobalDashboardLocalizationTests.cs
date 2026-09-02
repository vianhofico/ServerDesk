using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class GlobalDashboardLocalizationTests
{
    [Fact]
    public void EnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.GlobalDashboard.en.xaml");
        var vietnamese = ReadResources("Strings.GlobalDashboard.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void ParameterizedStatusResourcesFormatInBothLanguages()
    {
        var english = ReadResources("Strings.GlobalDashboard.en.xaml");
        var vietnamese = ReadResources("Strings.GlobalDashboard.vi.xaml");

        var englishText = string.Format(english["Loc.GlobalDashboard.RefreshComplete"], 3, 1, 5);
        var vietnameseText = string.Format(vietnamese["Loc.GlobalDashboard.RefreshComplete"], 3, 1, 5);

        Assert.Contains("3", englishText, StringComparison.Ordinal);
        Assert.Contains("1", englishText, StringComparison.Ordinal);
        Assert.Contains("5", englishText, StringComparison.Ordinal);
        Assert.Contains("3", vietnameseText, StringComparison.Ordinal);
        Assert.Contains("1", vietnameseText, StringComparison.Ordinal);
        Assert.Contains("5", vietnameseText, StringComparison.Ordinal);
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
