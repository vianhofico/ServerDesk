using Xunit;

namespace ServerDesk.Tests;

public sealed class NginxInventoryPresentationTests
{
    [Fact]
    public void InventoryWorkspaceUsesSharedFluentOperationalPatterns()
    {
        var xaml = ReadPresentation();

        Assert.Contains("xmlns:ui=\"http://schemas.lepo.co/wpfui/2022/xaml\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource PageTitleText}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource StatusPill}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CommandBarSurface}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource OperationalSearchTextBox}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource InlineInfoCard}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource OperationalDataGrid}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("<ui:Card", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource DetailsPane}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource TechnicalText}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryCommandsAndSearchAreAccessibleAndRiskOrdered()
    {
        var xaml = ReadPresentation();

        var refresh = xaml.IndexOf("x:Name=\"RefreshButton\"", StringComparison.Ordinal);
        var edit = xaml.IndexOf("x:Name=\"EditButton\"", StringComparison.Ordinal);
        var cancel = xaml.IndexOf("x:Name=\"CancelButton\"", StringComparison.Ordinal);

        Assert.True(refresh >= 0);
        Assert.True(edit > refresh);
        Assert.True(cancel > edit);
        Assert.Contains("Style=\"{StaticResource PrimaryButton}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource SecondaryButton}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource GhostButton}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Nginx.Refresh}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Nginx.Edit}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Nginx.Cancel}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Nginx.Search}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Nginx.ClearSearch}\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryDetailsKeepTechnicalConfigurationReadableAndLabeled()
    {
        var xaml = ReadPresentation();

        Assert.Contains("x:Name=\"EndpointText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"SelectedTitleText\"", xaml, StringComparison.Ordinal);
        Assert.Contains("Text=\"{DynamicResource Loc.Nginx.RawBlock}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("x:Name=\"RawBlockTextBox\"", xaml, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Nginx.RawBlock}\"", xaml, StringComparison.Ordinal);
        Assert.Contains("FontFamily=\"Cascadia Mono, Consolas\"", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void InventoryUsesSharedGridVirtualizationInsteadOfDuplicatingSettings()
    {
        var xaml = ReadPresentation();

        Assert.DoesNotContain("EnableRowVirtualization=\"True\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("EnableColumnVirtualization=\"True\"", xaml, StringComparison.Ordinal);
        Assert.DoesNotContain("BorderBrush=\"#22000000\"", xaml, StringComparison.Ordinal);
    }

    private static string ReadPresentation()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Presentation", "NginxInventoryWindow.xaml");
        return File.ReadAllText(path);
    }
}
