using Xunit;

namespace ServerDesk.Tests;

public sealed class GitOperationsPresentationTests
{
    [Fact]
    public void GitWorkspaceUsesSharedFluentOperationalPatterns()
    {
        var xaml = ReadPresentation();

        Assert.Contains("xmlns:ui=\"http://schemas.lepo.co/wpfui/2022/xaml\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource PageTitleText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource StatusPill}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CommandBarSurface}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource SectionSurface}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource MetricTile}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource OperationalDataGrid}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ui:Card", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource InlineInfoCard}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource TechnicalText}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void GitWorkspaceKeepsPreviewBeforeApplySafetyHierarchyVisible()
    {
        var xaml = ReadPresentation();

        var fetch = xaml.IndexOf("x:Name=\"FetchButton\"", StringComparison.Ordinal);
        var preview = xaml.IndexOf("x:Name=\"PreviewPullButton\"", StringComparison.Ordinal);
        var apply = xaml.IndexOf("x:Name=\"PullButton\"", StringComparison.Ordinal);

        Assert.True(fetch >= 0);
        Assert.True(preview > fetch);
        Assert.True(apply > preview);
        Assert.Contains("Text=\"{DynamicResource Loc.Git.FastForwardOnly}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{DynamicResource Loc.Git.SafetyFooter}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"PreviewText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"IncomingCommitsList\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource PrimaryButton}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void GitInputsAndCommandsRemainVisiblyLabeledAndAccessible()
    {
        var xaml = ReadPresentation();

        Assert.Contains("Text=\"{DynamicResource Loc.Git.RepositoryPath}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{DynamicResource Loc.Git.DiscoveryRoot}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{DynamicResource Loc.Git.Depth}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource FormLabelText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Git.RepositoryPath}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Git.Fetch}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Git.PreviewPull}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Git.ApplyPull}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Git.Discover}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void GitWorkspaceDoesNotReintroduceLocalStylesOrDuplicatedDataGridVirtualization()
    {
        var xaml = ReadPresentation();

        Assert.DoesNotContain("x:Key=\"GitButton\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"GitSummaryValue\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("x:Key=\"GitSummaryLabel\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableRowVirtualization=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(xaml, "Style=\"{StaticResource OperationalDataGrid}\""));
        Assert.Contains("VirtualizingPanel.IsVirtualizing=\"True\"", xaml, StringComparison.Ordinal);
        Assert.Contains("VirtualizingPanel.VirtualizationMode=\"Recycling\"", xaml, StringComparison.Ordinal);
    }

    private static int CountOccurrences(string content, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }

        return count;
    }

    private static string ReadPresentation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Presentation", "GitOperationsWindow.xaml");
        return File.ReadAllText(path);
    }
}
