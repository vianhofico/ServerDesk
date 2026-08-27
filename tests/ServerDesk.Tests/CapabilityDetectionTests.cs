using ServerDesk.Application.Capabilities;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;
using ServerDesk.Infrastructure.Ssh;
using Xunit;

namespace ServerDesk.Tests;

public sealed class CapabilityDetectionTests
{
    [Theory]
    [InlineData("ubuntu-24.04-os-release.txt", "ubuntu", "Ubuntu", "24.04", "Ubuntu 24.04.3 LTS")]
    [InlineData("ubuntu-26.04-os-release.txt", "ubuntu", "Ubuntu", "26.04", "Ubuntu 26.04 LTS")]
    [InlineData("debian-13-os-release.txt", "debian", "Debian GNU/Linux", "13", "Debian GNU/Linux 13 (trixie)")]
    [InlineData("custom-linux-os-release.txt", "example-appliance", "Example Appliance Linux", "42", "Example Appliance Linux 42")]
    public void OsReleaseFixturesParseWithoutDistroSpecificBranches(
        string fileName,
        string expectedId,
        string expectedName,
        string expectedVersion,
        string expectedPrettyName)
    {
        var values = OsReleaseParser.Parse(LoadFixture(fileName));

        Assert.Equal(expectedId, values["ID"]);
        Assert.Equal(expectedName, values["NAME"]);
        Assert.Equal(expectedVersion, values["VERSION_ID"]);
        Assert.Equal(expectedPrettyName, values["PRETTY_NAME"]);
    }

    [Fact]
    public async Task ScannerKeepsUnavailablePermissionDeniedAndUnknownDistinctOnUnknownDistro()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Unknown distro", "example.invalid", 22, "operator");
        var factory = new FakeCommandExecutorFactory(spec => RespondToClassificationScenario(spec));
        await using var service = new ServerCapabilityService(
            factory,
            new ServerCapabilityOptions(TimeSpan.FromMinutes(5)));

        var snapshot = await service.GetAsync(profile, cancellationToken: cancellationToken);

        Assert.Equal("example-appliance", snapshot.Identity.OsId);
        Assert.Equal("Example Appliance Linux 42", snapshot.Identity.DisplayName);
        Assert.Equal("x86_64", snapshot.Identity.Architecture);
        Assert.Equal("operator", snapshot.Identity.CurrentUser);
        Assert.False(snapshot.Identity.IsRoot);
        Assert.Equal(CapabilityStatus.Unavailable, snapshot.Docker.Status);
        Assert.Equal(CapabilityStatus.PermissionDenied, snapshot.Nginx.Status);
        Assert.Equal(CapabilityStatus.Unknown, snapshot.Git.Status);
        Assert.Equal(CapabilityStatus.Available, snapshot.Sudo.Status);
        Assert.False(snapshot.Sudo.Passwordless);
    }

    [Fact]
    public async Task CapabilityCacheReusesSnapshotUntilForcedRefresh()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var profile = ServerProfile.Create("Cache", "example.invalid", 22, "operator");
        var factory = new FakeCommandExecutorFactory(RespondToMinimalScenario);
        await using var service = new ServerCapabilityService(
            factory,
            new ServerCapabilityOptions(TimeSpan.FromMinutes(5)));

        var first = await service.GetAsync(profile, cancellationToken: cancellationToken);
        var second = await service.GetAsync(profile, cancellationToken: cancellationToken);
        var forced = await service.GetAsync(profile, forceRefresh: true, cancellationToken);

        Assert.Same(first, second);
        Assert.NotSame(second, forced);
        Assert.Equal(2, factory.CreateCount);

        service.Invalidate(profile.Id);
        await service.GetAsync(profile, cancellationToken: cancellationToken);
        Assert.Equal(3, factory.CreateCount);
    }

    [Fact]
    public void PosixCommandLineQuotesEveryTokenAndEnvironmentValue()
    {
        var command = new RemoteCommandSpec(
            "printf",
            ["hello'; touch /tmp/pwn; echo '"],
            TimeSpan.FromSeconds(5),
            Environment: new Dictionary<string, string>
            {
                ["SAFE_VALUE"] = "$(whoami) 'quoted'",
            },
            WorkingDirectory: "/tmp/a folder; echo bad");

        var built = PosixCommandLine.Build(command);

        Assert.Equal(
            "cd -- '/tmp/a folder; echo bad' && env SAFE_VALUE='$(whoami) '\"'\"'quoted'\"'\"'' 'printf' 'hello'\"'\"'; touch /tmp/pwn; echo '\"'\"''",
            built);
    }

    [Theory]
    [InlineData("SAFE_NAME", true)]
    [InlineData("_SAFE2", true)]
    [InlineData("2BAD", false)]
    [InlineData("BAD-NAME", false)]
    [InlineData("A B", false)]
    public void EnvironmentNameValidationIsExplicit(string name, bool expected)
    {
        Assert.Equal(expected, PosixCommandLine.IsEnvironmentName(name));
    }

    private static RemoteExecutionResult RespondToClassificationScenario(RemoteCommandSpec spec)
    {
        if (IsShellScript(spec, "cat /etc/os-release 2>/dev/null"))
        {
            return Success(0, LoadFixture("custom-linux-os-release.txt"));
        }

        if (IsShellScript(spec, "uname -m"))
        {
            return Success(0, "x86_64\n");
        }

        if (IsShellScript(spec, "uname -r"))
        {
            return Success(0, "6.12.0-test\n");
        }

        if (IsShellScript(spec, "id -un"))
        {
            return Success(0, "operator\n");
        }

        if (IsShellScript(spec, "id -u"))
        {
            return Success(0, "1000\n");
        }

        if (IsCommandLookup(spec, "docker"))
        {
            return Success(1);
        }

        if (IsCommandLookup(spec, "nginx"))
        {
            return Success(0);
        }

        if (spec.Executable == "nginx")
        {
            return Success(126, standardError: "nginx: Permission denied\n");
        }

        if (IsCommandLookup(spec, "git"))
        {
            return Success(0);
        }

        if (spec.Executable == "git")
        {
            return Success(2, standardError: "unexpected git probe failure\n");
        }

        if (IsCommandLookup(spec, "sudo"))
        {
            return Success(0);
        }

        if (spec.Executable == "sudo" && spec.Arguments.SequenceEqual(["--version"]))
        {
            return Success(0, "Sudo version 1.9.16\n");
        }

        if (spec.Executable == "sudo" && spec.Arguments.SequenceEqual(["-n", "true"]))
        {
            return Success(1, standardError: "sudo: a password is required\n");
        }

        if (IsAnyCommandLookup(spec))
        {
            return Success(1);
        }

        return Success(1, standardError: "fixture command unavailable\n");
    }

    private static RemoteExecutionResult RespondToMinimalScenario(RemoteCommandSpec spec)
    {
        if (IsShellScript(spec, "cat /etc/os-release 2>/dev/null"))
        {
            return Success(0, LoadFixture("debian-13-os-release.txt"));
        }

        if (IsShellScript(spec, "uname -m"))
        {
            return Success(0, "aarch64\n");
        }

        if (IsShellScript(spec, "uname -r"))
        {
            return Success(0, "6.12\n");
        }

        if (IsShellScript(spec, "id -un"))
        {
            return Success(0, "dev\n");
        }

        if (IsShellScript(spec, "id -u"))
        {
            return Success(0, "1001\n");
        }

        if (IsAnyCommandLookup(spec))
        {
            return Success(1);
        }

        return Success(1);
    }

    private static bool IsShellScript(RemoteCommandSpec spec, string script) =>
        spec.Executable == "/bin/sh" &&
        spec.Arguments.Count == 2 &&
        spec.Arguments[0] == "-lc" &&
        spec.Arguments[1] == script;

    private static bool IsCommandLookup(RemoteCommandSpec spec, string command) =>
        IsShellScript(spec, $"command -v {command} >/dev/null 2>&1");

    private static bool IsAnyCommandLookup(RemoteCommandSpec spec) =>
        spec.Executable == "/bin/sh" &&
        spec.Arguments.Count == 2 &&
        spec.Arguments[0] == "-lc" &&
        spec.Arguments[1].StartsWith("command -v ", StringComparison.Ordinal);

    private static RemoteExecutionResult Success(
        int exitCode,
        string standardOutput = "",
        string standardError = "") =>
        RemoteExecutionResult.Success(new RemoteCommandResult(
            exitCode,
            standardOutput,
            standardError,
            TimeSpan.FromMilliseconds(1)));

    private static string LoadFixture(string fileName) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Capabilities", fileName));

    private sealed class FakeCommandExecutorFactory : IRemoteCommandExecutorFactory
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public FakeCommandExecutorFactory(Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            _handler = handler;
        }

        public int CreateCount { get; private set; }

        public IRemoteCommandExecutor Create(ServerProfile profile)
        {
            CreateCount++;
            return new FakeCommandExecutor(profile.Id, _handler);
        }
    }

    private sealed class FakeCommandExecutor : IRemoteCommandExecutor
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public FakeCommandExecutor(
            Guid serverProfileId,
            Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            ServerProfileId = serverProfileId;
            _handler = handler;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_handler(command));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
