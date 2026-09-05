using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ProcessManagerPresentationTests
{
    [Fact]
    public void ProcessEnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.Processes.en.xaml");
        var vietnamese = ReadResources("Strings.Processes.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void ProcessResourcesCoverSearchStatesAndSignalSafetyCopy()
    {
        var english = ReadResources("Strings.Processes.en.xaml");

        Assert.Contains("Loc.Processes.Search.Label", english.Keys);
        Assert.Contains("Loc.Processes.Overlay.SearchTitle", english.Keys);
        Assert.Contains("Loc.Processes.Overlay.DisconnectedTitle", english.Keys);
        Assert.Contains("Loc.Processes.Status.Ambiguous", english.Keys);
        Assert.Contains("Loc.Processes.Confirm.Terminate.Body", english.Keys);
        Assert.Contains("Loc.Processes.Confirm.ForceKillFinal.Body", english.Keys);
    }

    [Fact]
    public void ProcessWindowUsesCanonicalOperationalStylesAndSeparatesForceKill()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Presentation", "ProcessManagerWindow.xaml");
        var xaml = File.ReadAllText(path);

        Assert.Contains("Style=\"{StaticResource OperationalDataGrid}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource OperationalSearchTextBox}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CommandBarSurface}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DetailsPane}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ForceKillButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DangerButton}\"", xaml, StringComparison.Ordinal);
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
