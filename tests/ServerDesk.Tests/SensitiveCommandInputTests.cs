using System.Text;
using ServerDesk.Application.Remote;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Tests;

public sealed class SensitiveCommandInputTests
{
    [Fact]
    public async Task SensitiveInputWritesExactUtf8ButNeverAppearsInDisplayOrCommandLine()
    {
        const string secret = "db-secret-'quoted'-$HOME-\u03a9\nsecond-line";
        var input = new SensitiveCommandInput(secret);
        var spec = new RemoteCommandSpec(
            "sha256sum",
            [],
            TimeSpan.FromSeconds(5),
            StandardInput: input);

        await using var destination = new MemoryStream();
        await input.WriteToAsync(destination, TestContext.Current.CancellationToken);

        Assert.Equal(secret, Encoding.UTF8.GetString(destination.ToArray()));
        Assert.DoesNotContain(secret, input.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, spec.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, PosixCommandLine.Build(spec), StringComparison.Ordinal);
        Assert.Contains("redacted", input.ToString(), StringComparison.OrdinalIgnoreCase);
    }
}
