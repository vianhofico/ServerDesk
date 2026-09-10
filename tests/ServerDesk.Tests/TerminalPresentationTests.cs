using System.Xml.Linq;
using Xunit;

namespace ServerDesk.Tests;

public sealed class TerminalPresentationTests
{
    [Fact]
    public void TerminalWindowUsesFluentChromeLocalizedTextAndVisibleServerLabel()
    {
        var content = File.ReadAllText(PresentationFixture("TerminalWindow.xaml"));

        Assert.Contains("xmlns:ui=\"http://schemas.lepo.co/wpfui/2022/xaml\"", content, StringComparison.Ordinal);
        Assert.Contains("Title=\"{DynamicResource Loc.Terminal.WindowTitle}\"", content, StringComparison.Ordinal);
        Assert.Contains("<ui:Card", content, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource CommandBarSurface}\"", content, StringComparison.Ordinal);
        Assert.Contains("Style=\"{StaticResource AccentStatusPill}\"", content, StringComparison.Ordinal);
        Assert.Contains("Text=\"{DynamicResource Loc.Terminal.Server.Label}\"", content, StringComparison.Ordinal);
        Assert.Contains("AutomationProperties.Name=\"{DynamicResource Loc.Terminal.Server.Label}\"", content, StringComparison.Ordinal);
        Assert.Contains("Loc.Terminal.Command.New", content, StringComparison.Ordinal);
        Assert.Contains("Loc.Terminal.Command.CloseTab", content, StringComparison.Ordinal);
        Assert.Contains("Loc.Terminal.Shortcuts", content, StringComparison.Ordinal);
        Assert.DoesNotContain("PreviewKeyDown=", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"+ New terminal\"", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Close tab\"", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalCodeBehindLocalizesChromeAndBridgeFeedbackWithoutAddingShellDispatch()
    {
        var content = File.ReadAllText(PresentationFixture("TerminalWindow.xaml.cs"));

        Assert.Contains("TerminalPresentationText.Format(\"Loc.Terminal.Tab.ConnectingFormat\"", content, StringComparison.Ordinal);
        Assert.Contains("TerminalPresentationText.State(state)", content, StringComparison.Ordinal);
        Assert.Contains("Loc.Terminal.Status.OpeningFormat", content, StringComparison.Ordinal);
        Assert.Contains("Loc.Terminal.Status.ConnectedFormat", content, StringComparison.Ordinal);
        Assert.Contains("Loc.Terminal.Bridge.NavigationBlocked", content, StringComparison.Ordinal);
        Assert.Contains("Loc.Terminal.Bridge.ClipboardTooLarge", content, StringComparison.Ordinal);
        Assert.DoesNotContain("Process.Start", content, StringComparison.Ordinal);
        Assert.DoesNotContain("cmd.exe", content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("powershell", content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TerminalHostDrainsInitializationAndBridgeWorkBeforeResourceTeardown()
    {
        var content = File.ReadAllText(PresentationFixture("TerminalWindow.xaml.cs"));

        Assert.Contains("using var operation = _operations.EnterOrThrow(this);", content, StringComparison.Ordinal);
        Assert.Contains("if (!_operations.TryEnter(out var operation))", content, StringComparison.Ordinal);
        Assert.Contains("_operations.TryRunWhileAccepting", content, StringComparison.Ordinal);
        Assert.Contains("var drainTask = _operations.StopAndDrainAsync();", content, StringComparison.Ordinal);
        Assert.Contains("await drainTask.ConfigureAwait(true);", content, StringComparison.Ordinal);
        Assert.Contains("catch (ObjectDisposedException) when (_operations.IsStopping)", content, StringComparison.Ordinal);
        Assert.Contains("return new ValueTask(_disposeCompletion.Task);", content, StringComparison.Ordinal);
    }

    [Fact]
    public void TerminalEnglishAndVietnameseResourcesHaveIdenticalKeys()
    {
        var english = ReadResources("Strings.Terminal.en.xaml");
        var vietnamese = ReadResources("Strings.Terminal.vi.xaml");

        Assert.NotEmpty(english);
        Assert.Equal(
            english.Keys.OrderBy(key => key, StringComparer.Ordinal),
            vietnamese.Keys.OrderBy(key => key, StringComparer.Ordinal));
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

    private static string PresentationFixture(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Presentation", fileName);
}
