using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ServerDesk.Application.Remote;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Application.Services;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Operations;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Agent;

public sealed class AgentLifecycleService : IAgentLifecycleService
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(30);
    private static readonly IReadOnlyDictionary<string, string> StableEnvironment =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["LC_ALL"] = "C" };

    private readonly IRemoteCommandExecutorFactory _commandFactory;
    private readonly IRemoteFileSystemFactory _fileSystemFactory;
    private readonly IServerServiceManager _serviceManager;
    private readonly IAgentTunnelSessionFactory _tunnelFactory;
    private readonly IAgentTransportClientFactory _transportFactory;

    public AgentLifecycleService(
        IRemoteCommandExecutorFactory commandFactory,
        IRemoteFileSystemFactory fileSystemFactory,
        IServerServiceManager serviceManager,
        IAgentTunnelSessionFactory tunnelFactory,
        IAgentTransportClientFactory transportFactory)
    {
        _commandFactory = commandFactory ?? throw new ArgumentNullException(nameof(commandFactory));
        _fileSystemFactory = fileSystemFactory ?? throw new ArgumentNullException(nameof(fileSystemFactory));
        _serviceManager = serviceManager ?? throw new ArgumentNullException(nameof(serviceManager));
        _tunnelFactory = tunnelFactory ?? throw new ArgumentNullException(nameof(tunnelFactory));
        _transportFactory = transportFactory ?? throw new ArgumentNullException(nameof(transportFactory));
    }

    public async Task<AgentLifecycleStatus> GetStatusAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var service = await _serviceManager.GetAsync(profile, AgentLifecyclePlanner.ServiceUnit, cancellationToken)
            .ConfigureAwait(false);
        if (!service.IsSuccess)
        {
            if (service.Error?.Code == RemoteErrorCode.PathNotFound)
            {
                return new AgentLifecycleStatus(
                    AgentLifecycleStatusKind.Absent,
                    null,
                    null,
                    "serverdesk-agent is not installed.");
            }

            return new AgentLifecycleStatus(
                service.Error is not null && IsAmbiguousCode(service.Error.Code)
                    ? AgentLifecycleStatusKind.Ambiguous
                    : AgentLifecycleStatusKind.Unreachable,
                null,
                null,
                "ServerDesk could not determine the fixed agent service state reliably.");
        }

        if (service.Services.Count != 1)
        {
            return new AgentLifecycleStatus(
                AgentLifecycleStatusKind.Ambiguous,
                null,
                null,
                "ServerDesk received an unexpected result for the fixed agent service.");
        }

        var unit = service.Services[0];
        if (!unit.IsActive)
        {
            return Status(
                AgentLifecycleStatusKind.Degraded,
                unit,
                null,
                null,
                "serverdesk-agent is installed but is not active.");
        }

        try
        {
            await using var tunnel = _tunnelFactory.Create(profile, AgentLifecycleRemoteLayout.AgentPort);
            await tunnel.StartAsync(cancellationToken).ConfigureAwait(false);
            if (tunnel.State != AgentTunnelState.Active || tunnel.LocalPort is < 1 or > 65535)
            {
                return Status(
                    AgentLifecycleStatusKind.Unreachable,
                    unit,
                    null,
                    AgentConnectionState.Disconnected,
                    "The SSH-controlled agent tunnel did not become active.");
            }

            await using var transport = _transportFactory.Create(tunnel.LocalPort);
            AgentPeerInfo peer;
            try
            {
                peer = await transport.NegotiateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (AgentTransportException exception)
            {
                return Status(
                    AgentLifecycleStatusKind.Unreachable,
                    unit,
                    null,
                    exception.State,
                    "Agent version negotiation failed through the SSH-controlled tunnel.");
            }

            var compatibility = AgentCompatibilityPolicy.Evaluate(
                AgentProtocolVersion.Current,
                AgentCompatibilityPolicy.KnownCapabilities,
                peer);
            var parsed = TryParseRuntimeVersion(peer.AgentVersion, out var runtimeVersion);
            if (compatibility.State == AgentConnectionState.Incompatible)
            {
                return Status(
                    AgentLifecycleStatusKind.Incompatible,
                    unit,
                    parsed ? runtimeVersion : null,
                    compatibility.State,
                    "The installed agent protocol is incompatible with this ServerDesk build.");
            }

            if (compatibility.State != AgentConnectionState.Available)
            {
                return Status(
                    AgentLifecycleStatusKind.Unreachable,
                    unit,
                    parsed ? runtimeVersion : null,
                    compatibility.State,
                    "Agent negotiation did not produce an available transport.");
            }

            if (!string.Equals(peer.Platform, AgentReleaseVerifier.PlatformId, StringComparison.Ordinal) ||
                peer.Architecture is not ("x64" or "arm64") ||
                !parsed)
            {
                return Status(
                    AgentLifecycleStatusKind.Degraded,
                    unit,
                    null,
                    AgentConnectionState.Failed,
                    "The agent returned invalid runtime platform/version metadata.");
            }

            var health = await transport.GetHealthAsync(cancellationToken).ConfigureAwait(false);
            if (health.State == AgentConnectionState.Disconnected)
            {
                return Status(
                    AgentLifecycleStatusKind.Unreachable,
                    unit,
                    runtimeVersion,
                    health.State,
                    "The agent disconnected during its health probe.");
            }

            if (health.State != AgentConnectionState.Available)
            {
                return Status(
                    AgentLifecycleStatusKind.Degraded,
                    unit,
                    runtimeVersion,
                    health.State,
                    "The agent is reachable but reports degraded health.");
            }

            if (!TryParseRuntimeVersion(health.AgentVersion, out var healthVersion) || healthVersion != runtimeVersion)
            {
                return Status(
                    AgentLifecycleStatusKind.Degraded,
                    unit,
                    runtimeVersion,
                    AgentConnectionState.Failed,
                    "Agent health/version metadata does not match negotiated runtime metadata.");
            }

            if (!IsEnabled(unit.EnabledState))
            {
                return Status(
                    AgentLifecycleStatusKind.Degraded,
                    unit,
                    runtimeVersion,
                    AgentConnectionState.Available,
                    "The agent is healthy but the fixed service is not enabled for startup.");
            }

            return Status(
                AgentLifecycleStatusKind.Healthy,
                unit,
                runtimeVersion,
                AgentConnectionState.Available,
                "serverdesk-agent is active, enabled and healthy through the SSH-controlled tunnel.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return Status(
                AgentLifecycleStatusKind.Unreachable,
                unit,
                null,
                AgentConnectionState.Disconnected,
                "ServerDesk could not establish the SSH-controlled agent tunnel.");
        }
    }

    public async Task<AgentLifecycleExecutionResult> InstallAsync(
        ServerProfile profile,
        VerifiedAgentArtifact artifact,
        AgentLifecyclePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateArtifactPlan(artifact, plan, AgentLifecycleOperation.Install);

        var before = await GetStatusAsync(profile, cancellationToken).ConfigureAwait(false);
        if (before.Kind != AgentLifecycleStatusKind.Absent)
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "Install requires the fixed serverdesk-agent service to be absent. Refresh status before retrying.",
                before);
        }

        await using var executor = _commandFactory.Create(profile);
        var architecture = await VerifyArchitectureAsync(executor, artifact.Manifest.Architecture, cancellationToken)
            .ConfigureAwait(false);
        if (!architecture.Success)
        {
            return Result(architecture, before);
        }

        var mutationStarted = true;
        try
        {
            var stage = await StageAsync(
                    profile,
                    executor,
                    artifact,
                    plan.PlanId,
                    includeUnit: true,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!stage.Success)
            {
                return Result(stage, before);
            }

            var installed = await InstallBinaryAndUnitAsync(executor, artifact, plan.PlanId, cancellationToken)
                .ConfigureAwait(false);
            if (!installed.Success)
            {
                if (!installed.IsAmbiguous)
                {
                    await CleanupStageBestEffortAsync(executor, plan.PlanId).ConfigureAwait(false);
                }

                return Result(installed, before);
            }

            var enable = await _serviceManager.ExecuteAsync(
                    profile,
                    AgentLifecyclePlanner.ServiceUnit,
                    ServerServiceAction.Enable,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!enable.IsSuccess)
            {
                var mapped = MapServiceFailure(enable, "Enabling serverdesk-agent failed.");
                if (!mapped.IsAmbiguous)
                {
                    await CleanupStageBestEffortAsync(executor, plan.PlanId).ConfigureAwait(false);
                }

                return Result(mapped, before);
            }

            var start = await _serviceManager.ExecuteAsync(
                    profile,
                    AgentLifecyclePlanner.ServiceUnit,
                    ServerServiceAction.Start,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!start.IsSuccess)
            {
                var mapped = MapServiceFailure(start, "Starting serverdesk-agent failed.");
                if (!mapped.IsAmbiguous)
                {
                    await CleanupStageBestEffortAsync(executor, plan.PlanId).ConfigureAwait(false);
                }

                return Result(mapped, before);
            }

            var after = await GetStatusAsync(profile, cancellationToken).ConfigureAwait(false);
            if (!Matches(after, artifact.Manifest.Version))
            {
                if (!IsUncertain(after))
                {
                    await CleanupStageBestEffortAsync(executor, plan.PlanId).ConfigureAwait(false);
                }

                return VerificationFailure(
                    after,
                    "Agent activation completed, but target health/version did not verify.");
            }

            var cleanup = await CleanupStageAsync(executor, plan.PlanId, cancellationToken).ConfigureAwait(false);
            if (!cleanup.Success)
            {
                return new AgentLifecycleExecutionResult(
                    cleanup.IsAmbiguous ? AgentLifecycleExecutionState.Ambiguous : AgentLifecycleExecutionState.Failed,
                    "The agent is installed and healthy, but staging cleanup could not be verified. Refresh before retrying.",
                    cleanup.Error,
                    after);
            }

            return new AgentLifecycleExecutionResult(
                AgentLifecycleExecutionState.Succeeded,
                $"serverdesk-agent {artifact.Manifest.Version} installed and verified through the SSH-controlled tunnel.",
                Status: after);
        }
        catch (OperationCanceledException) when (mutationStarted)
        {
            return AmbiguousResult(
                "Agent installation was cancelled after a remote mutation may have started. Refresh status before retrying.");
        }
        catch (RemoteFileSystemException) when (mutationStarted)
        {
            return AmbiguousResult(
                "The SSH/SFTP connection was interrupted after agent staging started. Refresh status before retrying.");
        }
        catch (Exception) when (mutationStarted)
        {
            return AmbiguousResult(
                "Agent installation lost a reliable completion signal after mutation started. Refresh status before retrying.");
        }
    }

    public async Task<AgentLifecycleExecutionResult> UpdateAsync(
        ServerProfile profile,
        VerifiedAgentArtifact artifact,
        AgentLifecyclePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ValidateArtifactPlan(artifact, plan, AgentLifecycleOperation.Update);
        if (plan.CurrentVersion is not AgentReleaseVersion expectedCurrent)
        {
            throw new ArgumentException("Agent update plan requires a current version.", nameof(plan));
        }

        var before = await GetStatusAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!before.IsHealthy || before.Version is not AgentReleaseVersion installed || installed != expectedCurrent)
        {
            return Failure(
                RemoteErrorCode.PathConflict,
                "Update requires the same healthy installed version used when the plan was prepared.",
                before);
        }

        if (artifact.Manifest.Version.CompareTo(installed) <= 0)
        {
            return Failure(
                RemoteErrorCode.UnsupportedVersion,
                "Downgrade and same-version agent replacement are disabled.",
                before);
        }

        await using var executor = _commandFactory.Create(profile);
        var architecture = await VerifyArchitectureAsync(executor, artifact.Manifest.Architecture, cancellationToken)
            .ConfigureAwait(false);
        if (!architecture.Success)
        {
            return Result(architecture, before);
        }

        var previousIntegrity = await ReadRemoteIntegrityAsync(
                executor,
                AgentLifecyclePlanner.BinaryPath,
                cancellationToken)
            .ConfigureAwait(false);
        if (!previousIntegrity.Success || previousIntegrity.Integrity is null)
        {
            return Failure(
                previousIntegrity.Error?.Code ?? RemoteErrorCode.CommandFailed,
                "ServerDesk could not establish the current agent binary integrity before update; no mutation was sent.",
                before);
        }

        var mutationStarted = true;
        try
        {
            var stage = await StageAsync(
                    profile,
                    executor,
                    artifact,
                    plan.PlanId,
                    includeUnit: false,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!stage.Success)
            {
                return Result(stage, before);
            }

            var preserve = await RunAsync(
                    executor,
                    "sudo",
                    [
                        "-n", "install", "-m", "0755", "--",
                        AgentLifecyclePlanner.BinaryPath,
                        AgentLifecycleRemoteLayout.RollbackBinaryPath,
                    ],
                    OperationRisk.Destructive,
                    mutation: true,
                    "Could not preserve the current agent binary for bounded rollback.",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!preserve.Success)
            {
                return Result(preserve, before);
            }

            var verifyRollback = await VerifyRemoteFileAsync(
                    executor,
                    AgentLifecycleRemoteLayout.RollbackBinaryPath,
                    previousIntegrity.Integrity.Length,
                    previousIntegrity.Integrity.Sha256,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!verifyRollback.Success)
            {
                return AmbiguousResult(
                    "The bounded rollback copy was created but its exact integrity could not be verified. Do not retry until status is refreshed.");
            }

            var replace = await InstallStagedBinaryAsync(executor, artifact, plan.PlanId, cancellationToken)
                .ConfigureAwait(false);
            if (!replace.Success)
            {
                return Result(replace, before);
            }

            var restart = await _serviceManager.ExecuteAsync(
                    profile,
                    AgentLifecyclePlanner.ServiceUnit,
                    ServerServiceAction.Restart,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!restart.IsSuccess)
            {
                var mapped = MapServiceFailure(restart, "Restarting the updated agent failed.");
                if (mapped.IsAmbiguous)
                {
                    return Result(mapped, before);
                }

                return await RollBackAsync(
                        profile,
                        executor,
                        plan.PlanId,
                        expectedCurrent,
                        previousIntegrity.Integrity,
                        "The target binary was installed but systemd reported a deterministic restart failure.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var after = await GetStatusAsync(profile, cancellationToken).ConfigureAwait(false);
            if (!Matches(after, artifact.Manifest.Version))
            {
                if (IsUncertain(after))
                {
                    return new AgentLifecycleExecutionResult(
                        AgentLifecycleExecutionState.Ambiguous,
                        "The updated service restarted, but target health/version is uncertain. The rollback copy is retained; refresh before any mutation.",
                        new RemoteError(RemoteErrorCode.AmbiguousState, "Post-update verification is uncertain."),
                        after);
                }

                return await RollBackAsync(
                        profile,
                        executor,
                        plan.PlanId,
                        expectedCurrent,
                        previousIntegrity.Integrity,
                        "The updated agent responded deterministically but did not verify at the authenticated target version.",
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            var removeRollback = await RunAsync(
                    executor,
                    "sudo",
                    ["-n", "rm", "-f", "--", AgentLifecycleRemoteLayout.RollbackBinaryPath],
                    OperationRisk.Mutating,
                    mutation: true,
                    "Could not remove the bounded rollback binary after successful verification.",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!removeRollback.Success)
            {
                return Result(removeRollback, after);
            }

            var cleanup = await CleanupStageAsync(executor, plan.PlanId, cancellationToken).ConfigureAwait(false);
            if (!cleanup.Success)
            {
                return Result(cleanup, after);
            }

            return new AgentLifecycleExecutionResult(
                AgentLifecycleExecutionState.Succeeded,
                $"serverdesk-agent updated from {expectedCurrent} to {artifact.Manifest.Version} and verified.",
                Status: after);
        }
        catch (OperationCanceledException) when (mutationStarted)
        {
            return AmbiguousResult(
                "Agent update was cancelled after a remote mutation may have started. Do not retry until status is refreshed.");
        }
        catch (RemoteFileSystemException) when (mutationStarted)
        {
            return AmbiguousResult(
                "The SSH/SFTP connection was interrupted after update staging started. Do not retry until status is refreshed.");
        }
        catch (Exception) when (mutationStarted)
        {
            return AmbiguousResult(
                "Agent update lost a reliable completion signal after mutation started. Do not retry until status is refreshed.");
        }
    }

    public async Task<AgentLifecycleExecutionResult> UninstallAsync(
        ServerProfile profile,
        AgentLifecyclePlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        AgentLifecyclePlanPolicy.Validate(plan);
        if (plan.Operation != AgentLifecycleOperation.Uninstall)
        {
            throw new ArgumentException("Agent lifecycle plan is not an uninstall plan.", nameof(plan));
        }

        var before = await GetStatusAsync(profile, cancellationToken).ConfigureAwait(false);
        await using var executor = _commandFactory.Create(profile);
        var mutationStarted = false;
        try
        {
            var current = await _serviceManager.GetAsync(
                    profile,
                    AgentLifecyclePlanner.ServiceUnit,
                    cancellationToken)
                .ConfigureAwait(false);
            if (current.IsSuccess && current.Services.Count == 1)
            {
                var unit = current.Services[0];
                if (unit.IsActive)
                {
                    mutationStarted = true;
                    var stop = await _serviceManager.ExecuteAsync(
                            profile,
                            AgentLifecyclePlanner.ServiceUnit,
                            ServerServiceAction.Stop,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!stop.IsSuccess)
                    {
                        return Result(MapServiceFailure(stop, "Stopping serverdesk-agent failed."), before);
                    }
                }

                if (IsEnabled(unit.EnabledState))
                {
                    mutationStarted = true;
                    var disable = await _serviceManager.ExecuteAsync(
                            profile,
                            AgentLifecyclePlanner.ServiceUnit,
                            ServerServiceAction.Disable,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!disable.IsSuccess)
                    {
                        return Result(MapServiceFailure(disable, "Disabling serverdesk-agent failed."), before);
                    }
                }
            }
            else if (current.Error?.Code != RemoteErrorCode.PathNotFound)
            {
                return Failure(
                    RemoteErrorCode.AmbiguousState,
                    "ServerDesk could not establish the fixed service state before uninstall. Refresh before retrying.",
                    before);
            }

            mutationStarted = true;
            var removeFiles = await RunAsync(
                    executor,
                    "sudo",
                    [
                        "-n", "rm", "-f", "--",
                        AgentLifecyclePlanner.ServiceUnitPath,
                        AgentLifecyclePlanner.BinaryPath,
                        AgentLifecycleRemoteLayout.RollbackBinaryPath,
                    ],
                    OperationRisk.Destructive,
                    mutation: true,
                    "Could not remove the fixed agent service unit/binary resources.",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!removeFiles.Success)
            {
                return Result(removeFiles, before);
            }

            var removeDirectories = await RunAsync(
                    executor,
                    "sudo",
                    [
                        "-n", "rm", "-rf", "--",
                        AgentLifecyclePlanner.StateDirectory,
                        AgentLifecyclePlanner.CacheDirectory,
                    ],
                    OperationRisk.Destructive,
                    mutation: true,
                    "Could not remove the fixed agent state/cache resources.",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!removeDirectories.Success)
            {
                return Result(removeDirectories, before);
            }

            var reload = await RunAsync(
                    executor,
                    "sudo",
                    ["-n", "systemctl", "daemon-reload"],
                    OperationRisk.Mutating,
                    mutation: true,
                    "systemd daemon-reload failed after removing serverdesk-agent.",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!reload.Success)
            {
                return Result(reload, before);
            }

            foreach (var path in RemovalVerificationPaths())
            {
                var absent = await RunAsync(
                        executor,
                        "test",
                        ["!", "-e", path],
                        OperationRisk.ReadOnly,
                        mutation: false,
                        "An agent-owned resource is still present after uninstall.",
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!absent.Success)
                {
                    return AmbiguousResult(
                        "Uninstall mutations completed, but clean removal of every fixed agent-owned resource could not be verified.");
                }
            }

            var after = await GetStatusAsync(profile, cancellationToken).ConfigureAwait(false);
            if (after.Kind != AgentLifecycleStatusKind.Absent)
            {
                return new AgentLifecycleExecutionResult(
                    AgentLifecycleExecutionState.Ambiguous,
                    "Agent-owned resources were removed, but the fixed systemd unit is still visible. Refresh before retrying.",
                    new RemoteError(RemoteErrorCode.AmbiguousState, "The fixed agent unit remains visible after uninstall."),
                    after);
            }

            return new AgentLifecycleExecutionResult(
                AgentLifecycleExecutionState.Succeeded,
                "serverdesk-agent was cleanly uninstalled; only fixed agent-owned unit/binary/state/cache resources were removed.",
                Status: after);
        }
        catch (OperationCanceledException) when (mutationStarted)
        {
            return AmbiguousResult(
                "Agent uninstall was cancelled after a remote mutation may have started. Refresh before retrying.");
        }
        catch (Exception) when (mutationStarted)
        {
            return AmbiguousResult(
                "Agent uninstall lost a reliable completion signal after mutation started. Refresh before retrying.");
        }
    }

    internal static bool TryParseRuntimeVersion(string? value, out AgentReleaseVersion version)
    {
        if (AgentReleaseVersion.TryParse(value, out version))
        {
            return true;
        }

        version = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Split('.', StringSplitOptions.None);
        if (parts.Length != 4 || !string.Equals(parts[3], "0", StringComparison.Ordinal))
        {
            return false;
        }

        return AgentReleaseVersion.TryParse(string.Join(".", parts.Take(3)), out version);
    }

    internal static bool IsSafeUnixAccountName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 32)
        {
            return false;
        }

        if (!(value[0] == '_' || char.IsAsciiLetterLower(value[0])))
        {
            return false;
        }

        for (var index = 1; index < value.Length; index++)
        {
            var character = value[index];
            if (!(char.IsAsciiLetterLower(character) || char.IsAsciiDigit(character) || character is '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static void ValidateArtifactPlan(
        VerifiedAgentArtifact artifact,
        AgentLifecyclePlan plan,
        AgentLifecycleOperation operation)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        AgentLifecyclePlanPolicy.Validate(plan);
        if (plan.Operation != operation ||
            plan.TargetVersion is not AgentReleaseVersion target ||
            target != artifact.Manifest.Version)
        {
            throw new ArgumentException(
                "Agent lifecycle plan does not match the authenticated verified artifact.",
                nameof(plan));
        }

        if (artifact.Length != artifact.Manifest.ArtifactLength ||
            !string.Equals(
                Convert.ToHexStringLower(SHA256.HashData(artifact.Content)),
                artifact.Manifest.ArtifactSha256,
                StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Verified agent artifact content no longer matches its authenticated manifest.",
                nameof(artifact));
        }
    }

    private async Task<StepResult> VerifyArchitectureAsync(
        IRemoteCommandExecutor executor,
        string expected,
        CancellationToken cancellationToken)
    {
        var result = await RunAsync(
                executor,
                "uname",
                ["-m"],
                OperationRisk.ReadOnly,
                mutation: false,
                "Unable to determine the remote Linux architecture.",
                cancellationToken)
            .ConfigureAwait(false);
        if (!result.Success)
        {
            return result;
        }

        var actual = result.Output.Trim() switch
        {
            "x86_64" => "x64",
            "aarch64" => "arm64",
            _ => string.Empty,
        };
        return string.Equals(actual, expected, StringComparison.Ordinal)
            ? StepResult.Ok()
            : StepResult.Fail(new RemoteError(
                RemoteErrorCode.UnsupportedVersion,
                "The authenticated agent artifact architecture does not match the remote Linux host."));
    }

    private async Task<StepResult> StageAsync(
        ServerProfile profile,
        IRemoteCommandExecutor executor,
        VerifiedAgentArtifact artifact,
        Guid planId,
        bool includeUnit,
        CancellationToken cancellationToken)
    {
        var prepare = await PrepareStagingAsync(profile, executor, planId, cancellationToken).ConfigureAwait(false);
        if (!prepare.Success)
        {
            return prepare;
        }

        await using (var fileSystem = _fileSystemFactory.Create(profile))
        {
            await fileSystem.ConnectAsync(cancellationToken).ConfigureAwait(false);
            await using var artifactStream = new MemoryStream(artifact.Content, writable: false);
            await fileSystem.UploadAsync(
                    artifactStream,
                    AgentLifecycleRemoteLayout.GetStagedBinary(planId),
                    artifact.Length,
                    overwrite: false,
                    cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (includeUnit)
            {
                await using var unitStream = new MemoryStream(AgentSystemdUnitDefinition.Bytes, writable: false);
                await fileSystem.UploadAsync(
                        unitStream,
                        AgentLifecycleRemoteLayout.GetStagedUnit(planId),
                        AgentSystemdUnitDefinition.Bytes.LongLength,
                        overwrite: false,
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        var binary = await VerifyRemoteFileAsync(
                executor,
                AgentLifecycleRemoteLayout.GetStagedBinary(planId).Value,
                artifact.Manifest.ArtifactLength,
                artifact.Manifest.ArtifactSha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (!binary.Success)
        {
            if (binary.Error is not null && IsAmbiguousCode(binary.Error.Code))
            {
                return StepResult.AmbiguousFailure(new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    "Agent staging completed, but remote artifact integrity could not be observed reliably."));
            }

            await CleanupStageBestEffortAsync(executor, planId).ConfigureAwait(false);
            return binary;
        }

        if (!includeUnit)
        {
            return StepResult.Ok();
        }

        var unit = await VerifyRemoteFileAsync(
                executor,
                AgentLifecycleRemoteLayout.GetStagedUnit(planId).Value,
                AgentSystemdUnitDefinition.Bytes.LongLength,
                AgentSystemdUnitDefinition.Sha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (!unit.Success)
        {
            if (unit.Error is not null && IsAmbiguousCode(unit.Error.Code))
            {
                return StepResult.AmbiguousFailure(new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    "Agent unit staging completed, but exact staged content could not be observed reliably."));
            }

            await CleanupStageBestEffortAsync(executor, planId).ConfigureAwait(false);
        }

        return unit;
    }

    private async Task<StepResult> PrepareStagingAsync(
        ServerProfile profile,
        IRemoteCommandExecutor executor,
        Guid planId,
        CancellationToken cancellationToken)
    {
        if (!IsSafeUnixAccountName(profile.Username))
        {
            return StepResult.Fail(new RemoteError(
                RemoteErrorCode.InvalidEndpoint,
                "The SSH account name is outside the conservative Linux account syntax allowed for agent staging."));
        }

        var roots = await RunAsync(
                executor,
                "sudo",
                [
                    "-n", "install", "-d", "-m", "0711", "--",
                    AgentLifecyclePlanner.CacheDirectory,
                    AgentLifecycleRemoteLayout.StagingRoot,
                ],
                OperationRisk.Mutating,
                mutation: true,
                "Could not create the fixed agent lifecycle cache/staging roots.",
                cancellationToken)
            .ConfigureAwait(false);
        if (!roots.Success)
        {
            return roots;
        }

        return await RunAsync(
                executor,
                "sudo",
                [
                    "-n", "install", "-d", "-m", "0700", "-o", profile.Username, "--",
                    AgentLifecycleRemoteLayout.GetStagingDirectory(planId).Value,
                ],
                OperationRisk.Mutating,
                mutation: true,
                "Could not create the fixed per-operation agent staging directory.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<StepResult> InstallBinaryAndUnitAsync(
        IRemoteCommandExecutor executor,
        VerifiedAgentArtifact artifact,
        Guid planId,
        CancellationToken cancellationToken)
    {
        var directory = await RunAsync(
                executor,
                "sudo",
                ["-n", "install", "-d", "-m", "0755", "--", "/opt/serverdesk-agent"],
                OperationRisk.Mutating,
                mutation: true,
                "Could not create the fixed agent binary directory.",
                cancellationToken)
            .ConfigureAwait(false);
        if (!directory.Success)
        {
            return directory;
        }

        var binary = await InstallStagedBinaryAsync(executor, artifact, planId, cancellationToken)
            .ConfigureAwait(false);
        if (!binary.Success)
        {
            return binary;
        }

        var unit = await RunAsync(
                executor,
                "sudo",
                [
                    "-n", "install", "-m", "0644", "--",
                    AgentLifecycleRemoteLayout.GetStagedUnit(planId).Value,
                    AgentLifecyclePlanner.ServiceUnitPath,
                ],
                OperationRisk.Mutating,
                mutation: true,
                "Could not install the fixed serverdesk-agent systemd unit.",
                cancellationToken)
            .ConfigureAwait(false);
        if (!unit.Success)
        {
            return unit;
        }

        var verifyUnit = await VerifyRemoteFileAsync(
                executor,
                AgentLifecyclePlanner.ServiceUnitPath,
                AgentSystemdUnitDefinition.Bytes.LongLength,
                AgentSystemdUnitDefinition.Sha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (!verifyUnit.Success)
        {
            return StepResult.AmbiguousFailure(new RemoteError(
                RemoteErrorCode.AmbiguousState,
                "The systemd unit was installed, but its exact fixed content could not be verified."));
        }

        return await RunAsync(
                executor,
                "sudo",
                ["-n", "systemctl", "daemon-reload"],
                OperationRisk.Mutating,
                mutation: true,
                "systemd daemon-reload failed after installing serverdesk-agent.",
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<StepResult> InstallStagedBinaryAsync(
        IRemoteCommandExecutor executor,
        VerifiedAgentArtifact artifact,
        Guid planId,
        CancellationToken cancellationToken)
    {
        var install = await RunAsync(
                executor,
                "sudo",
                [
                    "-n", "install", "-m", "0755", "--",
                    AgentLifecycleRemoteLayout.GetStagedBinary(planId).Value,
                    AgentLifecyclePlanner.BinaryPath,
                ],
                OperationRisk.Destructive,
                mutation: true,
                "Could not activate the verified agent binary.",
                cancellationToken)
            .ConfigureAwait(false);
        if (!install.Success)
        {
            return install;
        }

        var verify = await VerifyRemoteFileAsync(
                executor,
                AgentLifecyclePlanner.BinaryPath,
                artifact.Manifest.ArtifactLength,
                artifact.Manifest.ArtifactSha256,
                cancellationToken)
            .ConfigureAwait(false);
        return verify.Success
            ? verify
            : StepResult.AmbiguousFailure(new RemoteError(
                RemoteErrorCode.AmbiguousState,
                "The agent binary was replaced, but exact installed integrity could not be verified."));
    }

    private async Task<StepResult> VerifyRemoteFileAsync(
        IRemoteCommandExecutor executor,
        string path,
        long expectedLength,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        var integrity = await ReadRemoteIntegrityAsync(executor, path, cancellationToken).ConfigureAwait(false);
        if (!integrity.Success || integrity.Integrity is null)
        {
            return StepResult.Fail(integrity.Error ?? new RemoteError(
                RemoteErrorCode.CommandFailed,
                "Unable to establish remote agent file integrity."));
        }

        if (integrity.Integrity.Length != expectedLength)
        {
            return StepResult.Fail(new RemoteError(
                RemoteErrorCode.ParseFailed,
                "Remote agent file length does not match the authenticated expected length."));
        }

        return CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(integrity.Integrity.Sha256),
                Encoding.ASCII.GetBytes(expectedSha256))
            ? StepResult.Ok()
            : StepResult.Fail(new RemoteError(
                RemoteErrorCode.ParseFailed,
                "Remote agent file SHA-256 does not match the authenticated expected digest."));
    }

    private async Task<IntegrityResult> ReadRemoteIntegrityAsync(
        IRemoteCommandExecutor executor,
        string path,
        CancellationToken cancellationToken)
    {
        var stat = await RunAsync(
                executor,
                "stat",
                ["-c", "%s", "--", path],
                OperationRisk.ReadOnly,
                mutation: false,
                "Unable to read remote agent file length.",
                cancellationToken)
            .ConfigureAwait(false);
        if (!stat.Success)
        {
            return IntegrityResult.Fail(stat.Error);
        }

        if (!long.TryParse(stat.Output.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var length) || length < 0)
        {
            return IntegrityResult.Fail(new RemoteError(
                RemoteErrorCode.ParseFailed,
                "Remote agent file length is invalid."));
        }

        var hash = await RunAsync(
                executor,
                "sha256sum",
                ["--", path],
                OperationRisk.ReadOnly,
                mutation: false,
                "Unable to calculate remote agent file SHA-256.",
                cancellationToken)
            .ConfigureAwait(false);
        if (!hash.Success)
        {
            return IntegrityResult.Fail(hash.Error);
        }

        var digest = ParseSha256(hash.Output);
        return digest is null
            ? IntegrityResult.Fail(new RemoteError(
                RemoteErrorCode.ParseFailed,
                "Remote agent file SHA-256 output is invalid."))
            : IntegrityResult.Ok(new RemoteFileIntegrity(length, digest));
    }

    private async Task<AgentLifecycleExecutionResult> RollBackAsync(
        ServerProfile profile,
        IRemoteCommandExecutor executor,
        Guid planId,
        AgentReleaseVersion previousVersion,
        RemoteFileIntegrity previousIntegrity,
        string reason,
        CancellationToken cancellationToken)
    {
        var restore = await RunAsync(
                executor,
                "sudo",
                [
                    "-n", "install", "-m", "0755", "--",
                    AgentLifecycleRemoteLayout.RollbackBinaryPath,
                    AgentLifecyclePlanner.BinaryPath,
                ],
                OperationRisk.Destructive,
                mutation: true,
                "Could not restore the bounded previous agent binary.",
                cancellationToken)
            .ConfigureAwait(false);
        if (!restore.Success)
        {
            return AmbiguousResult($"{reason} Rollback could not be verified; manual status inspection is required.");
        }

        var exactRestore = await VerifyRemoteFileAsync(
                executor,
                AgentLifecyclePlanner.BinaryPath,
                previousIntegrity.Length,
                previousIntegrity.Sha256,
                cancellationToken)
            .ConfigureAwait(false);
        if (!exactRestore.Success)
        {
            return AmbiguousResult($"{reason} The rollback binary was copied but exact previous integrity could not be verified.");
        }

        var restart = await _serviceManager.ExecuteAsync(
                profile,
                AgentLifecyclePlanner.ServiceUnit,
                ServerServiceAction.Restart,
                cancellationToken)
            .ConfigureAwait(false);
        if (!restart.IsSuccess)
        {
            return AmbiguousResult($"{reason} The previous binary was restored, but its service restart could not be verified.");
        }

        var restored = await GetStatusAsync(profile, cancellationToken).ConfigureAwait(false);
        if (!Matches(restored, previousVersion))
        {
            return new AgentLifecycleExecutionResult(
                AgentLifecycleExecutionState.Ambiguous,
                $"{reason} Rollback ran, but previous healthy version {previousVersion} could not be verified.",
                new RemoteError(RemoteErrorCode.AmbiguousState, "Agent rollback verification failed."),
                restored);
        }

        var cleanupRollback = await RunAsync(
                executor,
                "sudo",
                ["-n", "rm", "-f", "--", AgentLifecycleRemoteLayout.RollbackBinaryPath],
                OperationRisk.Mutating,
                mutation: true,
                "Could not remove the rollback binary after successful rollback.",
                cancellationToken)
            .ConfigureAwait(false);
        var cleanupStage = await CleanupStageAsync(executor, planId, cancellationToken).ConfigureAwait(false);
        if (!cleanupRollback.Success || !cleanupStage.Success)
        {
            return new AgentLifecycleExecutionResult(
                AgentLifecycleExecutionState.Ambiguous,
                $"{reason} Previous version {previousVersion} was restored, but cleanup could not be verified.",
                new RemoteError(RemoteErrorCode.AmbiguousState, "Rollback cleanup could not be verified."),
                restored);
        }

        return new AgentLifecycleExecutionResult(
            AgentLifecycleExecutionState.RolledBack,
            $"{reason} ServerDesk restored and verified previous agent version {previousVersion}.",
            new RemoteError(RemoteErrorCode.CommandFailed, "Agent update failed and was rolled back."),
            restored);
    }

    private Task<StepResult> CleanupStageAsync(
        IRemoteCommandExecutor executor,
        Guid planId,
        CancellationToken cancellationToken) =>
        RunAsync(
            executor,
            "sudo",
            ["-n", "rm", "-rf", "--", AgentLifecycleRemoteLayout.GetStagingDirectory(planId).Value],
            OperationRisk.Mutating,
            mutation: true,
            "Could not remove the bounded agent staging directory.",
            cancellationToken);

    private async Task CleanupStageBestEffortAsync(IRemoteCommandExecutor executor, Guid planId)
    {
        try
        {
            _ = await CleanupStageAsync(executor, planId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
        }
    }

    private async Task<StepResult> RunAsync(
        IRemoteCommandExecutor executor,
        string executable,
        IReadOnlyList<string> arguments,
        OperationRisk risk,
        bool mutation,
        string failureMessage,
        CancellationToken cancellationToken)
    {
        var execution = await executor.ExecuteAsync(
                new RemoteCommandSpec(
                    executable,
                    arguments,
                    CommandTimeout,
                    risk,
                    StableEnvironment),
                cancellationToken)
            .ConfigureAwait(false);
        if (execution.Error is not null)
        {
            if (mutation && IsAmbiguousCode(execution.Error.Code))
            {
                return StepResult.AmbiguousFailure(new RemoteError(
                    RemoteErrorCode.AmbiguousState,
                    $"{failureMessage} ServerDesk lost a reliable completion signal; refresh before retrying."));
            }

            return StepResult.Fail(new RemoteError(execution.Error.Code, failureMessage));
        }

        var command = execution.Command!;
        if (command.ExitCode != 0)
        {
            var code = IsSudoFailure(command.StandardError)
                ? RemoteErrorCode.SudoRequired
                : RemoteErrorCode.CommandFailed;
            return StepResult.Fail(new RemoteError(code, failureMessage));
        }

        return StepResult.Ok(command.StandardOutput);
    }

    private static StepResult MapServiceFailure(ServerServiceActionResult result, string message)
    {
        var code = result.Error?.Code ?? RemoteErrorCode.CommandFailed;
        return IsAmbiguousCode(code)
            ? StepResult.AmbiguousFailure(new RemoteError(
                RemoteErrorCode.AmbiguousState,
                $"{message} ServerDesk cannot prove the resulting service state; refresh before retrying."))
            : StepResult.Fail(new RemoteError(code, message));
    }

    private static AgentLifecycleExecutionResult VerificationFailure(
        AgentLifecycleStatus status,
        string message) =>
        new(
            IsUncertain(status) ? AgentLifecycleExecutionState.Ambiguous : AgentLifecycleExecutionState.Failed,
            message + (IsUncertain(status) ? " Do not retry until status is refreshed." : string.Empty),
            new RemoteError(
                IsUncertain(status) ? RemoteErrorCode.AmbiguousState : RemoteErrorCode.CommandFailed,
                "Agent lifecycle post-mutation verification failed."),
            status);

    private static AgentLifecycleExecutionResult Result(
        StepResult step,
        AgentLifecycleStatus? status = null) =>
        new(
            step.IsAmbiguous ? AgentLifecycleExecutionState.Ambiguous : AgentLifecycleExecutionState.Failed,
            step.Error?.Message ?? "Agent lifecycle step failed.",
            step.Error,
            status);

    private static AgentLifecycleExecutionResult Failure(
        RemoteErrorCode code,
        string message,
        AgentLifecycleStatus? status = null) =>
        new(
            AgentLifecycleExecutionState.Failed,
            message,
            new RemoteError(code, message),
            status);

    private static AgentLifecycleExecutionResult AmbiguousResult(string message) =>
        new(
            AgentLifecycleExecutionState.Ambiguous,
            message,
            new RemoteError(RemoteErrorCode.AmbiguousState, message));

    private static AgentLifecycleStatus Status(
        AgentLifecycleStatusKind kind,
        ServerServiceInfo unit,
        AgentReleaseVersion? version,
        AgentConnectionState? connectionState,
        string message) =>
        new(
            kind,
            version,
            connectionState,
            message,
            unit.ActiveState,
            unit.EnabledState);

    private static bool Matches(AgentLifecycleStatus status, AgentReleaseVersion version) =>
        status.IsHealthy && status.Version == version;

    private static bool IsUncertain(AgentLifecycleStatus status) =>
        status.Kind is AgentLifecycleStatusKind.Unreachable or AgentLifecycleStatusKind.Ambiguous;

    private static bool IsEnabled(string state) =>
        state is "enabled" or "enabled-runtime" or "linked" or "linked-runtime";

    private static bool IsAmbiguousCode(RemoteErrorCode code) =>
        code is RemoteErrorCode.NetworkInterrupted or
            RemoteErrorCode.CommandTimeout or
            RemoteErrorCode.OperationCancelled or
            RemoteErrorCode.AmbiguousState;

    private static bool IsSudoFailure(string text) =>
        !string.IsNullOrWhiteSpace(text) &&
        (text.Contains("password", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("sudoers", StringComparison.OrdinalIgnoreCase) ||
         text.Contains("not allowed", StringComparison.OrdinalIgnoreCase));

    private static string? ParseSha256(string output)
    {
        var token = output.Trim()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        return token is not null &&
               token.Length == 64 &&
               token.All(character => char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character))
            ? token
            : null;
    }

    private static IReadOnlyList<string> RemovalVerificationPaths() =>
    [
        AgentLifecyclePlanner.ServiceUnitPath,
        AgentLifecyclePlanner.BinaryPath,
        AgentLifecycleRemoteLayout.RollbackBinaryPath,
        AgentLifecyclePlanner.StateDirectory,
        AgentLifecyclePlanner.CacheDirectory,
    ];

    private sealed record RemoteFileIntegrity(long Length, string Sha256);

    private sealed record IntegrityResult(bool Success, RemoteFileIntegrity? Integrity, RemoteError? Error)
    {
        public static IntegrityResult Ok(RemoteFileIntegrity integrity) => new(true, integrity, null);

        public static IntegrityResult Fail(RemoteError? error) =>
            new(
                false,
                null,
                error ?? new RemoteError(RemoteErrorCode.CommandFailed, "Remote file integrity could not be established."));
    }

    private sealed record StepResult(bool Success, bool IsAmbiguous, RemoteError? Error, string Output)
    {
        public static StepResult Ok(string output = "") => new(true, false, null, output);

        public static StepResult Fail(RemoteError error) => new(false, false, error, string.Empty);

        public static StepResult AmbiguousFailure(RemoteError error) => new(false, true, error, string.Empty);
    }
}
