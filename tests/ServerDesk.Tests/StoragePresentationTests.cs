using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class StoragePresentationTests
{
    [Fact]
    public void StorageEnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.Storage.en.xaml");
        var vietnamese = ReadResources("Strings.Storage.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void StorageResourcesCoverContextPolicySearchAndRecoverableStates()
    {
        var english = ReadResources("Strings.Storage.en.xaml");

        Assert.Contains("Loc.Storage.Header.Environment", english.Keys);
        Assert.Contains("Loc.Storage.Header.Connection", english.Keys);
        Assert.Contains("Loc.Storage.Policy.Text", english.Keys);
        Assert.Contains("Loc.Storage.Search.Clear", english.Keys);
        Assert.Contains("Loc.Storage.State.NoMatch", english.Keys);
        Assert.Contains("Loc.Storage.State.Cancelled", english.Keys);
        Assert.Contains("Loc.Storage.Directory.Cancelled", english.Keys);
        Assert.Contains("Loc.Storage.Error.Permission", english.Keys);
    }

    [Fact]
    public void StorageWindowUsesCanonicalOperationalPatternsAndExplicitStates()
    {
        var xaml = ReadWindow();

        Assert.Contains("Style=\"{StaticResource OperationalDataGrid}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource OperationalSearchTextBox}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CommandBarSurface}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DetailsPane}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ClearSearchButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"FilesystemStateOverlay\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"BlockStateOverlay\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DirectoryStateOverlay\"", xaml, StringComparison.Ordinal);
        Assert.Contains("WarningBrush", xaml, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"Cascadia Mono, Consolas\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void StorageWindowKeepsDirectoryAnalysisReadOnlyAndHasNoStorageMutationHandlers()
    {
        var xaml = ReadWindow();

        Assert.Contains("Click=\"AnalyzeOnClick\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Loc.Storage.Directory.Description", xaml, StringComparison.Ordinal);
        Assert.Contains("Loc.Storage.Footer", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MountOnClick", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("UnmountOnClick", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("FormatOnClick", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("DeleteOnClick", xaml, StringComparison.Ordinal);
    }

    private static string ReadWindow()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Presentation", "StorageWindow.xaml");
        return File.ReadAllText(path);
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
