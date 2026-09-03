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

public sealed class AgentLifecycleExecutionTests
{
    [Fact]
    public async Task InstallUsesOnlyFixedTypedCommandsAndVerifiedStaging()
    {
        var artifact = CreateArtifact("2.0.0", "signed-agent-v2");
        var plan = AgentLifecyclePlanner.PlanInstall(artifact);
        var profile = CreateProfile();
        var commands = new RecordingCommandFactory(spec => InstallCommand(spec, artifact));
        var files = new RecordingFileSystemFactory();
        var services = new FakeServiceManager([AbsentService(), ActiveServiceResult()]);
        var lifecycle = CreateService(commands, files, services, artifact.Manifest.Version);

        var result = await lifecycle.InstallAsync(profile, artifact, plan, TestContext.Current.CancellationToken);

        Assert.Equal(AgentLifecycleExecutionState.Succeeded, result.State);
        Assert.NotNull(result.Status);
        Assert.True(result.Status.IsHealthy);
        Assert.Equal(artifact.Manifest.Version, result.Status.Version);
        Assert.Equal(2, files.Uploads.Count);
        Assert.All(files.Uploads, upload => Assert.StartsWith(
            AgentLifecycleRemoteLayout.GetStagingDirectory(plan.PlanId).Value + "/",
            upload.Path.Value,
            StringComparison.Ordinal));

        Assert.Contains(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.SequenceEqual([
                "-n", "install", "-d", "-m", "0711", "--",
                AgentLifecyclePlanner.CacheDirectory,
                AgentLifecycleRemoteLayout.StagingRoot,
            ]));
        Assert.Contains(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.SequenceEqual([
                "-n", "install", "-d", "-m", "0700", "-o", profile.Username, "--",
                AgentLifecycleRemoteLayout.GetStagingDirectory(plan.PlanId).Value,
            ]));
        Assert.Contains(commands.Commands, spec =>
            spec.Executable == "stat" &&
            spec.Arguments.SequenceEqual(["-c", "%s", "--", AgentLifecycleRemoteLayout.GetStagedBinary(plan.PlanId).Value]));
        Assert.Contains(commands.Commands, spec =>
            spec.Executable == "sha256sum" &&
            spec.Arguments.SequenceEqual(["--", AgentLifecycleRemoteLayout.GetStagedBinary(plan.PlanId).Value]));
        Assert.Contains(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.Count >= 2 &&
            spec.Arguments[^1] == AgentLifecyclePlanner.BinaryPath);
        Assert.Contains(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.Count >= 2 &&
            spec.Arguments[^1] == AgentLifecyclePlanner.ServiceUnitPath);

        Assert.DoesNotContain(commands.Commands, spec =>
            spec.Executable is "/bin/sh" or "sh" or "bash" || spec.Arguments.Contains("-lc"));
        Assert.All(commands.Commands, spec => Assert.Equal("C", spec.Environment?["LC_ALL"]));
        Assert.All(
            commands.Commands.Where(spec => spec.Executable == "sudo"),
            spec => Assert.Equal("-n", spec.Arguments[0]));
        Assert.DoesNotContain(
            commands.Commands.SelectMany(spec => spec.Arguments),
            argument =>
                argument.Contains("/etc/ssh", StringComparison.OrdinalIgnoreCase) ||
                argument.Contains("firewall", StringComparison.OrdinalIgnoreCase) ||
                argument.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("/home/", StringComparison.Ordinal));
        Assert.Equal([ServerServiceAction.Enable, ServerServiceAction.Start], services.Actions);
    }

    [Fact]
    public async Task ForgedPlanFailsBeforeAnyRemoteReadOrMutation()
    {
        var artifact = CreateArtifact("2.0.0", "agent");
        var valid = AgentLifecyclePlanner.PlanInstall(artifact);
        var forged = valid with { ServiceUnit = "other.service" };
        var commands = new RecordingCommandFactory(_ => Success());
        var files = new RecordingFileSystemFactory();
        var services = new FakeServiceManager([AbsentService()]);
        var lifecycle = CreateService(commands, files, services, artifact.Manifest.Version);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            lifecycle.InstallAsync(CreateProfile(), artifact, forged, TestContext.Current.CancellationToken));

        Assert.Equal(0, services.GetCalls);
        Assert.Equal(0, commands.CreateCount);
        Assert.Empty(files.Uploads);
    }

    [Fact]
    public async Task MutatedVerifiedArtifactFailsClosedBeforeRemoteAccess()
    {
        var artifact = CreateArtifact("2.0.0", "agent");
        var plan = AgentLifecyclePlanner.PlanInstall(artifact);
        artifact.Content[0] ^= 0x01;
        var commands = new RecordingCommandFactory(_ => Success());
        var files = new RecordingFileSystemFactory();
        var services = new FakeServiceManager([AbsentService()]);
        var lifecycle = CreateService(commands, files, services, artifact.Manifest.Version);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            lifecycle.InstallAsync(CreateProfile(), artifact, plan, TestContext.Current.CancellationToken));

        Assert.Equal(0, services.GetCalls);
        Assert.Equal(0, commands.CreateCount);
        Assert.Empty(files.Uploads);
    }

    [Fact]
    public async Task StageDigestMismatchPreventsPrivilegedActivation()
    {
        var artifact = CreateArtifact("2.0.0", "signed-agent-v2");
        var plan = AgentLifecyclePlanner.PlanInstall(artifact);
        var profile = CreateProfile();
        var stagedBinary = AgentLifecycleRemoteLayout.GetStagedBinary(plan.PlanId).Value;
        var commands = new RecordingCommandFactory(spec =>
        {
            if (spec.Executable == "sha256sum" && spec.Arguments[^1] == stagedBinary)
            {
                return Success(new string('0', 64) + "  " + stagedBinary + "\n");
            }

            return InstallCommand(spec, artifact);
        });
        var files = new RecordingFileSystemFactory();
        var services = new FakeServiceManager([AbsentService()]);
        var lifecycle = CreateService(commands, files, services, artifact.Manifest.Version);

        var result = await lifecycle.InstallAsync(profile, artifact, plan, TestContext.Current.CancellationToken);

        Assert.Equal(AgentLifecycleExecutionState.Failed, result.State);
        Assert.DoesNotContain(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.Count >= 2 &&
            (spec.Arguments[^1] == AgentLifecyclePlanner.BinaryPath ||
             spec.Arguments[^1] == AgentLifecyclePlanner.ServiceUnitPath));
        Assert.Contains(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.Contains("rm") &&
            spec.Arguments[^1] == AgentLifecycleRemoteLayout.GetStagingDirectory(plan.PlanId).Value);
        Assert.Empty(services.Actions);
    }

    [Fact]
    public async Task NetworkLossOnFirstMutationIsAmbiguousAndNeverRetried()
    {
        var artifact = CreateArtifact("2.0.0", "agent");
        var plan = AgentLifecyclePlanner.PlanInstall(artifact);
        var commands = new RecordingCommandFactory(spec =>
        {
            if (spec.Executable == "uname")
            {
                return Success("x86_64\n");
            }

            if (spec.Executable == "sudo")
            {
                return RemoteExecutionResult.Failure(new RemoteError(
                    RemoteErrorCode.NetworkInterrupted,
                    "fixture interruption"));
            }

            return Success();
        });
        var lifecycle = CreateService(
            commands,
            new RecordingFileSystemFactory(),
            new FakeServiceManager([AbsentService()]),
            artifact.Manifest.Version);

        var result = await lifecycle.InstallAsync(
            CreateProfile(),
            artifact,
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentLifecycleExecutionState.Ambiguous, result.State);
        Assert.Equal(RemoteErrorCode.AmbiguousState, result.Error?.Code);
        Assert.Single(commands.Commands.Where(spec => spec.Executable == "sudo"));
    }

    [Fact]
    public async Task UnsafeSshAccountNameNeverReachesPrivilegedOwnerArgument()
    {
        var artifact = CreateArtifact("2.0.0", "agent");
        var plan = AgentLifecyclePlanner.PlanInstall(artifact);
        var commands = new RecordingCommandFactory(spec =>
            spec.Executable == "uname" ? Success("x86_64\n") : Success());
        var lifecycle = CreateService(
            commands,
            new RecordingFileSystemFactory(),
            new FakeServiceManager([AbsentService()]),
            artifact.Manifest.Version);
        var profile = ServerProfile.Create("fixture", "example.test", 22, "bad.name");

        var result = await lifecycle.InstallAsync(profile, artifact, plan, TestContext.Current.CancellationToken);

        Assert.Equal(AgentLifecycleExecutionState.Failed, result.State);
        Assert.DoesNotContain(commands.Commands, spec => spec.Executable == "sudo");
        Assert.Empty(commands.Commands.SelectMany(spec => spec.Arguments).Where(value => value == "bad.name"));
    }

    [Fact]
    public async Task AmbiguousRestartAfterUpdateDoesNotBlindRollback()
    {
        var oldArtifact = CreateArtifact("1.0.0", "old-agent");
        var target = CreateArtifact("2.0.0", "new-agent");
        var plan = AgentLifecyclePlanner.PlanUpdate(oldArtifact.Manifest.Version, target);
        var profile = CreateProfile();
        var oldDigest = oldArtifact.Manifest.ArtifactSha256;
        var oldLength = oldArtifact.Length;
        var targetActivated = false;
        var commands = new RecordingCommandFactory(spec =>
        {
            if (spec.Executable == "uname")
            {
                return Success("x86_64\n");
            }

            if (spec.Executable == "sudo")
            {
                if (spec.Arguments.Count >= 2 &&
                    spec.Arguments[^2] == AgentLifecycleRemoteLayout.GetStagedBinary(plan.PlanId).Value &&
                    spec.Arguments[^1] == AgentLifecyclePlanner.BinaryPath)
                {
                    targetActivated = true;
                }

                return Success();
            }

            if (spec.Executable == "stat")
            {
                var path = spec.Arguments[^1];
                var length = path == AgentLifecycleRemoteLayout.GetStagedBinary(plan.PlanId).Value ||
                             (path == AgentLifecyclePlanner.BinaryPath && targetActivated)
                    ? target.Length
                    : oldLength;
                return Success(length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\n");
            }

            if (spec.Executable == "sha256sum")
            {
                var path = spec.Arguments[^1];
                var digest = path == AgentLifecycleRemoteLayout.GetStagedBinary(plan.PlanId).Value ||
                             (path == AgentLifecyclePlanner.BinaryPath && targetActivated)
                    ? target.Manifest.ArtifactSha256
                    : oldDigest;
                return Success(digest + "  " + path + "\n");
            }

            return Success();
        });
        var services = new FakeServiceManager(
            [ActiveServiceResult()],
            action => action == ServerServiceAction.Restart
                ? new ServerServiceActionResult(
                    false,
                    new RemoteError(RemoteErrorCode.AmbiguousState, "fixture uncertain restart"),
                    "fixture uncertain restart")
                : SuccessfulAction());
        var lifecycle = CreateService(
            commands,
            new RecordingFileSystemFactory(),
            services,
            oldArtifact.Manifest.Version);

        var result = await lifecycle.UpdateAsync(
            profile,
            target,
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentLifecycleExecutionState.Ambiguous, result.State);
        Assert.DoesNotContain(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.Count >= 2 &&
            spec.Arguments[^2] == AgentLifecycleRemoteLayout.RollbackBinaryPath &&
            spec.Arguments[^1] == AgentLifecyclePlanner.BinaryPath);
        Assert.DoesNotContain(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.Contains("rm") &&
            spec.Arguments.Contains(AgentLifecycleRemoteLayout.RollbackBinaryPath));
    }

    [Fact]
    public async Task CleanUninstallRemovesOnlyFixedAgentOwnedResources()
    {
        var installedVersion = new AgentReleaseVersion(1, 0, 0);
        var plan = AgentLifecyclePlanner.PlanUninstall(installedVersion);
        var commands = new RecordingCommandFactory(_ => Success());
        var services = new FakeServiceManager([
            ActiveServiceResult(),
            ActiveServiceResult(),
            AbsentService(),
        ]);
        var lifecycle = CreateService(
            commands,
            new RecordingFileSystemFactory(),
            services,
            installedVersion);

        var result = await lifecycle.UninstallAsync(
            CreateProfile(),
            plan,
            TestContext.Current.CancellationToken);

        Assert.Equal(AgentLifecycleExecutionState.Succeeded, result.State);
        Assert.Equal([ServerServiceAction.Stop, ServerServiceAction.Disable], services.Actions);
        Assert.Contains(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.SequenceEqual([
                "-n", "rm", "-f", "--",
                AgentLifecyclePlanner.ServiceUnitPath,
                AgentLifecyclePlanner.BinaryPath,
                AgentLifecycleRemoteLayout.RollbackBinaryPath,
            ]));
        Assert.Contains(commands.Commands, spec =>
            spec.Executable == "sudo" &&
            spec.Arguments.SequenceEqual([
                "-n", "rm", "-rf", "--",
                AgentLifecyclePlanner.StateDirectory,
                AgentLifecyclePlanner.CacheDirectory,
            ]));
        Assert.DoesNotContain(
            commands.Commands.SelectMany(spec => spec.Arguments),
            argument =>
                argument.Contains("/etc/ssh", StringComparison.OrdinalIgnoreCase) ||
                argument.Contains("firewall", StringComparison.OrdinalIgnoreCase) ||
                argument.Contains("docker", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("/home/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StatusExplainsProtocolIncompatibility()
    {
        var services = new FakeServiceManager([ActiveServiceResult()]);
        var lifecycle = CreateService(
            new RecordingCommandFactory(_ => Success()),
            new RecordingFileSystemFactory(),
            services,
            new AgentReleaseVersion(1, 0, 0),
            new AgentProtocolVersion(2, 0));

        var status = await lifecycle.GetStatusAsync(CreateProfile(), TestContext.Current.CancellationToken);

        Assert.Equal(AgentLifecycleStatusKind.Incompatible, status.Kind);
        Assert.Equal(AgentConnectionState.Incompatible, status.ConnectionState);
    }

    [Fact]
    public void FixedUnitKeepsLoopbackPortAndSeparatesLifecycleCacheFromDynamicUser()
    {
        Assert.Contains("ExecStart=/opt/serverdesk-agent/serverdesk-agent\n", AgentSystemdUnitDefinition.Content, StringComparison.Ordinal);
        Assert.Contains("Environment=SERVERDESK_AGENT_PORT=41371\n", AgentSystemdUnitDefinition.Content, StringComparison.Ordinal);
        Assert.Contains("DynamicUser=yes\n", AgentSystemdUnitDefinition.Content, StringComparison.Ordinal);
        Assert.Contains("StateDirectory=serverdesk-agent\n", AgentSystemdUnitDefinition.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("CacheDirectory=", AgentSystemdUnitDefinition.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", AgentSystemdUnitDefinition.Content, StringComparison.Ordinal);
        Assert.Equal("/var/cache/serverdesk-agent/serverdesk-agent.previous", AgentLifecycleRemoteLayout.RollbackBinaryPath);
    }

    [Theory]
    [InlineData("serverdesk_ci", true)]
    [InlineData("_agent2", true)]
    [InlineData("bad.name", false)]
    [InlineData("-root", false)]
    [InlineData("Root", false)]
    [InlineData("bad user", false)]
    public void StagingOwnerSyntaxIsConservative(string account, bool expected) =>
        Assert.Equal(expected, AgentLifecycleService.IsSafeUnixAccountName(account));

    [Theory]
    [InlineData("1.2.3", true, "1.2.3")]
    [InlineData("1.2.3.0", true, "1.2.3")]
    [InlineData("1.2.3.1", false, "0.0.0")]
    [InlineData("1.2.3-beta", false, "0.0.0")]
    public void RuntimeVersionNormalizationIsStrict(string raw, bool expected, string normalized)
    {
        var parsed = AgentLifecycleService.TryParseRuntimeVersion(raw, out var version);

        Assert.Equal(expected, parsed);
        Assert.Equal(normalized, version.ToString());
    }

    private static AgentLifecycleService CreateService(
        IRemoteCommandExecutorFactory commandFactory,
        IRemoteFileSystemFactory fileSystemFactory,
        IServerServiceManager serviceManager,
        AgentReleaseVersion runtimeVersion,
        AgentProtocolVersion? protocol = null) =>
        new(
            commandFactory,
            fileSystemFactory,
            serviceManager,
            new FakeTunnelFactory(),
            new FakeTransportFactory(runtimeVersion, protocol ?? AgentProtocolVersion.Current));

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
        ServerProfile.Create("Agent lifecycle fixture", "example.test", 22, "serverdesk_ci");

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
        public List<(RemotePath Path, byte[] Content)> Uploads { get; } = [];

        public IRemoteFileSystem Create(ServerProfile profile) =>
            new RecordingFileSystem(profile.Id, Uploads);
    }

    private sealed class RecordingFileSystem : IRemoteFileSystem
    {
        private readonly List<(RemotePath Path, byte[] Content)> _uploads;

        public RecordingFileSystem(Guid profileId, List<(RemotePath Path, byte[] Content)> uploads)
        {
            ServerProfileId = profileId;
            _uploads = uploads;
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
            using var target = new MemoryStream();
            await source.CopyToAsync(target, cancellationToken);
            _uploads.Add((destination, target.ToArray()));
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

        public int GetCalls { get; private set; }

        public List<ServerServiceAction> Actions { get; } = [];

        public Task<ServerServiceQueryResult> ListAsync(ServerProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ServerServiceQueryResult> GetAsync(
            ServerProfile profile,
            string unit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GetCalls++;
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
        private readonly AgentProtocolVersion _protocol;

        public FakeTransportFactory(AgentReleaseVersion version, AgentProtocolVersion protocol)
        {
            _version = version;
            _protocol = protocol;
        }

        public IAgentTransportClient Create(int localPort)
        {
            Assert.Equal(54321, localPort);
            return new FakeTransport(_version, _protocol);
        }
    }

    private sealed class FakeTransport : IAgentTransportClient
    {
        private readonly AgentReleaseVersion _version;
        private readonly AgentProtocolVersion _protocol;

        public FakeTransport(AgentReleaseVersion version, AgentProtocolVersion protocol)
        {
            _version = version;
            _protocol = protocol;
        }

        public ValueTask<AgentPeerInfo> NegotiateAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new AgentPeerInfo(
                _protocol,
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
