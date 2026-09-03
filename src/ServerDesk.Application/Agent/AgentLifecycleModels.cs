using System.Security.Cryptography;
using System.Text;
using ServerDesk.Application.RemoteFiles;
using ServerDesk.Domain.Errors;
using ServerDesk.Domain.Servers;

namespace ServerDesk.Application.Agent;

public enum AgentLifecycleExecutionState
{
    Succeeded,
    Failed,
    Ambiguous,
    RolledBack,
}

public enum AgentLifecycleStatusKind
{
    Absent,
    Healthy,
    Degraded,
    Incompatible,
    Unreachable,
    Ambiguous,
}

public sealed record AgentLifecycleStatus(
    AgentLifecycleStatusKind Kind,
    AgentReleaseVersion? Version,
    AgentConnectionState? ConnectionState,
    string Message,
    string? ServiceActiveState = null,
    string? ServiceEnabledState = null)
{
    public bool IsHealthy => Kind == AgentLifecycleStatusKind.Healthy;
}

public sealed record AgentLifecycleExecutionResult(
    AgentLifecycleExecutionState State,
    string Message,
    RemoteError? Error = null,
    AgentLifecycleStatus? Status = null)
{
    public bool IsSuccess => State == AgentLifecycleExecutionState.Succeeded;
}

public interface IAgentTransportClientFactory
{
    IAgentTransportClient Create(int localPort);
}

public interface IAgentLifecycleService
{
    Task<AgentLifecycleStatus> GetStatusAsync(
        ServerProfile profile,
        CancellationToken cancellationToken = default);

    Task<AgentLifecycleExecutionResult> InstallAsync(
        ServerProfile profile,
        VerifiedAgentArtifact artifact,
        AgentLifecyclePlan plan,
        CancellationToken cancellationToken = default);

    Task<AgentLifecycleExecutionResult> UpdateAsync(
        ServerProfile profile,
        VerifiedAgentArtifact artifact,
        AgentLifecyclePlan plan,
        CancellationToken cancellationToken = default);

    Task<AgentLifecycleExecutionResult> UninstallAsync(
        ServerProfile profile,
        AgentLifecyclePlan plan,
        CancellationToken cancellationToken = default);
}

public static class AgentLifecycleRemoteLayout
{
    public const int AgentPort = 41371;
    public const string StagingRoot = AgentLifecyclePlanner.CacheDirectory + "/staging";
    public const string RollbackBinaryPath = AgentLifecyclePlanner.BinaryPath + ".previous";

    public static RemotePath GetStagingDirectory(Guid planId)
    {
        if (planId == Guid.Empty)
        {
            throw new ArgumentException("Agent lifecycle plan id must be non-empty.", nameof(planId));
        }

        return RemotePath.Parse($"{StagingRoot}/{planId:N}");
    }

    public static RemotePath GetStagedBinary(Guid planId) =>
        GetStagingDirectory(planId).Combine("agent");

    public static RemotePath GetStagedUnit(Guid planId) =>
        GetStagingDirectory(planId).Combine("serverdesk-agent.service");
}

public static class AgentSystemdUnitDefinition
{
    public const string Content =
        "[Unit]\n" +
        "Description=ServerDesk optional realtime agent\n" +
        "After=network.target\n" +
        "\n" +
        "[Service]\n" +
        "Type=simple\n" +
        "ExecStart=/opt/serverdesk-agent/serverdesk-agent\n" +
        "Environment=SERVERDESK_AGENT_PORT=41371\n" +
        "WorkingDirectory=/var/lib/serverdesk-agent\n" +
        "DynamicUser=yes\n" +
        "StateDirectory=serverdesk-agent\n" +
        "CacheDirectory=serverdesk-agent\n" +
        "UMask=0077\n" +
        "NoNewPrivileges=true\n" +
        "PrivateTmp=true\n" +
        "ProtectSystem=strict\n" +
        "ProtectHome=true\n" +
        "RestrictSUIDSGID=true\n" +
        "LockPersonality=true\n" +
        "Restart=on-failure\n" +
        "RestartSec=5s\n" +
        "\n" +
        "[Install]\n" +
        "WantedBy=multi-user.target\n";

    internal static byte[] Bytes { get; } = Encoding.UTF8.GetBytes(Content);

    internal static string Sha256 { get; } = Convert.ToHexStringLower(SHA256.HashData(Bytes));
}
