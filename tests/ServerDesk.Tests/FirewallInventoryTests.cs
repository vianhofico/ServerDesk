using System.Text.Json;
using ServerDesk.Application.Firewall;
using ServerDesk.Application.Remote;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class FirewallInventoryTests
{
    [Theory]
    [InlineData("ubuntu-24.04.json", FirewallAdapterKind.Ufw)]
    [InlineData("ubuntu-26.04.json", FirewallAdapterKind.Ufw)]
    [InlineData("debian-13.json", FirewallAdapterKind.Firewalld)]
    public void CertifiedFixturesNormalizeRules(string fixtureName, FirewallAdapterKind adapter)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Fixture(fixtureName)));
        var root = fixture.RootElement;

        var observation = adapter == FirewallAdapterKind.Ufw
            ? FirewallParser.ParseUfw(
                root.GetProperty("version").GetString()!,
                root.GetProperty("status").GetString()!,
                FirewallInventoryOptions.Default)
            : FirewallParser.ParseFirewalld(
                root.GetProperty("version").GetString()!,
                root.GetProperty("state").GetString()!,
                root.GetProperty("zones").GetString()!,
                FirewallInventoryOptions.Default);

        Assert.True(observation.IsActive);
        Assert.Equal(adapter, observation.Adapter);
        Assert.NotEmpty(observation.Rules);
        Assert.All(observation.Rules, rule =>
        {
            Assert.Equal(adapter, rule.Adapter);
            Assert.False(string.IsNullOrWhiteSpace(rule.Id));
            Assert.False(string.IsNullOrWhiteSpace(rule.PortOrService));
            Assert.False(string.IsNullOrWhiteSpace(rule.Raw));
        });
    }

    [Fact]
    public void UfwParserKeepsDirectionActionProtocolAndSource()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Fixture("ubuntu-24.04.json")));
        var observation = FirewallParser.ParseUfw(
            fixture.RootElement.GetProperty("version").GetString()!,
            fixture.RootElement.GetProperty("status").GetString()!,
            FirewallInventoryOptions.Default);

        var ssh = Assert.Single(observation.Rules, rule => rule.PortOrService == "22");
        Assert.Equal(FirewallRuleAction.Allow, ssh.Action);
        Assert.Equal(FirewallRuleDirection.Inbound, ssh.Direction);
        Assert.Equal("tcp", ssh.Protocol);
        Assert.Equal("10.20.0.0/16", ssh.Source);

        var dns = Assert.Single(observation.Rules, rule => rule.PortOrService == "53");
        Assert.Equal(FirewallRuleAction.Deny, dns.Action);
        Assert.Equal(FirewallRuleDirection.Outbound, dns.Direction);
        Assert.Equal("udp", dns.Protocol);
    }

    [Fact]
    public void FirewalldParserNormalizesServicesPortsZonesAndSources()
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Fixture("debian-13.json")));
        var observation = FirewallParser.ParseFirewalld(
            fixture.RootElement.GetProperty("version").GetString()!,
            fixture.RootElement.GetProperty("state").GetString()!,
            fixture.RootElement.GetProperty("zones").GetString()!,
            FirewallInventoryOptions.Default);

        var ssh = Assert.Single(observation.Rules, rule => rule.PortOrService == "ssh");
        Assert.Equal("public", ssh.Zone);
        Assert.Equal("10.40.0.0/16", ssh.Source);
        Assert.Equal(FirewallRuleDirection.Inbound, ssh.Direction);

        var custom = Assert.Single(observation.Rules, rule => rule.PortOrService == "9000");
        Assert.Equal("trusted", custom.Zone);
        Assert.Equal("tcp", custom.Protocol);
        Assert.Equal("192.0.2.0/24", custom.Source);
    }

    [Fact]
    public async Task MissingBothClisIsDistinctAndAllProbesAreReadOnly()
    {
        var state = new FakeState
        {
            UfwMissing = true,
            FirewalldMissing = true,
        };
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(FirewallRuntimeStatus.CliUnavailable, result.Snapshot!.Status);
        Assert.Equal(FirewallAdapterKind.None, result.Snapshot.ActiveAdapter);
        Assert.Equal(2, state.Commands.Count);
        Assert.All(state.Commands, AssertReadOnlyStableLocale);
    }

    [Fact]
    public async Task PermissionDeniedIsNotReportedAsEmptyInventory()
    {
        var state = new FakeState
        {
            UfwStatusError = new RemoteError(RemoteErrorCode.PermissionDenied, "permission denied"),
            FirewalldMissing = true,
        };
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(FirewallRuntimeStatus.PermissionDenied, result.Snapshot!.Status);
        Assert.Equal(FirewallAdapterKind.Ufw, result.Snapshot.ActiveAdapter);
        Assert.Empty(result.Snapshot.Rules);
    }

    [Fact]
    public async Task BothActiveAdaptersProduceConflictInsteadOfGuessing()
    {
        var state = new FakeState
        {
            UfwStatus = "Status: active\n[ 1] 22/tcp                     ALLOW IN    Anywhere",
            FirewalldState = "running",
            FirewalldStateExitCode = 0,
            FirewalldZones = "public (active)\n  services: ssh\n  ports:\n  sources:",
        };
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(FirewallRuntimeStatus.AdapterConflict, result.Snapshot!.Status);
        Assert.Equal(FirewallAdapterKind.None, result.Snapshot.ActiveAdapter);
        Assert.Empty(result.Snapshot.Rules);
    }

    [Fact]
    public async Task ActiveUfwWinsWhenFirewalldCliIsPresentButStopped()
    {
        var state = new FakeState
        {
            UfwStatus = "Status: active\n[ 1] 22/tcp                     ALLOW IN    Anywhere",
            FirewalldState = "not running",
            FirewalldStateExitCode = 252,
        };
        var service = CreateService(state);

        var result = await service.InspectAsync(Profile(), TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(FirewallRuntimeStatus.Available, result.Snapshot!.Status);
        Assert.Equal(FirewallAdapterKind.Ufw, result.Snapshot.ActiveAdapter);
        Assert.Single(result.Snapshot.Rules);
        Assert.All(state.Commands, AssertReadOnlyStableLocale);
    }

    private static void AssertReadOnlyStableLocale(RemoteCommandSpec command)
    {
        Assert.Equal(OperationRisk.ReadOnly, command.Risk);
        Assert.NotNull(command.Environment);
        Assert.Equal("C", command.Environment["LC_ALL"]);
    }

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Firewall", name);

    private static FirewallInventoryService CreateService(FakeState state) =>
        new(new FakeCommandFactory(state), FirewallInventoryOptions.Default);

    private static ServerProfile Profile() =>
        ServerProfile.Create("Firewall", "example.invalid", 22, "dev");

    private sealed class FakeState
    {
        public List<RemoteCommandSpec> Commands { get; } = [];
        public bool UfwMissing { get; set; }
        public string UfwVersion { get; set; } = "ufw 0.36.2";
        public int UfwVersionExitCode { get; set; }
        public string UfwStatus { get; set; } = "Status: inactive";
        public int UfwStatusExitCode { get; set; }
        public RemoteError? UfwStatusError { get; set; }
        public bool FirewalldMissing { get; set; }
        public string FirewalldVersion { get; set; } = "2.3.0";
        public int FirewalldVersionExitCode { get; set; }
        public string FirewalldState { get; set; } = "not running";
        public int FirewalldStateExitCode { get; set; } = 252;
        public RemoteError? FirewalldStateError { get; set; }
        public string FirewalldZones { get; set; } = string.Empty;
        public int FirewalldZonesExitCode { get; set; }
        public RemoteError? FirewalldZonesError { get; set; }
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

            if (command.Executable == "ufw" && command.Arguments.SequenceEqual(["--version"]))
            {
                return Task.FromResult(_state.UfwMissing
                    ? RemoteExecutionResult.Failure(new RemoteError(RemoteErrorCode.CommandNotFound, "ufw missing"))
                    : Success(_state.UfwVersionExitCode, _state.UfwVersion));
            }

            if (command.Executable == "ufw" && command.Arguments.SequenceEqual(["status", "numbered"]))
            {
                return Task.FromResult(_state.UfwStatusError is not null
                    ? RemoteExecutionResult.Failure(_state.UfwStatusError)
                    : Success(_state.UfwStatusExitCode, _state.UfwStatus));
            }

            if (command.Executable == "firewall-cmd" && command.Arguments.SequenceEqual(["--version"]))
            {
                return Task.FromResult(_state.FirewalldMissing
                    ? RemoteExecutionResult.Failure(new RemoteError(RemoteErrorCode.CommandNotFound, "firewalld missing"))
                    : Success(_state.FirewalldVersionExitCode, _state.FirewalldVersion));
            }

            if (command.Executable == "firewall-cmd" && command.Arguments.SequenceEqual(["--state"]))
            {
                return Task.FromResult(_state.FirewalldStateError is not null
                    ? RemoteExecutionResult.Failure(_state.FirewalldStateError)
                    : Success(_state.FirewalldStateExitCode, _state.FirewalldState));
            }

            if (command.Executable == "firewall-cmd" && command.Arguments.SequenceEqual(["--list-all-zones"]))
            {
                return Task.FromResult(_state.FirewalldZonesError is not null
                    ? RemoteExecutionResult.Failure(_state.FirewalldZonesError)
                    : Success(_state.FirewalldZonesExitCode, _state.FirewalldZones));
            }

            throw new InvalidOperationException($"Unexpected firewall command: {command.Executable} {string.Join(' ', command.Arguments)}");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static RemoteExecutionResult Success(int exitCode, string stdout) =>
            RemoteExecutionResult.Success(new RemoteCommandResult(exitCode, stdout, string.Empty, TimeSpan.Zero));
    }
}
