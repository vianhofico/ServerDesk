using ServerDesk.App.Presentation;
using ServerDesk.Application.Processes;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ProcessWorkspaceProjectionTests
{
    [Fact]
    public void ProjectPreservesTechnicalFieldsAndFormatsResourceValues()
    {
        ServerProcessInfo[] processes =
        [
            new(
                42,
                1,
                "app",
                "S",
                12.5,
                1_048_576,
                3_661,
                "dotnet",
                "dotnet ServerDesk.Agent.dll"),
        ];

        var rows = ProcessWorkspaceProjection.Project(processes);

        var row = Assert.Single(rows);
        Assert.Equal(42, row.ProcessId);
        Assert.Equal(1, row.ParentProcessId);
        Assert.Equal("app", row.User);
        Assert.Equal("S", row.State);
        Assert.Equal("12.5%", row.CpuText);
        Assert.Equal("1 MiB", row.MemoryText);
        Assert.Equal("01:01:01", row.ElapsedText);
        Assert.Contains("ServerDesk.Agent.dll", row.SearchText, StringComparison.Ordinal);
    }

    [Fact]
    public void FilterIsLocalCaseInsensitiveAndPreservesLoadedOrder()
    {
        var rows = ProcessWorkspaceProjection.Project(
        [
            new ServerProcessInfo(21, 1, "root", "S", 1.2, 512, 30, "sshd", "sshd: user"),
            new ServerProcessInfo(37, 21, "deploy", "R", 8.4, 2_048, 90, "dotnet", "dotnet api.dll"),
            new ServerProcessInfo(51, 21, "deploy", "S", 0.4, 1_024, 120, "nginx", "nginx: worker"),
        ]);

        Assert.Equal([37, 51], ProcessWorkspaceProjection.Filter(rows, "DEPLOY").Select(row => row.ProcessId));
        Assert.Equal([37], ProcessWorkspaceProjection.Filter(rows, "api.dll").Select(row => row.ProcessId));
        Assert.Equal([21], ProcessWorkspaceProjection.Filter(rows, "21 sshd").Select(row => row.ProcessId));
        Assert.Equal(rows.Select(row => row.ProcessId), ProcessWorkspaceProjection.Filter(rows, " ").Select(row => row.ProcessId));
    }

    [Fact]
    public void SummarizeUsesLoadedSnapshotForUsersAndMemoryAndVisibleRowsForVisibleCount()
    {
        var rows = ProcessWorkspaceProjection.Project(
        [
            new ServerProcessInfo(21, 1, "root", "S", 1.2, 1_024, 30, "sshd", "sshd: user"),
            new ServerProcessInfo(37, 21, "Deploy", "R", 8.4, 2_048, 90, "dotnet", "dotnet api.dll"),
            new ServerProcessInfo(51, 21, "deploy", "S", 0.4, 4_096, 120, "nginx", "nginx: worker"),
        ]);
        var visible = ProcessWorkspaceProjection.Filter(rows, "deploy");

        var summary = ProcessWorkspaceProjection.Summarize(rows, visible);

        Assert.Equal(3, summary.TotalProcesses);
        Assert.Equal(2, summary.VisibleProcesses);
        Assert.Equal(2, summary.UserCount);
        Assert.Equal(7_168, summary.ResidentBytes);
        Assert.Equal("7 KiB", summary.ResidentMemoryText);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(1024, "1 KiB")]
    [InlineData(1048576, "1 MiB")]
    [InlineData(-1, "0 B")]
    public void FormatBytesUsesStableBinaryUnits(long bytes, string expected)
    {
        Assert.Equal(expected, ProcessWorkspaceProjection.FormatBytes(bytes));
    }
}
