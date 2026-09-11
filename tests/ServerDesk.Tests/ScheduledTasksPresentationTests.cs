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
        var xaml = ReadPresentation();

        Assert.Contains("xmlns:ui=\"http://schemas.lepo.co/wpfui/2022/xaml\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource PageTitleText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource StatusPill}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource AccentStatusPill}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource OperationalDataGrid}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource OperationalSearchTextBox}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CommandBarSurface}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DetailsPane}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource MetricTile}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ui:Card", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"TaskStateOverlay\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DetailsContent\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ClearSearchButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"EnableButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DisableButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"DeleteButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DangerButton}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"Cascadia Mono, Consolas\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RawCronBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"ApplyRawCronButton\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SaveCronButton\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledTasksEditorHasVisibleLabelsAndAutomationNames()
    {
        var xaml = ReadPresentation();

        Assert.Contains("Style=\"{StaticResource FormLabelText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Tasks.Refresh}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.TasksWorkspace.Search.Clear}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Tasks.Minute}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Tasks.Hour}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Tasks.DayOfMonth}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Tasks.Month}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Tasks.DayOfWeek}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Tasks.Command}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledTasksWindowRemovesDuplicatedLocalTypographyStyles()
    {
        var xaml = ReadPresentation();

        Assert.DoesNotContain("x:Key=\"TaskSummaryLabel\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"TaskSummaryValue\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"TaskDetailLabel\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"TaskDetailValue\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"TaskTechnicalValue\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void ScheduledTasksWindowKeepsStatusStackPanelForRuntimeFooterLocalization()
    {
        var xaml = ReadPresentation();
        var statusCard = xaml.IndexOf("x:Name=\"StatusCard\"", StringComparison.Ordinal);
        var stackPanel = xaml.IndexOf("<StackPanel>", statusCard, StringComparison.Ordinal);
        var capabilityText = xaml.IndexOf("x:Name=\"CapabilityText\"", statusCard, StringComparison.Ordinal);

        Assert.True(statusCard >= 0);
        Assert.True(stackPanel > statusCard);
        Assert.True(capabilityText > stackPanel);
    }

    [Fact]
    public void ScheduledTasksWindowKeepsAdvancedAndReadOnlySurfacesSeparate()
    {
        var xaml = ReadPresentation();

        Assert.Contains("Loc.Tasks.RawCrontab", xaml, StringComparison.Ordinal);
        Assert.Contains("Loc.Tasks.History", xaml, StringComparison.Ordinal);
        Assert.Contains("Loc.Tasks.RawSource", xaml, StringComparison.Ordinal);
        Assert.Contains("IsReadOnly=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Click=\"ApplyRawCronOnClick\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("AutoGenerateColumns=\"True\"", xaml, StringComparison.Ordinal);
    }

    private static string ReadPresentation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Presentation", "ScheduledTasksWindow.xaml");
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
