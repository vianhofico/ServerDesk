using ServerDesk.Application.Terminal;
using Xunit;

namespace ServerDesk.Tests;

public sealed class TerminalContractTests
{
    [Fact]
    public void DefaultTerminalSizeIsUsable()
    {
        var size = TerminalSize.Default;

        Assert.Equal((uint)120, size.Columns);
        Assert.Equal((uint)30, size.Rows);
    }

    [Theory]
    [InlineData(1, 24)]
    [InlineData(1001, 24)]
    [InlineData(80, 0)]
    [InlineData(80, 1001)]
    public void TerminalSizeRejectsUnsafeDimensions(uint columns, uint rows)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TerminalSize(columns, rows));
    }

    [Fact]
    public void TerminalBoundaryDoesNotReferenceSshNetOrWebView2()
    {
        var references = typeof(IRemoteTerminalSession).Assembly
            .GetReferencedAssemblies()
            .Select(reference => reference.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(references, name => name.StartsWith("Renci.SshNet", StringComparison.Ordinal));
        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.Web.WebView2", StringComparison.Ordinal));
    }

    [Fact]
    public void TerminalLifecycleStatesAreExplicit()
    {
        Assert.True(Enum.IsDefined(TerminalSessionState.Created));
        Assert.True(Enum.IsDefined(TerminalSessionState.Connecting));
        Assert.True(Enum.IsDefined(TerminalSessionState.Connected));
        Assert.True(Enum.IsDefined(TerminalSessionState.Disconnecting));
        Assert.True(Enum.IsDefined(TerminalSessionState.Disconnected));
        Assert.True(Enum.IsDefined(TerminalSessionState.Faulted));
    }
}
