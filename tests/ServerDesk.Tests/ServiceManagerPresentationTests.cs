using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ServiceManagerPresentationTests
{
    [Fact]
    public void ServiceEnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.Services.en.xaml");
        var vietnamese = ReadResources("Strings.Services.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void ServiceResourcesCoverSearchStatesAndMutationSafetyCopy()
    {
        var english = ReadResources("Strings.Services.en.xaml");

        Assert.Contains("Loc.Services.Search.Label", english.Keys);
        Assert.Contains("Loc.Services.Overlay.SearchTitle", english.Keys);
        Assert.Contains("Loc.Services.Status.Ambiguous", english.Keys);
        Assert.Contains("Loc.Services.Confirm.Body", english.Keys);
        Assert.Contains("Loc.Services.Consequence.Stop", english.Keys);
        Assert.Contains("Loc.Services.Consequence.Disable", english.Keys);
    }

    [Fact]
    public void ServiceWindowUsesCanonicalOperationalStylesAndSeparatesDisruptiveActions()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Presentation", "ServiceManagerWindow.xaml");
        var xaml = File.ReadAllText(path);

        Assert.Contains("Style=\"{StaticResource OperationalDataGrid}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource OperationalSearchTextBox}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CommandBarSurface}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DetailsPane}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"StopButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DisableButton\"", xaml, StringComparison.Ordinal);
        Assert.True(
            xaml.Split("Style=\"{StaticResource DangerButton}\"", StringSplitOptions.None).Length - 1 >= 2,
            "Stop and Disable should remain visually disruptive.");
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
