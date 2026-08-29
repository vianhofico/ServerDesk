using ServerDesk.Domain.Errors;

namespace ServerDesk.Application.Firewall;

public enum FirewallAdapterKind
{
    None,
    Ufw,
    Firewalld,
}

public enum FirewallRuntimeStatus
{
    Available,
    Disabled,
    CliUnavailable,
    PermissionDenied,
    ProbeFailed,
    AdapterConflict,
    Unknown,
}

public enum FirewallRuleDirection
{
    Inbound,
    Outbound,
    Any,
    Unknown,
}

public enum FirewallRuleAction
{
    Allow,
    Deny,
    Reject,
    Limit,
    Unknown,
}

public sealed record FirewallRuleInfo(
    string Id,
    FirewallAdapterKind Adapter,
    string? Zone,
    FirewallRuleAction Action,
    FirewallRuleDirection Direction,
    string Protocol,
    string PortOrService,
    string Source,
    string Destination,
    string Raw);

public sealed record FirewallAdapterObservation(
    FirewallAdapterKind Adapter,
    bool CliAvailable,
    bool IsActive,
    bool PermissionDenied,
    string? Version,
    string Detail,
    IReadOnlyList<FirewallRuleInfo> Rules,
    string RawOutput);

public sealed record FirewallInventorySnapshot(
    FirewallRuntimeStatus Status,
    FirewallAdapterKind ActiveAdapter,
    IReadOnlyList<FirewallRuleInfo> Rules,
    IReadOnlyList<FirewallAdapterObservation> Adapters,
    string Detail)
{
    public bool IsUsable => Status is FirewallRuntimeStatus.Available or FirewallRuntimeStatus.Disabled;
}

public sealed record FirewallInventoryResult(
    FirewallInventorySnapshot? Snapshot,
    RemoteError? Error)
{
    public bool IsSuccess => Snapshot is not null && Error is null;
}

public sealed record FirewallInventoryOptions(
    TimeSpan CommandTimeout,
    int MaximumRules,
    int MaximumOutputBytes)
{
    public static FirewallInventoryOptions Default { get; } = new(
        TimeSpan.FromSeconds(20),
        1024,
        1024 * 1024);

    public void Validate()
    {
        if (CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(CommandTimeout));
        }

        if (MaximumRules is <= 0 or > 10000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumRules));
        }

        if (MaximumOutputBytes is <= 0 or > 16 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumOutputBytes));
        }
    }
}
