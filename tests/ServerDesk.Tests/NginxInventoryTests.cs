using ServerDesk.Application.Nginx;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class NginxInventoryTests
{
    [Theory]
    [InlineData("ubuntu-24.04.txt")]
    [InlineData("ubuntu-26.04.txt")]
    [InlineData("debian-13.txt")]
    public void CertifiedFixturesNormalizeLoadedServerBlocks(string fixtureName)
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nginx", fixtureName));

        var parsed = NginxConfigParser.Parse(raw, NginxInventoryOptions.Default);

        Assert.NotEmpty(parsed.Sources);
        Assert.NotEmpty(parsed.Sites);
        Assert.All(parsed.Sites, site => Assert.False(string.IsNullOrWhiteSpace(site.RawBlock)));
    }

    [Fact]
    public void ParserPreservesAdvancedRawAndRedactsProxyUserInfoForPresentation()
    {
        var raw = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nginx", "ubuntu-24.04.txt"));

        var parsed = NginxConfigParser.Parse(raw, NginxInventoryOptions.Default);
        var app = Assert.Single(parsed.Sites, site => site.ServerNames.Contains("app.example.test"));

        Assert.Contains("proxy_set_header Host $host;", app.RawBlock, StringComparison.Ordinal);
        Assert.Contains("http://***@127.0.0.1:5000", app.ProxyTargets);
        Assert.Contains("http://***@127.0.0.1:5000", app.PresentationRawBlock, StringComparison.Ordinal);
        Assert.DoesNotContain("super-secret", app.PresentationRawBlock, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingCliIsDistinctAndNeverMutates()
    {
        var state = new FakeState
        {
            VersionError = new RemoteError(RemoteErrorCode.CommandNotFound, "nginx not found"),
        };
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(NginxRuntimeState.CliMissing, result.Snapshot!.RuntimeState);
        var command = Assert.Single(state.Commands);
        Assert.Equal("nginx", command.Executable);
        Assert.Equal(["-v"], command.Arguments);
        Assert.Equal(OperationRisk.ReadOnly, command.Risk);
    }

    [Fact]
    public async Task InvalidLiveConfigurationIsDistinctAndNotParsedAsEmpty()
    {
        var state = new FakeState
        {
            DumpExitCode = 1,
            DumpError = "nginx: [emerg] unexpected end of file in /etc/nginx/sites-enabled/app.conf:12\nnginx: configuration file /etc/nginx/nginx.conf test failed",
        };
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(NginxRuntimeState.InvalidConfiguration, result.Snapshot!.RuntimeState);
        Assert.Contains("test failed", result.Snapshot.RuntimeDetail, StringComparison.OrdinalIgnoreCase);
        Assert.All(state.Commands, command => Assert.Equal(OperationRisk.ReadOnly, command.Risk));
    }

    [Fact]
    public async Task ServiceUsesExactTypedReadOnlyProbesAndNormalizesInventory()
    {
        var state = new FakeState
        {
            DumpOutput = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Nginx", "ubuntu-24.04.txt")),
        };
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(NginxRuntimeState.Available, result.Snapshot!.RuntimeState);
        Assert.Equal(2, result.Snapshot.Sites.Count);
        Assert.Equal(2, state.Commands.Count);
        Assert.Equal(["-v"], state.Commands[0].Arguments);
        Assert.Equal(["-T"], state.Commands[1].Arguments);
        Assert.All(state.Commands, command =>
        {
            Assert.Equal("nginx", command.Executable);
            Assert.Equal(OperationRisk.ReadOnly, command.Risk);
            Assert.NotNull(command.Environment);
            Assert.Equal("C", command.Environment["LC_ALL"]);
        });
    }

    private static NginxInventoryService CreateService(FakeState state) =>
        new(new FakeCommandFactory(state), NginxInventoryOptions.Default);

    private static ServerProfile Profile() =>
        ServerProfile.Create("nginx", "example.invalid", 22, "dev");

    private sealed class FakeState
    {
        public List<RemoteCommandSpec> Commands { get; } = [];
        public RemoteError? VersionError { get; set; }
        public int VersionExitCode { get; set; }
        public string VersionOutput { get; set; } = string.Empty;
        public string VersionErrorText { get; set; } = "nginx version: nginx/1.26.3";
        public RemoteError? DumpTransportError { get; set; }
        public int DumpExitCode { get; set; }
        public string DumpOutput { get; set; } = string.Empty;
        public string DumpError { get; set; } = "nginx: configuration file /etc/nginx/nginx.conf test is successful";
    }

    private sealed class FakeCommandFactory : IRemoteCommandExecutorFactory
    {
        private readonly FakeState _state;
        public FakeCommandFactory(FakeState state) => _state = state;
        public IRemoteCommandExecutor Create(ServerProfile profile) => new FakeCommandExecutor(profile.Id, _state);
    }

    private sealed class FakeCommandExecutor : IRemoteCommandExecutor
    {
        private readonly FakeState _state;
        public FakeCommandExecutor(Guid serverProfileId, FakeState state)
        {
            ServerProfileId = serverProfileId;
            _state = state;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _state.Commands.Add(command);
            if (command.Arguments.SequenceEqual(["-v"]))
            {
                return Task.FromResult(_state.VersionError is not null
                    ? RemoteExecutionResult.Failure(_state.VersionError)
                    : RemoteExecutionResult.Success(new RemoteCommandResult(
                        _state.VersionExitCode,
                        _state.VersionOutput,
                        _state.VersionErrorText,
                        TimeSpan.Zero)));
            }

            if (command.Arguments.SequenceEqual(["-T"]))
            {
                return Task.FromResult(_state.DumpTransportError is not null
                    ? RemoteExecutionResult.Failure(_state.DumpTransportError)
                    : RemoteExecutionResult.Success(new RemoteCommandResult(
                        _state.DumpExitCode,
                        _state.DumpOutput,
                        _state.DumpError,
                        TimeSpan.Zero)));
            }

            throw new InvalidOperationException("Unexpected nginx command in test.");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
