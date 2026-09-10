using ServerDesk.Application.Remote;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Tests;

public sealed class PosixCommandLineSecurityTests
{
    [Fact]
    public void BuildRejectsNulInEveryQuotedCommandToken()
    {
        var timeout = TimeSpan.FromSeconds(5);

        Assert.Throws<ArgumentException>(() => PosixCommandLine.Build(
            new RemoteCommandSpec("pri\0ntf", ["safe"], timeout)));

        Assert.Throws<ArgumentException>(() => PosixCommandLine.Build(
            new RemoteCommandSpec("printf", ["unsafe\0argument"], timeout)));

        Assert.Throws<ArgumentException>(() => PosixCommandLine.Build(
            new RemoteCommandSpec(
                "printf",
                ["safe"],
                timeout,
                Environment: new Dictionary<string, string>
                {
                    ["SAFE_VALUE"] = "unsafe\0environment",
                })));

        Assert.Throws<ArgumentException>(() => PosixCommandLine.Build(
            new RemoteCommandSpec(
                "printf",
                ["safe"],
                timeout,
                WorkingDirectory: "/tmp/unsafe\0directory")));
    }
}
