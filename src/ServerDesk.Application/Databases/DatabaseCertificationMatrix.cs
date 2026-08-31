namespace ServerDesk.Application.Databases;

public enum DatabaseCertificationLevel
{
    Certified,
    Tested,
    Unsupported,
}

public enum DatabaseCapabilityKind
{
    RuntimeInventory,
    SshTunneledConnectivity,
    Diagnostics,
    Backup,
    Restore,
}

public sealed record DatabaseCapabilityCertification(
    DatabaseCapabilityKind Capability,
    DatabaseCertificationLevel Level,
    string Evidence);

public sealed record DatabaseEngineCertification(
    DatabaseEngineKind Engine,
    string Version,
    IReadOnlyList<DatabaseCapabilityCertification> Capabilities);

public static class DatabaseCertificationMatrix
{
    public static IReadOnlyList<DatabaseEngineCertification> Entries { get; } =
    [
        CertifiedFullPath(DatabaseEngineKind.PostgreSql, "18.6", "postgres:18.6 OpenSSH CI fixture"),
        CertifiedFullPath(DatabaseEngineKind.MySql, "8.4.11", "mysql:8.4.11 OpenSSH CI fixture"),
        CertifiedFullPath(DatabaseEngineKind.MariaDb, "11.8.9", "mariadb:11.8.9 OpenSSH CI fixture"),
        new DatabaseEngineCertification(
            DatabaseEngineKind.Redis,
            "8.10.0",
            [
                Certified(DatabaseCapabilityKind.RuntimeInventory, "redis:8.10.0 OpenSSH CI fixture"),
                Certified(DatabaseCapabilityKind.SshTunneledConnectivity, "redis:8.10.0 OpenSSH CI fixture"),
                Certified(DatabaseCapabilityKind.Diagnostics, "redis:8.10.0 OpenSSH CI fixture"),
                Unsupported(DatabaseCapabilityKind.Backup, "Deterministic persistence-copy semantics are not certified."),
                Unsupported(DatabaseCapabilityKind.Restore, "Deterministic persistence recovery semantics are not certified."),
            ]),
        CertifiedFullPath(
            DatabaseEngineKind.SqlServer,
            "17.0.4075.5",
            "Microsoft SQL Server 2025 CU8 (17.0.4075.5) real OpenSSH CI fixture"),
        CertifiedFullPath(
            DatabaseEngineKind.MongoDb,
            "8.0.29",
            "mongo:8.0.29 standalone OpenSSH CI fixture; backup/restore additionally require exact standalone topology"),
    ];

    public static DatabaseCertificationLevel LevelFor(
        DatabaseEngineKind engine,
        string version,
        DatabaseCapabilityKind capability)
    {
        var entry = Entries.FirstOrDefault(candidate =>
            candidate.Engine == engine && string.Equals(candidate.Version, version, StringComparison.Ordinal));
        return entry?.Capabilities.FirstOrDefault(candidate => candidate.Capability == capability)?.Level
            ?? DatabaseCertificationLevel.Unsupported;
    }

    private static DatabaseEngineCertification CertifiedFullPath(
        DatabaseEngineKind engine,
        string version,
        string evidence) =>
        new(
            engine,
            version,
            [
                Certified(DatabaseCapabilityKind.RuntimeInventory, evidence),
                Certified(DatabaseCapabilityKind.SshTunneledConnectivity, evidence),
                Certified(DatabaseCapabilityKind.Diagnostics, evidence),
                Certified(DatabaseCapabilityKind.Backup, evidence),
                Certified(DatabaseCapabilityKind.Restore, evidence),
            ]);

    private static DatabaseCapabilityCertification Certified(DatabaseCapabilityKind capability, string evidence) =>
        new(capability, DatabaseCertificationLevel.Certified, evidence);

    private static DatabaseCapabilityCertification Unsupported(DatabaseCapabilityKind capability, string evidence) =>
        new(capability, DatabaseCertificationLevel.Unsupported, evidence);
}
