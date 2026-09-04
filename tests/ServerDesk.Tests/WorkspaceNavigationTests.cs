using System.Xml.Linq;
using ServerDesk.App.Presentation;
using Xunit;

namespace ServerDesk.Tests;

public sealed class WorkspaceNavigationTests
{
    [Fact]
    public void WorkspaceNavigationRoutesAreUniqueAndContainDeliveredV1Modules()
    {
        var items = WorkspaceNavigationCatalog.Items;

        Assert.Equal(items.Count, items.Select(item => item.Route).Distinct(StringComparer.Ordinal).Count());
        Assert.Contains(items, item => item.Route == WorkspaceNavigationCatalog.Explorer);
        Assert.Contains(items, item => item.Route == WorkspaceNavigationCatalog.Terminal);
        Assert.Contains(items, item => item.Route == WorkspaceNavigationCatalog.Processes);
        Assert.Contains(items, item => item.Route == WorkspaceNavigationCatalog.Services);
        Assert.Contains(items, item => item.Route == WorkspaceNavigationCatalog.Docker);
        Assert.Contains(items, item => item.Route == WorkspaceNavigationCatalog.Storage);
        Assert.Contains(items, item => item.Route == WorkspaceNavigationCatalog.Network);
        Assert.Contains(items, item => item.Route == WorkspaceNavigationCatalog.Logs);
        Assert.Contains(items, item => item.Route == WorkspaceNavigationCatalog.Git);
        Assert.Contains(items, item => item.Route == WorkspaceNavigationCatalog.Nginx);
        Assert.Contains(items, item => item.Route == WorkspaceNavigationCatalog.Databases);
        Assert.Contains(items, item => item.Route == WorkspaceNavigationCatalog.Backups);
    }

    [Fact]
    public void OnlyGlobalUtilitiesAreAvailableWithoutServerContext()
    {
        var globalRoutes = WorkspaceNavigationCatalog.Items
            .Where(item => !item.RequiresServer)
            .Select(item => item.Route)
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains(WorkspaceNavigationCatalog.GlobalDashboard, globalRoutes);
        Assert.Contains(WorkspaceNavigationCatalog.Organize, globalRoutes);
        Assert.Contains(WorkspaceNavigationCatalog.ConnectionHistory, globalRoutes);
        Assert.DoesNotContain(WorkspaceNavigationCatalog.Explorer, globalRoutes);
        Assert.DoesNotContain(WorkspaceNavigationCatalog.Terminal, globalRoutes);
        Assert.DoesNotContain(WorkspaceNavigationCatalog.Docker, globalRoutes);
    }

    [Fact]
    public void ShellEnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.Shell.en.xaml");
        var vietnamese = ReadResources("Strings.Shell.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void NavigationCopyDoesNotContainRoadmapPlaceholderLanguage()
    {
        var english = ReadResources("Strings.Shell.en.xaml");
        var navigationValues = english
            .Where(pair => pair.Key.StartsWith("Loc.Shell.Nav.", StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .ToArray();

        Assert.DoesNotContain(navigationValues, value => value.Contains("arrives in M", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(navigationValues, value => value.Contains("coming soon", StringComparison.OrdinalIgnoreCase));
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
