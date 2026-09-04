using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class RemoteExplorerPresentationTests
{
    [Fact]
    public void ExplorerEnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.Explorer.en.xaml");
        var vietnamese = ReadResources("Strings.Explorer.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void ExplorerResourcesCoverNavigationSearchAndDangerActions()
    {
        var english = ReadResources("Strings.Explorer.en.xaml");

        Assert.Contains("Loc.Explorer.Nav.Forward", english.Keys);
        Assert.Contains("Loc.Explorer.Nav.EditPath", english.Keys);
        Assert.Contains("Loc.Explorer.Search.Label", english.Keys);
        Assert.Contains("Loc.Explorer.Command.Delete", english.Keys);
        Assert.Contains("Loc.Explorer.Overlay.SearchTitle", english.Keys);
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
