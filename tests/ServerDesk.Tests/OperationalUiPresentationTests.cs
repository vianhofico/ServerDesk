using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class OperationalUiPresentationTests
{
    [Fact]
    public void OperationalStylesDefineCanonicalGridSearchCommandAndDetailsPatterns()
    {
        var document = XDocument.Load(PresentationFixture("Operational.xaml"), LoadOptions.None);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var styles = document.Root!
            .Elements(presentation + "Style")
            .Where(element => element.Attribute(x + "Key") is not null)
            .ToDictionary(
                element => element.Attribute(x + "Key")!.Value,
                StringComparer.Ordinal);

        Assert.Contains("OperationalDataGrid", styles.Keys);
        Assert.Contains("OperationalSearchTextBox", styles.Keys);
        Assert.Contains("CommandBarSurface", styles.Keys);
        Assert.Contains("DetailsPane", styles.Keys);
        Assert.Contains("TechnicalText", styles.Keys);

        var dataGrid = styles["OperationalDataGrid"];
        var setters = dataGrid
            .Elements(presentation + "Setter")
            .Where(element => element.Attribute("Property") is not null)
            .ToDictionary(
                element => element.Attribute("Property")!.Value,
                element => element.Attribute("Value")?.Value ?? string.Empty,
                StringComparer.Ordinal);

        Assert.Equal("Single", setters["SelectionMode"]);
        Assert.Equal("FullRow", setters["SelectionUnit"]);
        Assert.Equal("True", setters["EnableRowVirtualization"]);
        Assert.Equal("True", setters["EnableColumnVirtualization"]);
        Assert.Equal("True", setters["VirtualizingPanel.IsVirtualizing"]);
        Assert.Equal("Recycling", setters["VirtualizingPanel.VirtualizationMode"]);
        Assert.Equal("True", setters["ScrollViewer.CanContentScroll"]);
    }

    [Theory]
    [InlineData("RemoteExplorerWindow.xaml")]
    [InlineData("ProcessManagerWindow.xaml")]
    [InlineData("ServiceManagerWindow.xaml")]
    [InlineData("DockerInventoryWindow.xaml")]
    public void OperationalWindowsConsumeSharedGridSearchAndCommandPatterns(string fileName)
    {
        var content = File.ReadAllText(PresentationFixture(fileName));

        Assert.Contains("Style=\"{StaticResource OperationalDataGrid}\"", content, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource OperationalSearchTextBox}\"", content, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CommandBarSurface}\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TouchedModulesDoNotReintroduceLocalButtonStyles()
    {
        Assert.DoesNotContain(
            "ProcessButton",
            File.ReadAllText(PresentationFixture("ProcessManagerWindow.xaml")),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "ServiceButton",
            File.ReadAllText(PresentationFixture("ServiceManagerWindow.xaml")),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "DockerButton",
            File.ReadAllText(PresentationFixture("DockerInventoryWindow.xaml")),
            StringComparison.Ordinal);
    }

    private static string PresentationFixture(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Presentation", fileName);
}
