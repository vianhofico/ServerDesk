using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class DockerInventoryPresentationTests
{
    [Fact]
    public void DockerEnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.DockerInventory.en.xaml");
        var vietnamese = ReadResources("Strings.DockerInventory.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void DockerResourcesCoverRuntimeSearchPartialAndSafetyCopy()
    {
        var english = ReadResources("Strings.DockerInventory.en.xaml");

        Assert.Contains("Loc.Docker.Runtime.Channel", english.Keys);
        Assert.Contains("Loc.Docker.Runtime.HostDetail", english.Keys);
        Assert.Contains("Loc.Docker.Search.Clear", english.Keys);
        Assert.Contains("Loc.Docker.Status.Partial", english.Keys);
        Assert.Contains("Loc.Docker.Overlay.SearchTitle", english.Keys);
        Assert.Contains("Loc.Docker.Command.DiagnosticsTooltip", english.Keys);
        Assert.Contains("socket", english["Loc.Docker.Runtime.Channel"], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DockerWindowUsesCanonicalOperationalStylesAndReadOnlyInventoryStructure()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Presentation", "DockerInventoryWindow.xaml");
        var xaml = File.ReadAllText(path);

        Assert.Contains("Style=\"{StaticResource OperationalDataGrid}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource OperationalSearchTextBox}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CommandBarSurface}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DetailsPane}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DiagnosticsButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ContainerDetailsContent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ContainerStateOverlay\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ImageStateOverlay\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"VolumeStateOverlay\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"NetworkStateOverlay\"", xaml, StringComparison.Ordinal);
        Assert.Contains("BasedOn=\"{StaticResource TechnicalText}\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StartOnClick", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("StopOnClick", xaml, StringComparison.Ordinal);
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
