using Xunit;

namespace ServerDesk.Tests;

public sealed class AgentLogContractTests
{
    [Fact]
    public void JournalLogWireRequestIsEmptyAndHasNoSourceSelectionSurface()
    {
        var schema = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Agent", "agent_control.proto"));

        Assert.Contains("rpc StreamJournalLogs(LogStreamRequest) returns (stream JournalLogEntry);", schema, StringComparison.Ordinal);
        Assert.Contains("message LogStreamRequest {\n}", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("file_path", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("log_path", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("cursor", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("filter", schema, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stderr", schema, StringComparison.OrdinalIgnoreCase);
    }
}
