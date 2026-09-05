using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ScheduledTasksPresentationTests
{
    [Fact]
    public void TasksWorkspaceEnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.TasksWorkspace.en.xaml");
        var vietnamese = ReadResources("Strings.TasksWorkspace.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void TasksWorkspaceResourcesCoverContextSearchDetailsAndStates()
    {
        var english = ReadResources("Strings.TasksWorkspace.en.xaml");

        Assert.Contains("Loc.TasksWorkspace.Header.Environment", english.Keys);
        Assert.Contains("Loc.TasksWorkspace.Header.Connection", english.Keys);
        Assert.Contains("Loc.TasksWorkspace.Search.Clear", english.Keys);
        Assert.Contains("Loc.TasksWorkspace.Overlay.SearchTitle", english.Keys);
        Assert.Contains("Loc.TasksWorkspace.Overlay.CancelledTitle", english.Keys);
        Assert.Contains("Loc.TasksWorkspace.Details.Command", english.Keys);
    }

    [Fact]
    public void ScheduledTasksWindowUsesCanonicalOperationalPatternsAndDangerDelete()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Presentation", "ScheduledTasksWindow.xaml");
        var xaml = File.ReadAllText(path);

        Assert.Contains("Style=\"{StaticResource OperationalDataGrid}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource OperationalSearchTextBox}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CommandBarSurface}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DetailsPane}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStateOverlay\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailsContent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ClearSearchButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DeleteButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DangerButton}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"Cascadia Mono, Consolas\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RawCronBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ApplyRawCronButton\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledTasksWindowKeepsAdvancedAndReadOnlySurfacesSeparate()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Presentation", "ScheduledTasksWindow.xaml");
        var xaml = File.ReadAllText(path);

        Assert.Contains("Loc.Tasks.RawCrontab", xaml, StringComparison.Ordinal);
        Assert.Contains("Loc.Tasks.History", xaml, StringComparison.Ordinal);
        Assert.Contains("Loc.Tasks.RawSource", xaml, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ApplyRawCronOnClick\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoGenerateColumns=\"True\"", xaml, StringComparison.Ordinal);
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
