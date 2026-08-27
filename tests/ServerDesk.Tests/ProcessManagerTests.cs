using ServerDesk.Application.Processes;
using Xunit;

namespace ServerDesk.Tests;

public sealed class ProcessManagerTests
{
    [Fact]
    public void ParserNormalizesStablePsRowsAndPreservesArguments()
    {
        const string output = "  4242  1200 deploy   Ssl  12.5  65536  3723 dotnet /usr/bin/dotnet My App.dll --urls http://127.0.0.1:5000\n" +
                              "    77     1 root     Ss    0.0   2048 99999 sshd sshd: /usr/sbin/sshd -D\n";

        var rows = ServerProcessParser.Parse(output);

        Assert.Equal(2, rows.Count);
        Assert.Equal(4242, rows[0].ProcessId);
        Assert.Equal(1200, rows[0].ParentProcessId);
        Assert.Equal("deploy", rows[0].User);
        Assert.Equal("Ssl", rows[0].State);
        Assert.Equal(12.5, rows[0].CpuPercent);
        Assert.Equal(65536L * 1024, rows[0].ResidentBytes);
        Assert.Equal(3723, rows[0].ElapsedSeconds);
        Assert.Equal("dotnet", rows[0].Command);
        Assert.Equal("/usr/bin/dotnet My App.dll --urls http://127.0.0.1:5000", rows[0].Arguments);
    }

    [Fact]
    public void ParserAcceptsEmptyOutput()
    {
        Assert.Empty(ServerProcessParser.Parse(string.Empty));
    }

    [Fact]
    public void ParserRejectsMalformedRowsInsteadOfGuessing()
    {
        Assert.Throws<FormatException>(() => ServerProcessParser.Parse("not a ps row"));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(1)]
    public void SignalValidationRejectsPidOneAndNonPositiveIds(int processId)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ServerProcessService.ValidateProcessId(processId));
    }

    [Fact]
    public void SignalValidationAllowsOrdinaryPid()
    {
        ServerProcessService.ValidateProcessId(2);
    }
}
