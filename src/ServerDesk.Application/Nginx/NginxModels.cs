using ServerDesk.Domain.Errors;

namespace ServerDesk.Application.Nginx;

public enum NginxRuntimeState
{
    Available,
    CliMissing,
    PermissionDenied,
    InvalidConfiguration,
    ProbeFailed,
}

public sealed record NginxSiteInfo(
    string Id,
    string SourcePath,
    int SourceOrdinal,
    IReadOnlyList<string> ServerNames,
    IReadOnlyList<string> ListenEndpoints,
    IReadOnlyList<string> ProxyTargets,
    IReadOnlyList<string> CertificatePaths,
    IReadOnlyList<string> CertificateKeyPaths,
    string RawBlock)
{
    public string DisplayName => ServerNames.FirstOrDefault() ?? $"server #{SourceOrdinal + 1}";

    public bool UsesTls => CertificatePaths.Count > 0 || ListenEndpoints.Any(value => value.Contains("ssl", StringComparison.OrdinalIgnoreCase));
}

public sealed record NginxConfigSource(
    string Path,
    string RawContent,
    int ServerBlockCount);

public sealed record NginxInventorySnapshot(
    NginxRuntimeState RuntimeState,
    string? Version,
    IReadOnlyList<NginxSiteInfo> Sites,
    IReadOnlyList<NginxConfigSource> Sources,
    string RawDump,
    string? RuntimeDetail = null);

public sealed record NginxInventoryResult(
    NginxInventorySnapshot? Snapshot,
    RemoteError? Error)
{
    public bool IsSuccess => Snapshot is not null && Error is null;
}

public sealed record NginxInventoryOptions(
    TimeSpan CommandTimeout,
    int MaximumDumpBytes,
    int MaximumSites,
    int MaximumSources)
{
    public static NginxInventoryOptions Default { get; } = new(
        TimeSpan.FromSeconds(20),
        2 * 1024 * 1024,
        256,
        256);
}
