using System.Security.Cryptography;
using System.Text;
using ServerDesk.Application.Agent;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Services;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;
using Xunit;

namespace ServerDesk.Tests;

public sealed class AgentLifecycleSafetyAcceptanceTests
{
    [Fact]
    public async Task StagedLengthMismatchFailsBeforePrivilegedActivation()
    {
        var artifact = CreateArtifact("2.0.0", "length-mismatch-agent");
        var plan = AgentLifecyclePlanner.PlanInstall(artifact);
        var stagedBinary = AgentLifecycleRemoteLayout.GetStagedBinary(plan.PlanId).Value;
        var commands = new RecordingCommandFactory(spec =>
        {
            if (spec.Executable == "uname")
            {
                return Success("x86_64\n");
            }

            if (spec.Executable == "stat" && spec.Arguments[^1] == stagedBinary)
            {
                return Success((artifact.Length + 1).ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            }

            if (spec.Executable == "sha256sum" && spec.Arguments[^1] == stagedBinary)
            {
                return Success(artifact.Manifest.ArtifactSha256 + "  " + stagedBinary + "\n");
            }

            return Success();
        });
        var services = new FakeServiceManager([AbsentService()]);
        var lifecycle = CreateService(commands, services, artifact.Manifest.Version);

        var result = await lifecycle.InstallAsync(
            CreateProfile(),
            artifact,
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentLifecycleExecutionState.Failed, result.State);
        Assert.DoesNotContain(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.Count >= 2 &&
            (spec.Arguments[^1] == AgentLifecyclePlanner.BinaryPath ||
             spec.Arguments[^1] == AgentLifecyclePlanner.ServiceUnitPath));
        Assert.DoesNotContain(services.Actions, action =>
            action is ServerServiceAction.Enable or ServerServiceAction.Start);
    }

    [Fact]
    public async Task InstallCommandsCarryExactRiskLocaleAndNonInteractiveSudoBoundary()
    {
        var artifact = CreateArtifact("2.0.0", "risk-fixture-agent");
        var plan = AgentLifecyclePlanner.PlanInstall(artifact);
        var commands = new RecordingCommandFactory(spec => InstallCommand(spec, artifact));
        var services = new FakeServiceManager([AbsentService(), ActiveServiceResult()]);
        var lifecycle = CreateService(commands, services, artifact.Manifest.Version);

        var result = await lifecycle.InstallAsync(
            CreateProfile(),
            artifact,
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentLifecycleExecutionState.Succeeded, result.State);
        Assert.All(commands.Commands, spec => Assert.Equal("C", spec.Environment?["LC_ALL"]));
        Assert.All(
            commands.Commands.Where(spec => spec.Executable == "sudo"),
            spec => Assert.Equal("-n", spec.Arguments[0]));
        Assert.All(
            commands.Commands.Where(spec => spec.Executable is "uname" or "stat" or "sha256sum"),
            spec => Assert.Equal(OperationRisk.ReadOnly, spec.Risk));

        var activation = Assert.Single(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.SequenceEqual([
                "-n", "install", "-m", "0755", "--",
                AgentLifecycleRemoteLayout.GetStagedBinary(plan.PlanId).Value,
                AgentLifecyclePlanner.BinaryPath,
            ]));
        Assert.Equal(OperationRisk.Destructive, activation.Risk);

        Assert.All(
            commands.Commands.Where(spec =>
                spec.Executable == "sudo" &&
                !ReferenceEquals(spec, activation)),
            spec => Assert.Equal(OperationRisk.Mutating, spec.Risk));
        Assert.DoesNotContain(commands.Commands, spec =>
            spec.Executable is "/bin/sh" or "sh" or "bash" || spec.Arguments.Contains("-lc"));
    }

    [Fact]
    public async Task DeterministicUpdateRestartFailurePerformsExactlyOneBoundedRollbackAndVerifiesPreviousVersion()
    {
        var previous = CreateArtifact("1.0.0", "previous-agent");
        var target = CreateArtifact("2.0.0", "target-agent");
        var plan = AgentLifecyclePlanner.PlanUpdate(previous.Manifest.Version, target);
        var stagedBinary = AgentLifecycleRemoteLayout.GetStagedBinary(plan.PlanId).Value;
        var binaryContainsTarget = false;
        var commands = new RecordingCommandFactory(spec =>
        {
            if (spec.Executable == "uname")
            {
                return Success("x86_64\n");
            }

            if (spec.Executable == "sudo" && spec.Arguments.Count >= 2)
            {
                if (spec.Arguments[^2] == stagedBinary && spec.Arguments[^1] == AgentLifecyclePlanner.BinaryPath)
                {
                    binaryContainsTarget = true;
                }
                else if (spec.Arguments[^2] == AgentLifecycleRemoteLayout.RollbackBinaryPath &&
                         spec.Arguments[^1] == AgentLifecyclePlanner.BinaryPath)
                {
                    binaryContainsTarget = false;
                }

                return Success();
            }

            if (spec.Executable == "stat")
            {
                var path = spec.Arguments[^1];
                var length = path switch
                {
                    var value when value == stagedBinary => target.Length,
                    var value when value == AgentLifecycleRemoteLayout.RollbackBinaryPath => previous.Length,
                    var value when value == AgentLifecyclePlanner.BinaryPath && binaryContainsTarget => target.Length,
                    _ => previous.Length,
                };
                return Success(length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            }

            if (spec.Executable == "sha256sum")
            {
                var path = spec.Arguments[^1];
                var digest = path switch
                {
                    var value when value == stagedBinary => target.Manifest.ArtifactSha256,
                    var value when value == AgentLifecycleRemoteLayout.RollbackBinaryPath => previous.Manifest.ArtifactSha256,
                    var value when value == AgentLifecyclePlanner.BinaryPath && binaryContainsTarget => target.Manifest.ArtifactSha256,
                    _ => previous.Manifest.ArtifactSha256,
                };
                return Success(digest + "  " + path + "\n");
            }

            return Success();
        });
        var restartCalls = 0;
        var services = new FakeServiceManager(
            [ActiveServiceResult(), ActiveServiceResult()],
            action =>
            {
                if (action != ServerServiceAction.Restart)
                {
                    return SuccessfulAction();
                }

                restartCalls++;
                return restartCalls == 1
                    ? new ServerServiceActionResult(
                        false,
                        new RemoteError(RemoteErrorCode.CommandFailed, "deterministic fixture restart failure"),
                        "deterministic fixture restart failure")
                    : SuccessfulAction();
            });
        var lifecycle = CreateService(commands, services, previous.Manifest.Version);

        var result = await lifecycle.UpdateAsync(
            CreateProfile(),
            target,
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentLifecycleExecutionState.RolledBack, result.State);
        Assert.Equal(previous.Manifest.Version, result.Status?.Version);
        Assert.Equal(2, restartCalls);
        Assert.Single(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.Count >= 2 &&
            spec.Arguments[^2] == AgentLifecyclePlanner.BinaryPath &&
            spec.Arguments[^1] == AgentLifecycleRemoteLayout.RollbackBinaryPath);
        Assert.Single(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.Count >= 2 &&
            spec.Arguments[^2] == AgentLifecycleRemoteLayout.RollbackBinaryPath &&
            spec.Arguments[^1] == AgentLifecyclePlanner.BinaryPath);
        Assert.DoesNotContain(
            commands.Commands.SelectMany(spec => spec.Arguments),
            argument => argument.Contains("previous.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusSanitizesRemoteTechnicalErrorText()
    {
        const string secretSentinel = "PRIVATE-SENTINEL-DO-NOT-EXPOSE";
        var services = new FakeServiceManager([
            new ServerServiceQueryResult(
                [],
                new RemoteError(RemoteErrorCode.NetworkInterrupted, secretSentinel)),
        ]);
        var commands = new RecordingCommandFactory(_ => Success());
        var lifecycle = CreateService(commands, services, new AgentReleaseVersion(1, 0, 0));

        var status = await lifecycle.GetStatusAsync(
            CreateProfile(),
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentLifecycleStatusKind.Ambiguous, status.Kind);
        Assert.DoesNotContain(secretSentinel, status.Message, StringComparison.Ordinal);
        Assert.Equal(0, commands.CreateCount);
    }

    private static AgentLifecycleService CreateService(
        IRemoteCommandExecutorFactory commandFactory,
        IServerServiceManager serviceManager,
        AgentReleaseVersion runtimeVersion) =>
        new(
            commandFactory,
            new RecordingFileSystemFactory(),
            serviceManager,
            new FakeTunnelFactory(),
            new FakeTransportFactory(runtimeVersion));

    private static VerifiedAgentArtifact CreateArtifact(string version, string content)
    {
        Assert.True(AgentReleaseVersion.TryParse(version, out var parsed));
        var bytes = Encoding.UTF8.GetBytes(content);
        var manifest = new VerifiedAgentReleaseManifest(
            parsed,
            AgentProtocolVersion.Current,
            "x64",
            "serverdesk-agent-linux-x64",
            bytes.LongLength,
            Convert.ToHexStringLower(SHA256.HashData(bytes)),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            "test-release-key");
        return new VerifiedAgentArtifact(manifest, bytes);
    }

    private static ServerProfile CreateProfile() =>
        ServerProfile.Create("Agent lifecycle safety fixture", "example.test", 22, "serverdesk_ci");

    private static ServerServiceInfo ActiveService() =>
        new(
            AgentLifecyclePlanner.ServiceUnit,
            "ServerDesk optional realtime agent",
            "loaded",
            "active",
            "running",
            "enabled",
            1234,
            string.Empty);

    private static ServerServiceQueryResult ActiveServiceResult() => new([ActiveService()], null);

    private static ServerServiceQueryResult AbsentService() =>
        new([], new RemoteError(RemoteErrorCode.PathNotFound, "fixture unit absent"));

    private static ServerServiceActionResult SuccessfulAction() =>
        new(true, null, "fixture success", ActiveService());

    private static RemoteExecutionResult InstallCommand(RemoteCommandSpec spec, VerifiedAgentArtifact artifact)
    {
        if (spec.Executable == "uname")
        {
            return Success("x86_64\n");
        }

        if (spec.Executable == "stat")
        {
            var path = spec.Arguments[^1];
            var length = path.EndsWith(".service", StringComparison.Ordinal)
                ? AgentSystemdUnitDefinition.Bytes.LongLength
                : artifact.Length;
            return Success(length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
        }

        if (spec.Executable == "sha256sum")
        {
            var path = spec.Arguments[^1];
            var digest = path.EndsWith(".service", StringComparison.Ordinal)
                ? AgentSystemdUnitDefinition.Sha256
                : artifact.Manifest.ArtifactSha256;
            return Success(digest + "  " + path + "\n");
        }

        return Success();
    }

    private static RemoteExecutionResult Success(
        string standardOutput = "",
        string standardError = "") =>
        RemoteExecutionResult.Success(new RemoteCommandResult(
            0,
            standardOutput,
            standardError,
            TimeSpan.FromMilliseconds(1)));

    private sealed class RecordingCommandFactory : IRemoteCommandExecutorFactory
    {
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public RecordingCommandFactory(Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            _handler = handler;
        }

        public int CreateCount { get; private set; }

        public List<RemoteCommandSpec> Commands { get; } = [];

        public IRemoteCommandExecutor Create(ServerProfile profile)
        {
            CreateCount++;
            return new RecordingCommandExecutor(profile.Id, Commands, _handler);
        }
    }

    private sealed class RecordingCommandExecutor : IRemoteCommandExecutor
    {
        private readonly List<RemoteCommandSpec> _commands;
        private readonly Func<RemoteCommandSpec, RemoteExecutionResult> _handler;

        public RecordingCommandExecutor(
            Guid profileId,
            List<RemoteCommandSpec> commands,
            Func<RemoteCommandSpec, RemoteExecutionResult> handler)
        {
            ServerProfileId = profileId;
            _commands = commands;
            _handler = handler;
        }

        public Guid ServerProfileId { get; }

        public Task<RemoteExecutionResult> ExecuteAsync(
            RemoteCommandSpec command,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _commands.Add(command);
            return Task.FromResult(_handler(command));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingFileSystemFactory : IRemoteFileSystemFactory
    {
        public IRemoteFileSystem Create(ServerProfile profile) => new RecordingFileSystem(profile.Id);
    }

    private sealed class RecordingFileSystem : IRemoteFileSystem
    {
        public RecordingFileSystem(Guid profileId)
        {
            ServerProfileId = profileId;
        }

        public Guid ServerProfileId { get; }

        public bool IsConnected { get; private set; }

        public ValueTask ConnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = true;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisconnectAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IsConnected = false;
            return ValueTask.CompletedTask;
        }

        public async ValueTask UploadAsync(
            Stream source,
            RemotePath destination,
            long? totalBytes = null,
            bool overwrite = false,
            IProgress<RemoteTransferProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await source.CopyToAsync(Stream.Null, cancellationToken);
        }

        public ValueTask<IReadOnlyList<RemoteFileEntry>> ListAsync(RemotePath path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<RemoteFileEntry> StatAsync(RemotePath path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask CreateDirectoryAsync(RemotePath path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask RenameAsync(RemotePath source, RemotePath destination, bool overwrite = false, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteFileAsync(RemotePath path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DeleteDirectoryAsync(RemotePath path, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask SetPermissionsAsync(RemotePath path, RemoteUnixPermissions permissions, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DownloadAsync(RemotePath source, Stream destination, IProgress<RemoteTransferProgress>? progress = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask DisposeAsync()
        {
            IsConnected = false;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeServiceManager : IServerServiceManager
    {
        private readonly Queue<ServerServiceQueryResult> _reads;
        private readonly Func<ServerServiceAction, ServerServiceActionResult> _action;

        public FakeServiceManager(
            IEnumerable<ServerServiceQueryResult> reads,
            Func<ServerServiceAction, ServerServiceActionResult>? action = null)
        {
            _reads = new Queue<ServerServiceQueryResult>(reads);
            _action = action ?? (_ => SuccessfulAction());
        }

        public List<ServerServiceAction> Actions { get; } = [];

        public Task<ServerServiceQueryResult> ListAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServerServiceQueryResult> GetAsync(
            ServerProfile profile,
            string unit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(AgentLifecyclePlanner.ServiceUnit, unit);
            if (_reads.Count == 0)
            {
                throw new InvalidOperationException("Fixture has no remaining systemd read result.");
            }

            return Task.FromResult(_reads.Dequeue());
        }

        public Task<ServerServiceActionResult> ExecuteAsync(
            ServerProfile profile,
            string unit,
            ServerServiceAction action,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(AgentLifecyclePlanner.ServiceUnit, unit);
            Actions.Add(action);
            return Task.FromResult(_action(action));
        }
    }

    private sealed class FakeTunnelFactory : IAgentTunnelSessionFactory
    {
        public IAgentTunnelSession Create(ServerProfile serverProfile, int agentPort)
        {
            Assert.Equal(AgentLifecycleRemoteLayout.AgentPort, agentPort);
            return new FakeTunnel();
        }
    }

    private sealed class FakeTunnel : IAgentTunnelSession
    {
        public AgentTunnelState State { get; private set; } = AgentTunnelState.Created;

        public int LocalPort => State == AgentTunnelState.Active ? 54321 : 0;

        public ValueTask StartAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = AgentTunnelState.Active;
            return ValueTask.CompletedTask;
        }

        public ValueTask StopAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = AgentTunnelState.Stopped;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            State = AgentTunnelState.Stopped;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTransportFactory : IAgentTransportClientFactory
    {
        private readonly AgentReleaseVersion _version;

        public FakeTransportFactory(AgentReleaseVersion version)
        {
            _version = version;
        }

        public IAgentTransportClient Create(int localPort)
        {
            Assert.Equal(54321, localPort);
            return new FakeTransport(_version);
        }
    }

    private sealed class FakeTransport : IAgentTransportClient
    {
        private readonly AgentReleaseVersion _version;

        public FakeTransport(AgentReleaseVersion version)
        {
            _version = version;
        }

        public ValueTask<AgentPeerInfo> NegotiateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AgentPeerInfo(
                AgentProtocolVersion.Current,
                _version.ToString(),
                new HashSet<AgentCapability>(AgentCompatibilityPolicy.KnownCapabilities),
                "linux",
                "x64"));
        }

        public ValueTask<AgentHealthSnapshot> GetHealthAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AgentHealthSnapshot(
                AgentConnectionState.Available,
                DateTimeOffset.UtcNow,
                _version.ToString()));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
